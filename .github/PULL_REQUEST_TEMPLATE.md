<!--
Thanks for the PR. Please read CONTRIBUTING.md if you haven't; the checklist below is what review
looks at. Delete sections that genuinely do not apply.
-->

## What this changes

<!-- One or two sentences. What behaviour is different after this PR? -->

## Why

<!-- The problem being solved. If it fixes a failure, describe the failure: what went wrong, when,
     and what the user saw. Link the issue: "Fixes #123" / "Part of #123". -->

## How I verified it

<!-- Required. Not "should work" — what you ran and what you observed.

     * Bug fix:        how it reproduced before, and what happens now.
     * Behaviour/API:  the command or scenario you exercised and its output.
     * UI change:      what the page rendered (paste the text or attach a screenshot).
     * Encoding path:  the ffmpeg command line produced, and whether the output verified.

     Say if you ran against a real Jellyfin server, and which version. -->

```
dotnet build -c Release   # 0 warnings
dotnet test  -c Release   # N passed
```

## Checklist

- [ ] `dotnet build -c Release` is clean — no new warnings (warnings are errors here)
- [ ] `dotnet test -c Release` passes
- [ ] Tests added/updated where a plausible bug would fail them (see CONTRIBUTING → Tests), or a note below saying why none apply
- [ ] Integration suites that need a real ffmpeg were run locally, if this touches encoding, probing or process control (they self-skip otherwise)
- [ ] Public members carry `///` docs; existing patterns and `.editorconfig` followed
- [ ] README updated if behaviour, a setting or a default changed
- [ ] No release files touched (`manifest.json`, `build.yaml`, `build-plugin.ps1`, `Directory.Build.props`, README version strings)
- [ ] No build output, editor or OS files committed

## Notes for the reviewer

<!-- Anything you are unsure about, a decision you want challenged, or a follow-up you left out on
     purpose. -->
