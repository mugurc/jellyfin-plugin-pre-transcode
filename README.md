# Jellyfin Pre-Transcode

A Jellyfin plugin that **proactively pre-transcodes** your library media, in the background,
**once**, into a format you define as your *compatibility baseline* — so the server does not
have to live-transcode the same files on **every** playback.

Everything is admin-configurable from the Jellyfin dashboard. **Nothing is hardcoded**: the codec,
encoder, container, preset and tone-map dropdowns are populated by probing your server's actual
`ffmpeg` binary, so any codec your ffmpeg supports shows up automatically.

> **Status:** feature-complete and validated end-to-end against real ffmpeg on Jellyfin 10.11,
> but young. Test on a copy of your media first and start with the default **Separate directory**
> output policy (which never touches your originals).

## Why?

Jellyfin's built-in transcoding is **live** and **per-session**: it happens while you watch,
uses CPU/GPU for the duration of playback, and the result is thrown away afterward. If ten
people play the same incompatible 4K HEVC HDR file, the server transcodes it ten times.

Pre-Transcode does the work **once, ahead of time**, and stores the compatible result. You
trade a one-off background encode (when a file is added, or on a schedule) for never having
to live-transcode that file again.

## Features

- **Fully dynamic, nothing hardcoded.** Target video/audio codec, encoder implementation
  (software or hardware), container, resolution policy, quality mode (CRF *or* bitrate),
  encoder preset, audio downmix and HDR tone-mapping — all admin-configurable, with dropdowns
  populated from what your ffmpeg actually supports (`-codecs`, `-muxers`, per-encoder presets,
  the `tonemap` filter).
- **Reusable encoding profiles** and **editable resolution presets**.
- **A rules engine** that decides *when* a file should be queued: combinable conditions
  (video codec, resolution, bitrate, HDR/Dolby Vision, audio codec/channels, container, …) with
  AND/OR inside a rule and OR across rules.
- **Per-library overrides** — Movies, TV and Home Videos can each use a different profile and rules.
- **A persistent job queue** that survives restarts, deduplicates, and supports cancel / requeue /
  pause from a live status page with progress bars.
- **Safety first.** Each encode is written to a temp file and **verified** (non-empty,
  ffprobe-parseable, duration within tolerance) before any output policy is applied. The default
  policy never modifies your originals. Files still being written (active downloads) are skipped.
- **Idempotent.** Files already compliant with the target profile are detected and skipped cheaply.
- **Feeds itself.** A post-scan hook and an item-added monitor queue new items automatically (opt-in),
  and a **"Pre-Transcode: sweep library"** scheduled task (daily by default) can re-check everything.

## How it differs from Tdarr / Unmanic

Tdarr and Unmanic are excellent, mature, standalone tools and for large or complex libraries
they are probably still the better choice. Pre-Transcode is deliberately narrower:

| | Pre-Transcode | Tdarr | Unmanic |
|---|---|---|---|
| Runs as | A Jellyfin **plugin** (no extra container) | Separate server + node containers | Separate container |
| Config lives in | Jellyfin dashboard | Its own web UI | Its own web UI |
| Library awareness | Native — reads Jellyfin libraries & items directly | Watches folders | Watches folders |
| Multi-node / clustering | No (single server) | Yes | No |
| Plugin/flow ecosystem | No — a focused rules engine | Large plugin/flow marketplace | Plugin system |
| Best for | "I just want my Jellyfin files pre-made compatible, configured in one place" | Large libraries, distributed encoding, complex flows | Home users wanting a standalone watcher |

**Honest tradeoffs:** Pre-Transcode runs inside the Jellyfin server process, so a heavy encode
competes with your server for CPU (mitigated by a default concurrency of 1 and off-peak scheduling).
It has no distributed encoding and a smaller feature surface than Tdarr. If you already run
Tdarr/Unmanic happily, you don't need this. Its value is being **Jellyfin-native**: no extra
containers, library-aware rules, and everything configured from the Jellyfin dashboard.

## Requirements

