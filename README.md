# BCUK-Companion-Core

Shared .NET implementation of the BCUK Bot "companion app" protocol (see
[`companionappsetupguide.md`](companionappsetupguide.md) for the full
protocol spec). Each Discord user's companion app is its own, separately
built Windows app — this repo publishes the building blocks those
per-user apps depend on as NuGet packages, rather than something each
per-user app forks and edits.

## Solution layout

| Project | What it is |
|---|---|
| `src/BCUKCompanion.Core` | Platform-agnostic library: loopback OAuth login, manual-token support, encrypted token storage, and the reconnecting SSE event client. No UI. Published as the `BCUKCompanion.Core` package. |
| `src/BCUKCompanion.TrayApp.Shell` | Windows tray-app shell built on `Core`: system tray icon, login window (browser OAuth or paste-a-token), settings window (server URL, start-with-Windows), and balloon notifications on redemption. Exposes `CompanionTrayApplication.Run()` as its entry point. Published as the `BCUKCompanion.TrayApp.Shell` package. |
| `samples/BCUKCompanion.TrayApp.Sample` | Minimal reference host showing how a per-user app consumes the two packages above — just a `.csproj`, an `appsettings.json`, and a one-line `Program.cs`. |
| `tests/BCUKCompanion.Core.Tests` | xUnit tests for `Core`. |

Packages are published to a private GitHub Packages NuGet feed on every
push to `main` (see `.github/workflows/dotnet.yml`).

## Building a per-user app

A per-user companion app is its own small repo that depends on the
packages published from here — it does not fork or copy any source from
this repo.

