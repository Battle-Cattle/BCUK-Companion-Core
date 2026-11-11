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
  loopback flow, token storage abstraction, SSE event stream client,
  event-action dispatch/config persistence, and GitHub Releases update
  checking (`Updates/`).
  Builds and tests fine on Linux/macOS/Windows. Packed and published as
  `BCUKCompanion.Core`.
- `src/BCUKCompanion.TrayApp.Shell` — WPF + WinForms-NotifyIcon tray-app
  shell (login window, settings window, system tray icon, balloon
  notifications), exposed via `CompanionTrayApplication.Run()`.
  **Windows-only**, requires the `net10.0-windows` / WPF workload — see
  below. Packed and published as `BCUKCompanion.TrayApp.Shell`.
- `samples/BCUKCompanion.TrayApp.Sample` — minimal host project showing
  how a per-user app consumes the two packages above (project file +
  `appsettings.json` + one-line `Program.cs`). Uses `ProjectReference`
  to build against this repo's in-progress code; a real per-user repo
  uses `PackageReference` instead.
- `tests/BCUKCompanion.Core.Tests` — xUnit tests for the Core library.
- `tests/BCUKCompanion.TrayApp.Shell.Tests` — xUnit tests for the Shell's non-UI logic
  (event handling and acknowledgements, settings). Targets `net10.0-windows`, so CI
  runs it in the Windows job.

Multiple per-user apps may run on the same Windows account, so
`BCUKCompanion.TrayApp.Shell` does not hardcode its data folder name,
single-instance mutex name, or Windows startup registry value name —
hosts that need a distinct identity pass a `CompanionTrayAppOptions`
with a unique `DataFolderName` to `CompanionTrayApplication.Run()`.

## Installing the .NET SDK in a Linux sandbox/container

The "Cloud with Network" environment's setup script already installs the SDK,
so check `dotnet --version` first and skip this section if it answers.

The official install script (`dotnet-install.sh`, hits
`builds.dotnet.microsoft.com`) is blocked by this environment's egress
policy and will fail with a 403 from the proxy. Don't retry it — use
apt instead, which is allowed and is much faster:

```bash
apt-get update
apt-get install -y dotnet-sdk-10.0
```

This pulls the SDK from Ubuntu's own package mirror (`security.ubuntu.com`
/ `archive.ubuntu.com`), which the proxy permits, and takes well under a
minute versus the multi-step manual installer. Verify with:

```bash
dotnet --version   # expect 10.0.1xx
```

### Building/testing the Core library and tests (works on Linux)

```bash
dotnet build src/BCUKCompanion.Core
dotnet test tests/BCUKCompanion.Core.Tests
```

### Building the WPF Shell/Sample on Linux (build yes, run no)

The `Microsoft.NET.Sdk.WindowsDesktop` SDK isn't in the Linux `dotnet-sdk-10.0`
package. `Directory.Build.props` sets `EnableWindowsTargeting`, so the SDK
downloads the Windows reference packs from NuGet instead, and the whole solution
restores and compiles on Linux, analyzers included. GitHub's automatic
dependency submission relies on that too: it restores every project on Linux.

```bash
dotnet build
```

Nothing that targets `net10.0-windows` can *run* on Linux, because the
`Microsoft.WindowsDesktop.App` runtime doesn't exist there. That includes
`tests/BCUKCompanion.TrayApp.Shell.Tests`. Its tests never touch WPF types, so
for a local check you can build it, drop that framework from the test host's
runtimeconfig, and run it on the base runtime:

```bash
dotnet build tests/BCUKCompanion.TrayApp.Shell.Tests
f=$(find tests/BCUKCompanion.TrayApp.Shell.Tests/bin -name '*.Tests.runtimeconfig.json')
python3 - "$f" <<'PY'
import json, sys
p = sys.argv[1]; c = json.load(open(p)); o = c["runtimeOptions"]
o["frameworks"] = [x for x in o.get("frameworks", []) if x["name"] != "Microsoft.WindowsDesktop.App"]
json.dump(c, open(p, "w"), indent=2)
PY
dotnet test tests/BCUKCompanion.TrayApp.Shell.Tests --no-build
```

That's a local shortcut, not proof: CI's Windows job (`.github/workflows/dotnet.yml`)
runs these tests for real, and a test that does need WPF only works there.

