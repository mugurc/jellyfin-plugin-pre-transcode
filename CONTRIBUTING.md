# Contributing to Pre-Transcode

Contributions are welcome — bug reports, reproductions, documentation fixes and code. This file
describes what the project expects, so a change you spend time on is a change that can be merged.

The short version: **open an issue before writing code**, keep the build green with zero warnings,
prove the change ran, and don't touch the release files.

## Table of contents

- [Ways to contribute](#ways-to-contribute)
- [Before you write code](#before-you-write-code)
- [Development setup](#development-setup)
- [Running against a real Jellyfin server](#running-against-a-real-jellyfin-server)
- [Code style and analyzers](#code-style-and-analyzers)
- [Comments](#comments)
- [Tests](#tests)
- [Commits](#commits)
- [Pull requests](#pull-requests)
- [Releases are maintainer-only](#releases-are-maintainer-only)
- [What is in and out of scope](#what-is-in-and-out-of-scope)
- [Licence](#licence)

## Ways to contribute

- **Report a bug** — [open a bug report](https://github.com/mugurc/jellyfin-plugin-pre-transcode/issues/new?template=bug_report.yml).
  A log excerpt with **"Explain every decision in the log"** turned on is worth more than any
  description; see *Why isn't my rule firing?* in the [README](README.md).
- **Request a feature** — [open a feature request](https://github.com/mugurc/jellyfin-plugin-pre-transcode/issues/new?template=feature_request.yml).
  Describe the problem you hit, not only the solution you have in mind.
- **Ask a question / share a setup** — use
  [Discussions](https://github.com/mugurc/jellyfin-plugin-pre-transcode/discussions). Support
  questions filed as issues will be moved there.
- **Improve the docs** — the README is the manual. Corrections to it are as valuable as code.
- **Send code** — see the rest of this file.

## Before you write code

Open an issue (or comment on an existing one) and say what you intend to change, **before** you
start. This is not bureaucracy — this plugin runs inside the Jellyfin server process and touches
people's media files, so a lot of behaviour is deliberately conservative and some obvious-looking
changes are rejected for reasons that are not visible in the diff. Agreeing on the approach first
saves you from writing a PR that cannot be merged.

Small, self-evident fixes (a typo, an obviously wrong string, a broken link) can go straight to a PR.

## Development setup

Requires the **.NET 9 SDK** (`global.json` pins major version 9 with `rollForward: latestMajor`).
No Jellyfin checkout is needed: the server API comes from the `Jellyfin.Controller` /
`Jellyfin.Model` NuGet packages, pinned to the ABI the plugin targets (**10.11.x**).

```bash
git clone https://github.com/<you>/jellyfin-plugin-pre-transcode.git
cd jellyfin-plugin-pre-transcode

dotnet build --configuration Release      # must be warning-free; warnings are errors
dotnet test  --configuration Release      # unit + integration tests
```

The plugin DLL lands at
`Jellyfin.Plugin.PreTranscode/bin/Release/net9.0/Jellyfin.Plugin.PreTranscode.dll`.

CI (`.github/workflows/build.yaml`) runs exactly those two commands on every push and pull request,
so a PR that does not build or test cleanly locally will not pass there either.

### The three configuration pages are embedded resources

`Configuration/configPage.html`, `queuePage.html` and `jobsPage.html` are compiled **into** the DLL.
Editing one means rebuilding and reinstalling the DLL — there is no way to hot-reload them in a
running server. They are plain ES5-era JavaScript with no build step and no framework: keep them
that way, and follow the existing conventions (element ids equal to the configuration property
names, Jellyfin's `emby-*` custom elements, `fieldDescription` for explanatory text).

## Running against a real Jellyfin server

Any behaviour change should be exercised against a running server, not just unit-tested.

Install the freshly built DLL into a subfolder of your server's `plugins` directory named
`Pre-Transcode_<version>` (see *Installing → manual* in the README for the per-platform paths), then
restart the server. `plugins/<folder>/meta.json` from a release zip is the usual companion file; a
bare DLL also loads, it just reports no version.

If you would rather not touch your own library, run a **throwaway second instance** against separate
directories — this is how the maintainer verifies changes:

```bash
# macOS example; the binary and its flags are the same on Linux
/Applications/Jellyfin.app/Contents/MacOS/jellyfin \
  -d /tmp/jf-test/data -C /tmp/jf-test/cache -c /tmp/jf-test/config -l /tmp/jf-test/logs \
  -w /Applications/Jellyfin.app/Contents/Resources/jellyfin-web \
  --ffmpeg /Applications/Jellyfin.app/Contents/MacOS/ffmpeg --service --nonetchange
```

Complete the startup wizard, add a library of throwaway files generated with ffmpeg
(`ffmpeg -f lavfi -i testsrc=size=1920x1080:rate=24 -t 300 -c:v mpeg4 out.mkv` makes a source that is
not compliant with the default profile, so it actually encodes), drop the DLL into
`/tmp/jf-test/data/plugins/Pre-Transcode_<version>/`, and you have a disposable server you can pause,
restart and break freely.

## Code style and analyzers

`.editorconfig` holds the formatting rules (4-space indent, LF, UTF-8, final newline) — configure
your editor to honour it.

The plugin project deliberately builds with everything turned up:

| Setting | Effect on your PR |
|---|---|
| `TreatWarningsAsErrors` | **Any** warning fails the build. There is no "fix it later". |
| `AnalysisMode=AllEnabledByDefault` | Every Microsoft CA rule is on, plus StyleCop and `SerilogAnalyzer`. |
| `CodeAnalysisRuleSet=jellyfin.ruleset` | The exemptions Jellyfin itself uses. Do not add new ones to silence a finding in your own code. |
| `GenerateDocumentationFile` | Every **public** member needs `///` docs, including `<param>` and `<returns>`. `SA1600` is off, so internal/private members do not. |
| `Nullable=enable` | No `!` to paper over a nullable warning; handle the null. |

Beyond the analyzers:

- **Reuse the existing pattern.** A second convention next to an existing one is worse than an
  imperfect single convention. Look at how neighbouring code solves the same problem first.
- Keep pure logic in a small, dependency-free class (`Jobs/ProcessingWindow.cs`,
  `Library/ItemSearch.cs`, `Jobs/JobQuery.cs`, `Rules/RuleEvaluator.cs`) and keep the Jellyfin-facing
  wiring thin. This is what makes the behaviour testable without a server.
- Log with structured Serilog templates (`"... {Name} ({Path})"`), never string interpolation.
- Do not add a NuGet dependency without agreeing it in the issue first.

## Comments

Read a few files before you write one. Comments in this codebase explain **why** — usually the
failure that produced the code, the race it closes, or the trap the next reader would otherwise walk
into (see the block above `Pause()` in `Jobs/QueueProcessor.cs`). Comments that restate the code are
removed in review. If your change fixes a real-world failure, say what the failure was.

## Tests

`Jellyfin.Plugin.PreTranscode.Tests` is xunit, with `InternalsVisibleTo`, so `internal` types are
testable directly. `using Xunit;` is implicit (see the test csproj). Name tests
`Subject_Behaviour` to match the existing suites, and put a `///` summary on the class saying what
real behaviour or bug it protects.

A test earns its place only if a plausible bug would fail it:

- **Do** test observable behaviour, boundaries, invariants, state transitions, precedence and real
  error paths — a half-open interval's edges, a window that wraps past midnight, a query whose tokens
  come from different fields.
- **Don't** test plumbing: field copies, defaults, forwarding, mock echoes, source text, or
  "it didn't throw". Those pass forever and protect nothing.
- Deterministic and isolated. No wall-clock dependency in the assertions (pass the time in, as
  `ProcessingWindow.IsOpen` does), no reliance on test order, no network.

Integration suites that shell out to a real encoder (`RealFfmpegIntegrationTests`,
`ProcessSuspenderIntegrationTests`, `MediaProberCacheIntegrationTests`) locate a binary via
`FfmpegTestBinaries.Find` and **skip themselves** when the machine has none — which is why CI's green
run is not proof that they executed. Run them locally with an ffmpeg on `PATH` if you touch encoding,
probing or process control.

## Commits

Conventional-commit prefixes, imperative mood, lower case, no trailing period in the subject:

```
feat: confine transcoding to a daily window, search file names
fix: stop the queue's Pause wedging the worker on macOS
test: verify the audio and hwaccel work against real ffmpeg
docs: correct the alternate-version story, refresh paths, drop a dead field
chore: release v0.10.0.0
```

Use the body to say **why**, and what the failure was if it fixes one. Keep unrelated changes in
separate commits. Do not commit build output (`bin/`, `obj/`, `artifacts/`) or editor/OS files —
`.gitignore` covers the usual ones.

## Pull requests

1. Fork, branch from `main`.
2. Make the change, with tests where the [Tests](#tests) bar is met.
3. `dotnet build -c Release` and `dotnet test -c Release` — both clean.
4. Update the README if you changed behaviour, a setting or a default. The README is the manual, so a
   feature that is not in it is half-finished.
5. Open the PR against `main` and fill in the template: what, why, and **how you verified it**.

What reviews look for, in order: correctness and data safety, whether it follows the existing
patterns, whether the verification actually exercised the change, then style. A PR that says
"works on my machine" with no observation of the changed path will be asked for evidence.

For a UI change, include what you saw — a screenshot, or the text the page rendered. For a bug fix,
say what reproduced it before and what happens now.

## Releases are maintainer-only

**Do not touch these in a PR:** `manifest.json`, `build.yaml`, `build-plugin.ps1`, the version
strings in the README, or `Directory.Build.props`. Packaging, checksums, tags and the catalogue
manifest are done in one `chore: release vX.Y.Z.0` commit by the maintainer. A PR that bumps the
version will be asked to drop those files.

## What is in and out of scope

Likely to be accepted: rule conditions, output policies, profile options, page/UX improvements,
better logging and diagnostics, correctness and data-safety fixes, ffmpeg capability handling,
anything that makes a failure explain itself.

Unlikely to be accepted: distributed or off-box encoding, a scheduler more complex than the existing
daily window, a plugin-specific database, a JavaScript build step or UI framework for the
configuration pages, and anything that modifies a user's media outside the documented output
policies.

Unsure? Ask in an issue first. That is always cheaper than writing it.

## Licence

The project is [GPLv3](LICENSE) (it links Jellyfin's GPLv3 packages). By contributing you agree your
contribution is licensed under the same terms. There is no CLA.
