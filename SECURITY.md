# Security policy

## Supported versions

Only the **latest release** is supported. Fixes ship in a new version rather than as patches to
older ones; the version list is in
[`manifest.json`](manifest.json) and on the
[releases page](https://github.com/mugurc/jellyfin-plugin-pre-transcode/releases).

The plugin targets the Jellyfin **10.11.x** plugin ABI.

## Reporting a vulnerability

**Do not open a public issue.** Report privately through GitHub:

1. Go to [Security → Report a vulnerability](https://github.com/mugurc/jellyfin-plugin-pre-transcode/security/advisories/new).
2. Include the plugin and Jellyfin versions, what an attacker can do, and the steps or configuration
   needed to reproduce it.

You will get an acknowledgement as promptly as a one-maintainer project allows, and credit in the
release notes unless you prefer otherwise. Please give a reasonable window for a fix before
disclosing publicly.

## What is in scope

This plugin runs inside the Jellyfin server process, exposes an admin-only API under
`/PreTranscode`, executes the server's `ffmpeg`/`ffprobe`, and writes files according to the
configured output policy. In scope, for example:

- An API endpoint reachable **without** Jellyfin's `RequiresElevation` policy, or one that leaks
  information to a non-admin user.
- A path, argument or configuration value that escapes its intended use — command injection into the
  ffmpeg invocation, or a write outside the configured output location.
- Destruction of a source file outside the documented behaviour of the chosen output policy.
- Credentials, tokens or other secrets written to the log.

## What is not in scope

- Anything requiring an already-compromised Jellyfin administrator account: admins can configure
  arbitrary ffmpeg arguments and output directories **by design**.
- Vulnerabilities in Jellyfin itself or in `ffmpeg` — report those to their projects.
- Data loss caused by deliberately choosing the **Replace in place** policy, which is documented as
  irreversible.
- Resource exhaustion from an admin's own settings (concurrency, resolution, encoder choice).
