# CLAUDE.md

Guidance for working in this repo.

## What this repo is

`BCUK-Companion-Core` is the shared .NET implementation of the "companion
app" protocol described in `companionappsetupguide.md` (loopback OAuth
login, manual token entry, secure token storage, and the SSE event
stream). Each user's actual companion app is a separate, individually
built Windows app — this repo is the core library (and a reference tray
app) that those per-user apps are built from. It is not itself "the bot"
(that's the separate BCUK Bot server repo referenced in the guide).

## Solution layout

- `src/BCUKCompanion.Core` — platform-agnostic class library: OAuth
  loopback flow, token storage abstraction, SSE event stream client.
  Builds and tests fine on Linux/macOS/Windows.
- `src/BCUKCompanion.TrayApp` — WPF + WinForms-NotifyIcon reference app
  (login window, settings window, system tray icon, balloon
  notifications). **Windows-only**, requires the `net8.0-windows` /
  WPF workload — see below.
- `tests/BCUKCompanion.Core.Tests` — xUnit tests for the Core library.

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

### Building the WPF TrayApp (does NOT work on Linux)

The `Microsoft.NET.Sdk.WindowsDesktop` MSBuild SDK (which provides the
WPF/WinForms reference assemblies) is only distributed as part of the
Windows .NET SDK installer — it is not restorable from NuGet and is not
included in the Linux `dotnet-sdk-8.0` apt package. Attempting
`dotnet build` on `BCUKCompanion.TrayApp` from Linux fails with
`MSB4236: The SDK 'Microsoft.NET.Sdk.WindowsDesktop' ... could not be
found`. This is expected — build/test that project on a real Windows
machine or a `windows-latest` GitHub Actions runner (see
`.github/workflows/dotnet.yml`). Do not spend time trying to work around
this on Linux.