1. Add a `nuget.config` pointing at this repo's GitHub Packages feed,
   with a PAT that has `read:packages` scope:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration>
     <packageSources>
       <add key="bcuk-companion" value="https://nuget.pkg.github.com/Battle-Cattle/index.json" />
     </packageSources>
     <packageSourceCredentials>
       <bcuk-companion>
         <add key="Username" value="YOUR_GITHUB_USERNAME" />
         <add key="ClearTextPassword" value="YOUR_GITHUB_PAT" />
       </bcuk-companion>
     </packageSourceCredentials>
   </configuration>
   ```

2. Create a `WinExe`/`net10.0-windows` project (`UseWPF` and
   `UseWindowsForms` both `true`) that references the two packages:

   ```xml
   <ItemGroup>
     <PackageReference Include="BCUKCompanion.Core" Version="0.12.0" />
     <PackageReference Include="BCUKCompanion.TrayApp.Shell" Version="0.11.0" />
   </ItemGroup>
   ```

3. Add an `appsettings.json` next to the project with that user's
   `botHost`, and a one-line `Program.cs`:

   ```csharp
   using BCUKCompanion.TrayApp;

   internal static class Program
   {
       [STAThread]
       private static void Main() => CompanionTrayApplication.Run();
   }
   ```

4. Set the project's `AssemblyName`/`ApplicationIcon` to brand the build,
   and ship it as its own installer/exe.

### Reacting to bot events and adding your own settings UI

`CompanionTrayAppOptions` lets a companion app plug in its own actions and
settings UI without owning any bot-connection, dispatch or tray-icon code
itself:

```csharp
CompanionTrayApplication.Run(new CompanionTrayAppOptions
{
    CreateEventActionDispatcher = () =>
    {
        var config = myStore.Load();
        return new EventActionDispatcher(() => config.Mappings, () => new MyActionContext(config));
    },
    AdditionalMenuItems = new[]
    {
        new TrayMenuItem("My Settings...", () => ShowMySettingsWindow())
    }
});
```

- `CreateEventActionDispatcher` builds the dispatcher for each bot event (on
  a background thread, so it can load the latest saved config). The shell
  then handles every redemption and advanced command the same way: it runs
  the matched actions, shows a tray balloon for any that fail, and
  acknowledges advanced commands from the result. The app only registers its
  own action kinds (`EventActionTypeRegistry`) and supplies the context they
  need.
- `OnBotEvent` is invoked on the UI thread for every bot event, before any
  actions run. `BotEventArgs.EventName` is a stable identifier for the kind of
  event — branch on it rather than on any display text, since Core may add
  more event kinds over time. `Metadata` carries the event's details:
  - `"redemption.received"` (channel-point redemption): `rewardId`,
    `rewardTitle`, `userLogin`, `userName`, `userInput`, `redeemedAt`.
  - `"activity.received"` (follow/sub/...): `type`, `displayName`,
    `detail`, `occurredAt`.
  - `"command.triggered"` (advanced command): `eventId`, `commandId`,
    `commandName`, `source` (`chat`/`streamdeck`), `userLogin`, `userName`,
    `args`, `pointsSpent`, `triggeredAt`, plus `param.<key>` for each of the
    command's parameters.
- `OnBotEventAsync` is awaited after `OnBotEvent` for every bot event. It's
  only needed by apps that run their own dispatch instead of setting
  `CreateEventActionDispatcher`. In that case, return the result of running
  the event's actions, and the shell acknowledges advanced commands from it:
  anything but every mapped action succeeding (including no mapping, or
  leaving both unset) refunds the viewer.
- `EventActionMapping.TriggerKind` decides whether a mapping matches a
  reward title (`Reward`, the default — configs saved before this field
  existed load as rewards) or an advanced command name (`Command`); the
  mappings window has a selector for it. Actions can read the event being
  dispatched with `context.GetService<BotEventArgs>()`.
- `AdditionalMenuItems` are appended to the shared tray icon's context
  menu (above "Exit"), so a companion app can surface its own settings
  window from the existing tray icon instead of a command-line flag.
- `DisplayName` is the app's name in the tray tooltip, window titles and
  the shell's own balloons and messages (default `"BCUK Companion"`).

### Settings window and action editors

Subclass `EventActionMappingsWindow<TConfig>` (in `BCUKCompanion.TrayApp.Actions`)
for the "Event Mappings" tab, the Test button and saving. For the Add/Edit
Action dialog, override `CreateActionEditors()` to return one
`EventActionEditor` per integration (a category with its own parameter
controls); the shell appends a built-in editor for Core's `DelayAction` and
validates each built action against `BuildContext()`. `BuildFooter()` gives
the standard status-text-plus-Save bar, and `RemoveActions(predicate)` strips
actions from every mapping (e.g. those targeting a deleted device).

Persist the config with a subclass of `EventActionConfigStore<TConfig>`;
override `CreateWhenMissing()` to migrate an older install's settings, and
`OnLoaded(config)` to drop entries a hand-edited file left invalid.

### Self-updating from GitHub Releases

```csharp
Updates = new CompanionUpdateOptions { RepoOwner = "Battle-Cattle", RepoName = "BCUK-Companion-MyApp" },
```

turns on a silent check-and-install at launch (release builds only — a
local build's `0.0.0` version never updates itself) and a "Check for
updates..." tray menu item. Installing downloads the release zip, verifies
it against the release's `.sha256` file when there is one, and swaps the
files in place via a PowerShell script once the app exits, then relaunches it.

Releases must have the shape `.github/workflows/companion-release.yml`
publishes, so call that reusable workflow from the app's own release
workflow (see the comment at the top of that file) rather than copying it.
The app's `.csproj` should set `<Version>0.0.0</Version>`; the workflow
stamps the real version from the tag.

See `samples/BCUKCompanion.TrayApp.Sample` in this repo for a working
example (it uses `ProjectReference` instead of `PackageReference` since
it builds against this repo's in-progress code).

## Building this repo

```bash
dotnet build src/BCUKCompanion.Core                    # cross-platform
dotnet test tests/BCUKCompanion.Core.Tests             # cross-platform
dotnet build src/BCUKCompanion.TrayApp.Shell           # Windows only
dotnet build samples/BCUKCompanion.TrayApp.Sample      # Windows only
```

See [`CLAUDE.md`](CLAUDE.md) for notes on installing the .NET SDK in a
Linux container/sandbox and why the Shell/Sample projects only build on
Windows.