- Jellyfin **10.11.x** (this build targets `net9.0` / `targetAbi 10.11.0.0`).
- An `ffmpeg`/`ffprobe` binary available to the server. The official and linuxserver.io images
  bundle `jellyfin-ffmpeg`; the plugin uses the encoder path Jellyfin is configured with (and
  derives `ffprobe` from it when the server doesn't report one).

## Installing

### Option A — plugin repository (recommended)

In Jellyfin: **Dashboard → Plugins → Repositories → +**, and add this URL:

```
https://raw.githubusercontent.com/mugurc/jellyfin-plugin-pre-transcode/main/manifest.json
```

Then install **Pre-Transcode** from **Catalog** and restart the server. Installing from the
repository (rather than side-loading) also lets Jellyfin show the plugin's details and offer updates.

### Option B — manual

Build (see below) and copy the DLL into a subfolder of your Jellyfin **plugins** directory:

- **Windows** (native): `C:\ProgramData\Jellyfin\Server\plugins\Pre-Transcode_0.1.0.0\`
- **linuxserver.io Docker**: `/config/data/plugins/Pre-Transcode_0.1.0.0/` inside the container.
  Path-agnostic install (works under Runtipi etc.):

  ```bash
  CID=jellyfin  # your container name (docker ps)
  docker exec "$CID" mkdir -p /config/data/plugins/Pre-Transcode_0.1.0.0
  docker cp Jellyfin.Plugin.PreTranscode.dll "$CID":/config/data/plugins/Pre-Transcode_0.1.0.0/
  docker restart "$CID"
  ```

Then open **Dashboard → Plugins → Pre-Transcode**.

## Configuration

All settings live on the plugin's page (**Dashboard → Plugins → Pre-Transcode**):

- **General** — master enable switch, "queue new items automatically after a scan", max concurrent
  jobs (default 1), and the file-stability window (seconds a file must be untouched before it is
  eligible, to avoid grabbing active downloads).
- **Encoding profiles** — one or more named profiles. Each defines the target video codec + encoder,
  quality (CRF/QP *or* target/max bitrate), preset, resolution policy (unchanged / cap width / cap
  height / cap longest edge / match a preset), audio codec + encoder + bitrate + downmix policy,
  HDR tone-mapping (on/off + algorithm), container, and the **output handling policy**.
- **Resolution presets** — editable named resolutions (2160p/1080p/720p/480p seeded).
- **Global trigger rules** — an item is queued when **any enabled rule** matches. Within a rule,
  conditions are combined by **AND** or **OR**.
- **Per-library overrides** — assign a specific profile and/or rule set to individual libraries.

### Output handling policies

- **Separate directory** *(default, safest)* — writes the result to a chosen output directory (or
  alongside the source with a suffix), leaving originals untouched.
- **Replace in place** — after verification, replaces the original (deleting it if the container/
  extension changed). Reclaims space, irreversible.
- **Add as alternate version** — writes a sibling file next to the original. *Best-effort:* Jellyfin
  has no stable plugin API to register alternate versions, so this simply places a companion file.

## Usage

1. Configure a profile and at least one **enabled** rule (nothing is processed until a rule is on).
2. Queue work either by enabling **"queue new items automatically"** (post-scan hook + item-added
   monitor) or by running the **Pre-Transcode: sweep library** task (Dashboard → Scheduled Tasks,
   run now or on its daily schedule).
3. Watch progress on the plugin's **Queue & status** page: pending / processing (with %) / completed
   / failed (with the ffmpeg error), plus pause, cancel, requeue and clear-finished controls.

## Building

Requires the **.NET 9 SDK**.

```bash
dotnet build --configuration Release
dotnet test   --configuration Release   # 52 unit + integration tests
```

The plugin DLL is produced at
`Jellyfin.Plugin.PreTranscode/bin/Release/net9.0/Jellyfin.Plugin.PreTranscode.dll`.

To produce an installable, checksummed zip (and the catalog manifest entry), run:

```powershell
./build-plugin.ps1 -Version 0.1.0.0
```

## Roadmap

- [x] Phase 1 — Scaffold: plugin loads, config page.
- [x] Phase 2 — Configuration schema + dynamic, ffmpeg-driven dashboard UI (unit-tested parser).
- [x] Phase 3 — Rule-evaluation engine (pure, unit-tested).
- [x] Phase 4 — ffmpeg command builder (pure, unit-tested).
- [x] Phase 5 — Persistent job queue + worker (temp-write, verify, apply output policy). Validated
  end-to-end against real ffmpeg (HEVC/MKV → H.264/MP4, source preserved).
- [x] Phase 6 — Library-scan hook + item-added monitor + scheduled sweep task, per-library rules.
- [ ] Future — richer subtitle handling, multi-track audio, distributed/off-box encoding.

## License

[GPLv3](LICENSE). This plugin links against Jellyfin's GPLv3 packages and is therefore
distributed under the same license.
