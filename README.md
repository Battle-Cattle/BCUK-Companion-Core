# BCUK-Companion-Core

Shared .NET implementation of the BCUK Bot "companion app" protocol (see
[`companionappsetupguide.md`](companionappsetupguide.md) for the full
protocol spec). Each Discord user's companion app is its own, separately
built Windows app — this repo provides the core client library and a
reference tray-app template those per-user apps are built from.

## Solution layout

| Project | What it is |
|---|---|
| `src/BCUKCompanion.Core` | Platform-agnostic library: loopback OAuth login, manual-token support, encrypted token storage, and the reconnecting SSE event client. No UI. |
| `src/BCUKCompanion.TrayApp` | Windows reference app built on `Core`: system tray icon, login window (browser OAuth or paste-a-token), settings window (server URL, start-with-Windows), and balloon notifications on redemption. Runs with no visible window — everything is reached from the tray icon. |
| `tests/BCUKCompanion.Core.Tests` | xUnit tests for `Core`. |

To build a per-user app: fork/clone `BCUKCompanion.TrayApp`, point
`appsettings.json` (`botHost`) and the assembly name/icon at that user's
deployment, and ship it as its own installer/exe.

## Building

```bash
dotnet build src/BCUKCompanion.Core            # cross-platform
dotnet test tests/BCUKCompanion.Core.Tests     # cross-platform
dotnet build src/BCUKCompanion.TrayApp         # Windows only
```

See [`CLAUDE.md`](CLAUDE.md) for notes on installing the .NET SDK in a
Linux container/sandbox and why the TrayApp project only builds on
Windows.
