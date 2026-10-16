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
`780885a1-46b9-4ba0-a3c8-d18ad7fe81b6`) and a one-field config page for the API key.

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

## Left to the builder

- Whether the `ThreeLetterISOLanguageName` should follow Jellyfin's own mapping rather than
  the hardcoded `fas`.
