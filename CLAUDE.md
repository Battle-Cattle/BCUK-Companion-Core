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
package, so a plain `dotnet build` of `BCUKCompanion.TrayApp.Shell` or the
Sample fails with `MSB4236`. Adding `-p:EnableWindowsTargeting=true` makes the
SDK download the Windows reference packs from NuGet instead, and then the whole
solution compiles on Linux, analyzers included:

```bash
dotnet build -p:EnableWindowsTargeting=true
```

Nothing that targets `net10.0-windows` can *run* on Linux, because the
`Microsoft.WindowsDesktop.App` runtime doesn't exist there. That includes
`tests/BCUKCompanion.TrayApp.Shell.Tests`. Its tests never touch WPF types, so
for a local check you can build it, drop that framework from the test host's
runtimeconfig, and run it on the base runtime:

```bash
dotnet build tests/BCUKCompanion.TrayApp.Shell.Tests -p:EnableWindowsTargeting=true
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
folder, so on launch a single-file build removes whatever an older
multi-file release left behind, using that build's `<app>.deps.json` as the
list (`LegacyInstallCleanup`). The deps.json doesn't list the framework's own
translation folders or an older runtime's `mscordaccore_*.dll`, so those are
removed by shape (culture-named folders of only `*.resources.dll`).

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
- Windows-only tests (DPAPI token storage) are skipped on Linux and run in CI's Windows job. Don't count a Linux run as proof that those passed.

## ALWAYS: Before committing

```bash
dotnet build -p:EnableWindowsTargeting=true && dotnet test tests/BCUKCompanion.Core.Tests
```

The first command builds the whole solution, Shell and Sample included (see above), so a missing doc comment or a compile error anywhere fails it. For a Shell change, also run its tests with the runtimeconfig shortcut above, and let CI's Windows job confirm them before treating the PR as green.

Bump `<Version>` in the changed package's `.csproj` (see Publishing packages) in the same PR as the change.

## PR Reviews (Qlty)

Qlty's PR comments (code smells such as file/function complexity, too many returns, duplication, plus diff-coverage gaps) are this repo's standards, not optional suggestions. **Never dismiss, defer or resolve a Qlty finding without fixing it**, and never reply that it "stays as is" or belongs in a follow-up. Fix every finding in the same PR, whether it's an inline review comment or a row in the "new issues" or coverage summary:
- **Complexity, returns or structure:** refactor (extract helpers or a module) until the metric is under the threshold, keeping behaviour unchanged and tests green.
- **Uncovered new lines:** add tests that cover them.

After pushing, check that Qlty's next analysis no longer lists the finding.

## PR Reviews (CodeRabbit)

CodeRabbit auto-reviews pushes but is rate-limited per developer; a rate-limited push gets a "Review limit reached" comment with a wait time and no actual diff review. **It never auto-retries** once the cooldown passes — a manual trigger is required every time, even if you just wait it out. After the wait time shown in the rate-limit comment has elapsed, post a PR comment:

- **`@coderabbitai review`** — reviews only what changed since the last review. Default choice.
- **`@coderabbitai full review`** — re-reviews the entire PR from scratch. Use this instead when the last review's **Merge Risk** (see below) was 🟡 Moderate or higher, when it surfaced a lot of issues, or when the intervening changes are large.

**The manual comment is ONLY for the rate-limit case above.** Do not post it reactively for other "review skipped" reasons:
- **"Review skipped: Draft detected"** — expected while the PR is a draft. Marking the PR ready for review (`draft: false`) auto-triggers a review on its own; no comment needed. Only manually trigger if, after going ready, no review shows up within a few minutes.
- **"No new commits to review since the last review"** — CodeRabbit is up to date; nothing to do.

If unsure which case a "skipped"/"no review" comment falls into, check whether it names a wait time (rate-limit → wait then trigger) or a different reason (draft/no-new-commits → no action).

**Merge Risk field:** CodeRabbit's main "📝 Walkthrough" comment is a living comment — every review (including the automatic first one) rewrites it in place, and it carries a **Merge Risk** line (e.g. `**Merge Risk:** _🔵 Low_ · up to \`a5ac9\``). Treat this as a required read, not decoration:
- After every review finishes, re-read the walkthrough comment for the current Merge Risk line and any caveat attached to it — don't stop at the inline/actionable comments.
- A caveat under the Merge Risk line is a real concern even when the badge is "Low" — CodeRabbit is flagging it, not blocking on it. Either fix it and push, or post one PR comment explaining why it's acceptable/out of scope, before treating the PR as mergeable.
- When you push a fix for a Merge Risk caveat, say so in a PR comment (what the risk was, which commit fixes it) tagging `@coderabbitai` so it lands in CodeRabbit's own thread and context — e.g. "@coderabbitai — fixed the race described above in `<commit>`...". This gives the next re-review and human reviewers the context, and lets CodeRabbit re-assess the same concern.
- When the Merge Risk was 🟡 Moderate or higher, the re-review after your fix must be a **`@coderabbitai full review`**, not an incremental `@coderabbitai review` — CodeRabbit acknowledging the fix in its thread (or marking the inline comments "Addressed") is not a re-review. If the push was rate-limited, wait out the cooldown and trigger the full review then.
- This can take more than one round: a fix can surface a narrower version of the same risk on re-review. Keep fixing and re-commenting (tagging `@coderabbitai` each time) until a review comes back with the risk resolved or explicitly accepted, not just re-labeled "Low" without comment.
- See BCUK-Bot-4 PR #612 (https://github.com/Battle-Cattle/BCUK-Bot-4/pull/612, "Forward all streamer events to the companion app") for the full pattern: an initial Low-risk walkthrough flagged a token-revocation race → fixed and commented → re-review found a narrower race in the same area → fixed and commented again → final re-review confirmed Low risk with the residual tradeoff explicitly accepted → only then marked ready for merge.
- This applies to any PR you're driving to green, not just ones you opened — a rising or unresolved Merge Risk is a signal to keep working the PR, not to wait on reviewers.

**Avoid re-triggering thrash:**
- **Never push a new commit while a review is in progress** — the in-flight review fails outright ("head commit changed during the review") and wastes the attempt. Wait for the review to finish (or fail/rate-limit) before pushing again.
- **Never re-trigger `@coderabbitai review` while already rate-limited or still in progress.** Each rate-limit reply just reports the currently remaining cooldown (it doesn't reset or extend it) — retrying early is simply wasted, not counterproductive. Wait out the countdown, trigger exactly once, then leave it alone until it either posts real findings or rate-limits again.
