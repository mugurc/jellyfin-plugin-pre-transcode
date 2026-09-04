# Jellyfin Pre-Transcode

[![Release](https://img.shields.io/github/v/release/mugurc/jellyfin-plugin-pre-transcode?style=flat-square&color=00A4DC&label=release)](https://github.com/mugurc/jellyfin-plugin-pre-transcode/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/mugurc/jellyfin-plugin-pre-transcode/total?style=flat-square&color=00A4DC&label=downloads)](https://github.com/mugurc/jellyfin-plugin-pre-transcode/releases)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11-00A4DC?style=flat-square&logo=jellyfin&logoColor=white)](https://jellyfin.org)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![License](https://img.shields.io/badge/license-GPLv3-blue?style=flat-square)](LICENSE)

**Transcode it once, in the background, instead of every time somebody presses play.**

Pre-Transcode converts your library to a compatibility baseline you define, so Jellyfin stops
live-transcoding the same files over and over. Nothing is hardcoded — the codec, encoder,
container, preset and tone-map dropdowns are built by probing your server's own `ffmpeg`, so
whatever your build supports is what you can pick.

![The plugin's settings page in the Jellyfin dashboard](/docs/image.png)

### Install

In Jellyfin: **Dashboard → Plugins → Repositories → +**, add this URL, then install
**Pre-Transcode** from the catalogue and restart:

```
https://raw.githubusercontent.com/mugurc/jellyfin-plugin-pre-transcode/main/manifest.json
```

> **Read this first.** The plugin is young — feature-complete and validated end-to-end against
> real ffmpeg on Jellyfin 10.11, but young. Test on a copy of your media, and start with the
> default **Separate directory** output policy, which never touches your originals. The
> **Replace in place** policy deletes the source after the new file is verified.

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
- **Keeps bit depth where it matters.** A 10-bit source is no longer flattened to 8-bit by default: the
  chosen encoder is asked which pixel formats it accepts (they differ — `yuv420p10le` for libx265,
  `p010le` for the NVENC/QSV/AMF families, none at all for `h264_qsv`) and 10-bit is kept for HEVC/AV1/VP9
  targets and for HDR that isn't being tone-mapped. H.264 still gets 8-bit, because its 10-bit profile
  isn't hardware-decodable on most clients. Overridable per profile.
- **Hardware decoding, separately from hardware encoding.** Point `-hwaccel` at your GPU so the *source*
  is decoded there instead of on the CPU — worth a lot on 4K HEVC, where decoding is a large share of the
  work, and useful even when you encode in software. The dropdown lists what your ffmpeg reports
  (`-hwaccels`). Off by default: the list says what the binary was *built* with, not that this machine has
  the device. Frames are copied back to system memory after decoding, so this composes with resolution
  caps and tone-mapping rather than excluding them.
- **A rules engine** that decides *when* a file should be queued: combinable conditions
  (video codec, resolution, bitrate, HDR/Dolby Vision, audio codec/channels, container, …) with
  AND/OR inside a rule and OR across rules.
- **Per-library overrides** — Movies, TV and Home Videos can each use a different profile and rules.
- **A persistent job queue** that survives restarts, deduplicates, and supports cancel / requeue /
  pause from a live status page with progress bars.
- **Safety first.** Each encode is written to a temp file and **verified** (non-empty,
  ffprobe-parseable, duration within tolerance) before any output policy is applied. The default
  policy never modifies your originals. Files still being written (active downloads) are skipped.
- **Keeps every track.** All audio tracks (every language) are carried across — a track already in the
  target codec is copied verbatim (no quality loss) **when the output container can store it**, the rest
  are re-encoded, and each is downmixed only if it individually exceeds the channel cap. A codec the
  container has no tag for (TrueHD into mp4; anything but Opus/Vorbis into webm) is re-encoded rather than
  copied, because a muxer handed one rejects the output header and fails the whole job. For a **Matroska
  (mkv)** output, all subtitle tracks and embedded fonts (for ASS/SSA) are copied losslessly too.
  *(MP4/MOV can only hold `mov_text`, so choose an mkv container in your profile if you want subtitles
  preserved.)*
- **Idempotent.** Files already compliant with the target profile are detected and skipped cheaply.
  That check compares codec, container, resolution, audio and HDR — **not** file size or bitrate — so a
  profile built to *shrink* material that already carries the target codec should switch off
  **"Skip files already matching this profile"**, or its trigger rules will match and then be vetoed.
- **Explains itself.** Turn on **"Explain every decision in the log"** and each item logs what ffprobe
  found, every rule condition with the value it was compared against and whether it passed, and the
  reason anything was skipped.
- **Feeds itself.** A post-scan hook and an item-added monitor queue new items automatically (opt-in).
  A freshly-added file that is still inside its stability window (an in-progress copy/download) is
  **deferred and re-checked until it settles** rather than skipped, so a new movie is picked up shortly
  after it lands instead of waiting for the next sweep. A **"Pre-Transcode: sweep library"** scheduled
  task (daily by default) re-checks everything as a backstop.

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

The three steps at the top of this page. Installing from the repository rather than side-loading
also lets Jellyfin show the plugin's details and offer updates.

### Option B — manual

Build (see below) and copy the DLL into a subfolder of your Jellyfin **plugins** directory. Name the
folder `Pre-Transcode_<version>`, matching the version you built — the examples below use `0.8.0.0`:

- **Windows** (native): `C:\ProgramData\Jellyfin\Server\plugins\Pre-Transcode_0.8.0.0\`
- **linuxserver.io Docker**: `/config/data/plugins/Pre-Transcode_0.8.0.0/` inside the container.
  Path-agnostic install (works under Runtipi etc.):

  ```bash
  CID=jellyfin       # your container name (docker ps)
  VER=0.8.0.0        # the version you built
  docker exec "$CID" mkdir -p "/config/data/plugins/Pre-Transcode_$VER"
  docker cp Jellyfin.Plugin.PreTranscode.dll "$CID":"/config/data/plugins/Pre-Transcode_$VER/"
  docker restart "$CID"
  ```

Then open **Dashboard → Plugins → Pre-Transcode**.

## Configuration

All settings live on the plugin's page (**Dashboard → Plugins → Pre-Transcode**):

- **General** — master enable switch, "queue new items automatically after a scan", max concurrent
  jobs (default 1), **hardware decoding** (off by default; see below), the file-stability window (seconds
  a file must be untouched before it is eligible, to avoid grabbing active downloads), how many finished
  jobs to keep (0 = all), and **"Explain every decision in the log"** (see *Why isn't my rule firing?*
  below). Pausing the queue is remembered across restarts.

  **Hardware decoding** is a server-wide setting, not a per-profile one: it describes the machine, so the
  same GPU decodes the source whichever profile encodes it. Try one job before leaving it on — a method
  your ffmpeg lists but the host cannot open (a cuda-enabled build on a box with no NVIDIA card) makes
  every job fail, with the ffmpeg error visible on the queue page.
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
  extension changed). Reclaims space, irreversible. When the container change renames the file
  (`Movie.mp4` → `Movie.mkv`), the plugin **repoints the Jellyfin item at the new path** and queues a
  re-probe, so the item plays immediately instead of failing with a file-not-found error until the next
  library scan. Note that Jellyfin derives an item's id from its path, so its next full scan still
  re-creates the renamed item — keeping the container unchanged avoids that.
- **Add as alternate version** — writes a companion file next to the original and then **registers it
  as a Jellyfin alternate version** the same way the dashboard's *Merge Versions* does: a database link
  (the source stays the primary version), **not** a filename convention — so your original file keeps
  its name and the two show up as one movie with a version selector. It waits for a library scan to
  index the new file (nudging one if needed), so registration completes shortly after the encode.
  *Best-effort:* if the item cannot be indexed/linked, the companion file is still on disk and can be
  merged manually.

## Usage

1. Configure a profile and at least one **enabled** rule (nothing is processed until a rule is on).
2. Queue work either by enabling **"queue new items automatically"** (post-scan hook + item-added
   monitor) or by running the **Pre-Transcode: sweep library** task (Dashboard → Scheduled Tasks,
   run now or on its daily schedule).
3. Watch progress on the plugin's **Control center** page (Dashboard sidebar → *Pre-Transcode*): the
   queue counts, what is encoding right now with its progress and ETA, the next few jobs, the last few
   that finished, plus the sweep / pause / cancel-all / clear-finished controls and the single-item
   search.
4. **Open full queue** and **Open full history** lead to the job list, which is paged, searchable and
   filtered — the control center never loads more than a screenful, so a library that produces tens of
   thousands of jobs no longer means a page hundreds of screens long.

### Why isn't my rule firing?

Switch on **"Explain every decision in the log"** (General) and run a sweep. Every item then logs the
probed facts, each condition with the value it was compared against, and the reason it was skipped:

```
Pre-Transcode evaluation of /media/Movie.mkv:
  video=hevc 1920x1080 @ unknown (probe reported none) kbps, fps=23.976, audio=aac/2ch,
  container=matroska,webm, duration=118 min, size=4021 MB, hdr=false, dolbyvision=false
  rule 'h265 too big (2hr)' (match ALL) => MATCH
      VideoCodec In 'HEVC' | actual: hevc => PASS
      VideoDurationMinutes GreaterThanOrEqual '100' | actual: 118 => PASS
      FileSizeMb GreaterThanOrEqual '950' | actual: 4021 => PASS
  verdict: at least one rule matched
```

Turn it off again afterwards — it writes a block per library item.

Two traps worth knowing about, both of which this log makes visible:

- **A matched rule can still be vetoed.** If the log says *"a rule matched, but the file is already
  compliant"*, the target profile would change nothing it compares — codec, container, resolution,
  audio, HDR. **File size and bitrate are not compared.** A profile that exists to shrink material
  already in the target codec must therefore switch off **"Skip files already matching this profile"**.
- **Container and audio conditions ask about the file, not one string.** `Container` is matched against
  what ffprobe actually reports (`matroska,webm` for mkv, `mov,mp4,m4a,3gp,3g2,mj2` for mp4), so typing
  `mkv` or `mp4` works — before 0.7.1 the comparison was literal, so `Container Equals mkv` matched
  nothing and `Container NotEquals mp4` matched *every* mp4. `AudioCodec` and `AudioChannels` are
  answered over **every** audio track: a positive operator means "some track is like this", a negating
  one means "no track is". They used to see only the first track, so a remux whose commentary is muxed
  ahead of the main audio was never queued.
- **An unknown value fails every operator.** A numeric condition whose value the probe could not
  determine is reported as `unknown (probe reported none)` and fails for *any* operator — it is not a
  threshold miss. Matroska, for instance, carries no per-stream video bitrate, so `VideoBitrateKbps`
  conditions cannot be used on mkv sources.

## Building

Requires the **.NET 9 SDK**.

```bash
dotnet build --configuration Release
dotnet test   --configuration Release   # 284 unit + integration tests
```

The plugin DLL is produced at
`Jellyfin.Plugin.PreTranscode/bin/Release/net9.0/Jellyfin.Plugin.PreTranscode.dll`.

To produce an installable, checksummed zip (and the catalog manifest entry), run:

```powershell
./build-plugin.ps1 -Version 0.8.0.0
```

## Roadmap

- [x] Phase 1 — Scaffold: plugin loads, config page.
- [x] Phase 2 — Configuration schema + dynamic, ffmpeg-driven dashboard UI (unit-tested parser).
- [x] Phase 3 — Rule-evaluation engine (pure, unit-tested).
- [x] Phase 4 — ffmpeg command builder (pure, unit-tested).
- [x] Phase 5 — Persistent job queue + worker (temp-write, verify, apply output policy). Validated
  end-to-end against real ffmpeg (HEVC/MKV → H.264/MP4, source preserved).
- [x] Phase 6 — Library-scan hook + item-added monitor + scheduled sweep task, per-library rules.
- [x] Multi-track audio preservation (all languages; copy-if-compatible) and lossless subtitle +
  font passthrough for Matroska outputs.
- [x] Reliable automatic queueing of new items: files still being copied/downloaded are deferred and
  re-checked until they settle rather than skipped until the next sweep; config no longer duplicates
  presets/rules across restarts.
- [x] Idempotent sweeps: a source whose expected output already exists on disk is never re-transcoded,
  and one that keeps failing is no longer auto-retried indefinitely.
- [x] Per-container subtitle negotiation: tracks the output container can store are copied verbatim,
  the rest converted (an mp4 source's `mov_text` → srt), so mp4 sources transcode to Matroska
  successfully and keep their subtitles.
- [x] Per-container audio negotiation: the same treatment for audio. A track the output container
  cannot store is re-encoded instead of copied, and a profile whose target audio codec the container
  cannot hold falls back to one it can — so "copy audio into mp4" no longer dies on a TrueHD track.
- [x] Hardware-accelerated decoding (`-hwaccel`), chosen from what the server's ffmpeg reports and
  independent of the encoder the profile picks.
- [ ] Future — external subtitle extraction, distributed/off-box encoding.

## License

[GPLv3](LICENSE). This plugin links against Jellyfin's GPLv3 packages and is therefore
distributed under the same license.
