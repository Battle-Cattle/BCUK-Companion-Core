# CLAUDE.md

Guidance for working in this repo.

## What this repo is

`BCUK-Companion-Core` is the shared .NET implementation of the "companion
app" protocol described in `companionappsetupguide.md` (loopback OAuth
login, manual token entry, secure token storage, and the SSE event
stream). Each user's actual companion app is a separate, individually
built Windows app — this repo publishes the code those per-user apps
depend on as NuGet packages (`BCUKCompanion.Core` and
`BCUKCompanion.TrayApp.Shell`). Per-user apps consume these via
`PackageReference`; they do not fork this repo's source. This repo is
not itself "the bot" (that's the separate BCUK Bot server repo
referenced in the guide).

## Solution layout

- `src/BCUKCompanion.Core` — platform-agnostic class library: OAuth
  loopback flow, token storage abstraction, SSE event stream client.
  Builds and tests fine on Linux/macOS/Windows. Packed and published as
  `BCUKCompanion.Core`.
- `src/BCUKCompanion.TrayApp.Shell` — WPF + WinForms-NotifyIcon tray-app
  shell (login window, settings window, system tray icon, balloon
  notifications), exposed via `CompanionTrayApplication.Run()`.
  **Windows-only**, requires the `net8.0-windows` / WPF workload — see
  below. Packed and published as `BCUKCompanion.TrayApp.Shell`.
- `samples/BCUKCompanion.TrayApp.Sample` — minimal host project showing
  how a per-user app consumes the two packages above (project file +
  `appsettings.json` + one-line `Program.cs`). Uses `ProjectReference`
  to build against this repo's in-progress code; a real per-user repo
  uses `PackageReference` instead.
- `tests/BCUKCompanion.Core.Tests` — xUnit tests for the Core library.

Multiple per-user apps may run on the same Windows account, so
`BCUKCompanion.TrayApp.Shell` does not hardcode its data folder name,
single-instance mutex name, or Windows startup registry value name —
hosts that need a distinct identity pass a `CompanionTrayAppOptions`
with a unique `DataFolderName` to `CompanionTrayApplication.Run()`.

## Installing the .NET SDK in a Linux sandbox/container

The official install script (`dotnet-install.sh`, hits
`builds.dotnet.microsoft.com`) is blocked by this environment's egress
policy and will fail with a 403 from the proxy. Don't retry it — use
apt instead, which is allowed and is much faster:

```bash
apt-get update
apt-get install -y dotnet-sdk-8.0
```

This pulls the SDK from Ubuntu's own package mirror (`security.ubuntu.com`
/ `archive.ubuntu.com`), which the proxy permits, and takes well under a
minute versus the multi-step manual installer. Verify with:

```bash
dotnet --version   # expect 8.0.1xx
```

### Building/testing the Core library and tests (works on Linux)

```bash
dotnet build src/BCUKCompanion.Core
dotnet test tests/BCUKCompanion.Core.Tests
```

### Building the WPF Shell/Sample (does NOT work on Linux)

The `Microsoft.NET.Sdk.WindowsDesktop` MSBuild SDK (which provides the
WPF/WinForms reference assemblies) is only distributed as part of the
Windows .NET SDK installer — it is not restorable from NuGet and is not
included in the Linux `dotnet-sdk-8.0` apt package. Attempting
`dotnet build` on `BCUKCompanion.TrayApp.Shell` or
`BCUKCompanion.TrayApp.Sample` from Linux fails with
`MSB4236: The SDK 'Microsoft.NET.Sdk.WindowsDesktop' ... could not be
found`. This is expected — build/test those projects on a real Windows
machine or a `windows-latest` GitHub Actions runner (see
`.github/workflows/dotnet.yml`). Do not spend time trying to work around
this on Linux.

## Publishing packages

`BCUKCompanion.Core` and `BCUKCompanion.TrayApp.Shell` are packed and
pushed to this org's GitHub Packages NuGet feed by the
`publish-packages` job in `.github/workflows/dotnet.yml`, on every push
to `main`. Bump `<Version>` in the relevant `.csproj` before merging a
change you want consumers to be able to pick up — the push step uses
`--skip-duplicate`, so re-publishing an unchanged version number is a
no-op rather than an error.
