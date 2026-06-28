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

2. Create a `WinExe`/`net8.0-windows` project (`UseWPF` and
   `UseWindowsForms` both `true`) that references the two packages:

   ```xml
   <ItemGroup>
     <PackageReference Include="BCUKCompanion.Core" Version="0.1.0" />
     <PackageReference Include="BCUKCompanion.TrayApp.Shell" Version="0.1.0" />
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
