# jellyfin-subdl — design

Date: 2026-10-16

## Intent

Get Persian (FA) subtitles onto movies and episodes automatically, without leaving
Jellyfin to fetch them by hand.

## Goals

- Installable from a custom Jellyfin plugin repository hosted on GitHub.
- Downloads Persian subtitles for new library items.
- Can backfill items already in the library.
- Small enough to read in one sitting.

## Non-goals

- Any language other than Persian. The gate is `fa`/`fas`/`per`/Persian/Farsi; everything
  else returns no results.
- Interactive subtitle search UI of its own. The plugin shows up in Jellyfin's existing
  subtitle search dialog for free.
- Subtitle translation (subdl's AI endpoints are SubDL Pro anyway).
- Any plugin-side scheduling. Jellyfin's own machinery drives it.

## Constraints

- Target server: Jellyfin 12.2 (`targetAbi` 12.2.0.0), so the plugin targets `net10.0` and
  references `Jellyfin.Controller`/`Jellyfin.Model` 12.2.0 with `ExcludeAssets=runtime`.
- subdl free tier: 2,000 searches/day, 50 downloads/day. Failed `format=file` download
  attempts still consume quota, which is why the plugin does not use that mode.
- The subdl API key is per-install and entered on the plugin config page.

## How it works

`Plugin : BasePlugin<PluginConfiguration>, IHasWebPages` provides identity (fixed GUID
`780885a1-46b9-4ba0-a3c8-d18ad7fe81b6`) and a one-field config page for the API key. The page
also shows the account's remaining daily quota, read through the plugin's own
`GET /SubDL/Status` endpoint.

`PluginServiceRegistrator : IPluginServiceRegistrator` is mandatory. Jellyfin 12 does not scan
plugin assemblies for service interfaces, so without this the provider is never constructed and
the plugin silently does nothing while still loading and reporting `Active`.

`SubdlSubtitleProvider : ISubtitleProvider` implements the two methods Jellyfin calls:

- `Search` — returns nothing unless the request is for Persian and an API key is set. Then
  calls `GET /api/v2/files/search?filename=<release filename>&languages=fa&type=movie|tv`,
  keeps results whose `language` is `FA`, and orders them by `match_score`. When Jellyfin's
  *Require a perfect match* option is on, results below 0.8 are dropped. The returned id is
  `{nId}_{language}_{season}_{episode}`.
- `GetSubtitles` — parses that id, fetches
  `GET /api/v2/subtitles/{nId}/download?format=zip`, and picks the entry matching
  `SxxExx`/`xXxx`, falling back to the largest subtitle file. Returns the stream with the
  requested language string so the saved file is named the way Jellyfin will look for it
  next time.

Jellyfin's own *Download missing subtitles* scheduled task and the per-library
*Subtitle download languages* setting provide both the new-item and backfill behaviour, so
the plugin contains no scheduling code.

## Verified against the live API

- `format=file` returns `409 not_a_single_file` for ordinary Persian subtitles and still
  consumes download quota, so the zip path is required.
- `/api/v2/files/search` resolves `Breaking.Bad.S05E14.1080p.BluRay.x264-DEMAND.mkv` to
  S05E14 correctly.
- Persian results for that episode were season packs scoring 0.5 and single episodes
  scoring 0.22–0.4, i.e. below the 0.8 bar, which is why "perfect match" is documented as
  possibly-yields-nothing.
- `/api/v2/subtitles/{nId}/download?format=zip` returns a real zip whose entries include
  the episode files.

## Deliberate calls

- An archive that cannot be tied to an episode by filename falls back to its largest file;
  this can pick the wrong episode on oddly named packs.
- Only one version is kept in `manifest.json`; Jellyfin only needs the newest.
- No unit test project. The risky logic (id round trip, archive entry choice) was exercised
  against the real service instead.

## 1.0.2.0

The plugin page gained a **Run now** button that starts Jellyfin's *Download missing subtitles*
task, the task's state and last result, and a warning listing libraries whose
*Require a perfect subtitle match* setting is on.

That warning is the important part. The plugin maps `IsPerfectMatch` onto subdl's `match_score`
>= 0.8, which the subdl docs call a confident release match. For Persian that bar is brutal: across
14 sample movies only one reached 0.8, because the Persian catalogue is mostly season packs and a
pack's release name never resembles a single episode's filename closely. The flag is kept
faithful to Jellyfin's meaning, so the fix is to turn the library setting off, and the page now says
so rather than silently returning nothing. OpenSubtitles solves the same problem by requiring an
exact file-hash match, which subdl cannot offer.

## 1.1.0.0

Added `SubSourceSubtitleProvider`, a second provider backed by subsource.net, with its own API key
on the config page.

There is deliberately no fallback logic in the plugin. Jellyfin's automated downloader asks
providers in `SubtitleFetcherOrder` and stops at the first that returns anything, which is exactly
"use SubDL, fall through to SubSource when it has nothing or its quota is spent". The plugin
registers SubDL first so that is the default order. A second provider also shows up separately in
Jellyfin's manual search, and can be enabled or disabled on its own.

The SubSource client is ported from Bazarr's `subliminal_patch` provider: title resolution through
`/api/v1/movies/search` (`searchType=imdb` for films, a text search on the series name for
episodes, since an episode's own IMDb id is not the series'), subtitles from `/api/v1/subtitles`
with `language=farsi_persian`, and a zip from `/api/v1/subtitles/{id}/download`. Forced
("foreign parts") entries are filtered out because they only cover the non-English dialogue.

**Verified against the live API, indirectly.** The machine this was written on cannot resolve
subsource.net (it answers with a private address) and connections to its Cloudflare addresses are
reset, so the request shapes came from Bazarr. The first real searches ran on the Jellyfin host
instead, which can reach the API, and they showed that the title search, subtitle list and
title/year matching all work. They also caught one wrong assumption: `subtitleId` is a JSON
**number**, not a string. Bazarr is Python and never noticed; `System.Text.Json` threw
`Cannot get the value of a token type 'Number' as a string` and the provider returned nothing,
which is exactly what the per-provider error logging surfaced. Fixed in 1.1.0.1.

## 1.2.0.0

Added orphaned subtitle archiving: `GET /SubDL/Orphans` lists subtitle files in the library folders
whose video is gone, and `POST /SubDL/Orphans/Archive` moves them to the configured
`ArchivePath`. Both are behind `Policies.SubtitleManagement`, and the archive button only appears
after a scan.

Decisions worth keeping:

- Archive is flat, per the user's choice, with a numeric suffix on name clashes rather than an
  overwrite.
- The move re-scans on the server instead of taking a file list from the client, so a stale or
  hostile request cannot aim the move at an arbitrary file.
- Library locations are collapsed so a nested library is not scanned twice.
- Subtitle-to-video matching peels up to four trailing dot-separated tags rather than carrying a
  language list, which handles `.fa.srt`, `.en.forced.srt` and friends without one.
- The scan does its own directory walk so one unreadable folder cannot abort it, and skips reparse
  points so a symlink loop cannot hang it.
- Empty folders are left alone; only files move.

Verified with a throwaway harness that compiles the archiver source directly and asserts against a
fake tree: the three genuine orphans are found while tagged subtitles, nested libraries, duplicate
locations and non-subtitle files are all handled correctly, and an existing file at the
destination is preserved under a `.1` name.

## Left to the builder

- Whether the `ThreeLetterISOLanguageName` should follow Jellyfin's own mapping rather than
  the hardcoded `fas`.

## 1.0.1.0

1.0.0.0 loaded and reported `Active` but never fetched anything: Jellyfin 12 registers plugin
services only through `IPluginServiceRegistrator`, and the plugin had none. A load test that
builds a `ServiceCollection`, runs every `IPluginServiceRegistrator` in the assembly and resolves
`IEnumerable<ISubtitleProvider>` now guards this. 1.0.1.0 also adds `ILogger` logging on every
search, download and failure, and the account-status endpoint behind the config page.