## Shared release workflow

`.github/workflows/companion-release.yml` is a reusable (`workflow_call`)
workflow per-user apps call to publish their Windows build to a GitHub
Release. Its output (tag, `<app>-<tag>-win-x64.zip`, `<zip>.sha256`) is the
contract `GitHubReleaseUpdateChecker` and the Shell's updater rely on —
change both together.

The zip is a single-file, self-contained build (just the `.exe` and
`appsettings.json`). The updater only ever copies files over the install
folder; it never deletes files a release no longer ships.

## Publishing packages

`BCUKCompanion.Core` and `BCUKCompanion.TrayApp.Shell` are packed and
pushed to this org's GitHub Packages NuGet feed by the
`publish-packages` job in `.github/workflows/dotnet.yml`, on every push
to `main`. Bump `<Version>` in the relevant `.csproj` before merging a
change you want consumers to be able to pick up — the push step uses
`--skip-duplicate`, so re-publishing an unchanged version number is a
no-op rather than an error.

---

## Code standards

These match `BCUK-Bot-4`'s `CLAUDE.md`, so every BCUK repo follows the same rules.

### Nullable and warnings

`<Nullable>enable</Nullable>` is on in every project. Don't silence a nullable warning with `!` unless a nearby check guarantees the value. When you do, add a comment that names the check. Don't add `#pragma warning disable` or `[SuppressMessage]` to make a build pass. Fix the cause.

### Complexity

Qlty reports function complexity, too many returns, deep nesting, long parameter lists and duplication (see PR Reviews (Qlty) below). Write code that stays under the limits from the start: cognitive complexity ≤ 15, cyclomatic complexity ≤ 12, at most 5 parameters, nesting depth ≤ 3. These are the same limits `BCUK-Bot-4` enforces with ESLint. When a method grows past them, extract helpers or a type.

### Docstrings

- Every type and member must have a `///` XML doc comment (`<summary>`) saying what it does. This covers public APIs, `internal`/`private` helpers, properties, constants and enum values. One line is enough for simple cases.
- Each parameter gets a `<param>` and a member that returns a value gets a `<returns>`, each with text. Say what the value is, not just its name: "Seconds to wait before the next reconnect attempt", not "The delay." For a `Task`-returning method, say what the task completes with. A record's positional parameters are documented with `<param>` on the record.
- When you change a member's signature or behaviour, update its doc comment to match. Stale docs are worse than no docs.
- **Test projects are exempt.** A descriptive test method name documents the behaviour instead. Test-local fakes and helpers don't need doc comments either. Source projects still follow the rule above.
- **Concise lambdas** (LINQ selectors, event handlers wired inline, `Task.Run` bodies) don't need a doc comment. Add a short `//` comment only when the behaviour isn't obvious from the code.
- The build enforces this. `Directory.Build.props` and `.editorconfig` turn StyleCop's SA1600/SA1601/SA1602 (missing docs), SA1611/SA1615 (missing `<param>`/`<returns>`) and SA1614/SA1616 (empty ones) into errors (`stylecop.json` extends them to internal and private members), and a `<param>`, `<paramref>` or `<see cref>` that no longer matches the code fails the build too. Private fields are exempt. Use `/// <inheritdoc/>` on an override that adds nothing to its base's docs.

### Tests

- Every new function or behaviour change in `BCUKCompanion.Core` **must** include or update an xUnit test in `tests/BCUKCompanion.Core.Tests`, mirroring the source folder (e.g. `Actions/` → `Actions/`).
- When modifying existing behaviour, update affected tests before committing. Never leave a passing-but-wrong test.
- A fix for a bug or a review finding gets a test that fails without the fix.
- A Shell change gets its test in `tests/BCUKCompanion.TrayApp.Shell.Tests`. Keep logic out of the windows so it can be tested: `BotEventHandler` takes a "run on the UI thread" delegate rather than a WPF `Dispatcher` for exactly this reason. Logic every companion app needs belongs in Core.
- Windows and dialogs are tested too, on an STA thread through the Shell tests' `WpfTest` (it shows dialogs modally and drives them once loaded). They're `[SkippableFact]`s that call `WindowsOnly.Skip()` first and keep every WPF type inside the lambda passed to `WpfTest.Run`, so they skip on Linux and run in CI's Windows job. A window that opens a dialog or message box takes a seam for it (e.g. `EventActionMappingsWindow.ShowDialogWindow`, `ShowTestResults`, `LoginWindow.OpenBrowser`). The tray app's logic lives in `TrayAppController`, not the `Application` subclass, because only one WPF application can exist per process. Tests that change process-wide state (`AppPaths`, `AutoStartService`) join the `SharedShellState` collection.
- Windows-only tests (DPAPI token storage) are skipped on Linux and run in CI's Windows job. Don't count a Linux run as proof that those passed. Give a Windows-only Core test `[Trait("Platform", "Windows")]` as well as its `Skip.IfNot(OperatingSystem.IsWindows(), ...)`: the Windows job runs only the Core tests with that trait, since the Linux job runs the rest. A test that runs on both platforms but must also run on Windows (because a Windows-only case, such as a junction, is what it guards) gets the trait without the skip, and says why in a comment.

## ALWAYS: Before committing

Build the whole solution, then run only the tests for what you changed. CI (`.github/workflows/dotnet.yml`) runs every test on Linux and Windows on each push, so don't repeat its full run locally:

```bash
dotnet build
dotnet test tests/BCUKCompanion.Core.Tests --filter "FullyQualifiedName~BCUKCompanion.Core.Tests.Actions"   # the test namespace or class mirroring what changed
```

The build covers Shell and Sample too (see above), so a missing doc comment or a compile error anywhere fails it. Always run it. **Run the full `dotnet test tests/BCUKCompanion.Core.Tests` instead** when the change touches something many areas use (token storage, the SSE client, config persistence, `Directory.Build.props`, package versions). For a Shell change, run its affected tests with the runtimeconfig shortcut above only if they're plain logic tests. The WPF and Windows-only ones skip on Linux, so CI's Windows job is the only real run for them.

**CI is the gate, not the local run.** A PR isn't done until CI is green on both jobs. If it fails, reproduce the failing test locally where the platform allows, fix it, and show it passing before pushing again.

Bump `<Version>` in the changed package's `.csproj` (see Publishing packages) in the same PR as the change.

## PR Reviews (Qlty)

Qlty's PR comments (code smells such as file/function complexity, too many returns, duplication, plus diff-coverage gaps) are this repo's standards, not optional suggestions. **Never dismiss, defer or resolve a Qlty finding without fixing it**, and never reply that it "stays as is" or belongs in a follow-up. Fix every finding in the same PR, whether it's an inline review comment or a row in the "new issues" or coverage summary:
- **Complexity, returns or structure:** refactor (extract helpers or a module) until the metric is under the threshold, keeping behaviour unchanged and tests green.
- **Uncovered new lines:** add tests that cover them.

After pushing, check that Qlty's next analysis no longer lists the finding.

## PR Reviews (CodeRabbit)

CodeRabbit reviews each push to a non-draft PR (marking a draft ready triggers one by itself). It's rate-limited per developer and **never retries on its own**. Only when a comment says "Review limit reached" with a wait time: wait that long, then post exactly one `@coderabbitai review` (only what changed since the last review), or `@coderabbitai full review` when the last Merge Risk was 🟡 Moderate or higher or the changes since are large. Any other "skipped" reason (draft, no new commits) needs no action.

**Merge Risk:** after every review, read the **Merge Risk** line in CodeRabbit's "📝 Walkthrough" comment, which is rewritten in place each time, not just the inline comments. A caveat under it is a real concern even at 🔵 Low: fix it, or post one PR comment saying why it's acceptable, before treating the PR as mergeable. After pushing a fix, say what the risk was and which commit fixes it in a comment tagging `@coderabbitai`; if the risk was 🟡 Moderate or higher, the re-review must be a `@coderabbitai full review`. Repeat until a review comes back with the risk resolved or explicitly accepted. [BCUK-Bot-4 PR #612](https://github.com/Battle-Cattle/BCUK-Bot-4/pull/612) shows the full pattern. This applies to any PR you're driving to green.

**Don't thrash:** never push while a review is in progress (it fails with "head commit changed"), and never re-trigger while rate-limited or in progress. A rate-limit reply only reports the remaining cooldown.
