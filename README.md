# jellyfin-subdl

A Jellyfin plugin that downloads Persian (FA) subtitles from [subdl.com](https://subdl.com),
with [subsource.net](https://subsource.net) as a second source behind it.

## Installation

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add:

   ```
   https://raw.githubusercontent.com/HosenM/jellyfin-subdl/main/manifest.json
   ```

2. Go to the **Catalog** tab, find **SubDL** and install it.
3. Restart Jellyfin.
4. Open **Dashboard → Plugins → SubDL** and paste your SubDL API key. Get one from your
   [subdl.com](https://subdl.com) account settings. Optionally paste a subsource.net key too —
   see below.

## Two sources

The plugin registers two subtitle providers, `SubDL` and `SubSource`. Jellyfin asks providers in
its *Subtitle fetcher order* and **stops at the first one that returns results**, so SubDL first
with SubSource second gives you fallback for free: SubSource is used when SubDL has no Persian
subtitle for an item, or when its daily quota is spent. Set the order under
**Edit library → Subtitles → Subtitle fetcher order**.

Leave the SubSource key empty to disable that source entirely.

### How the quota hand-over works

subdl gives a free key **2,000 searches** and only **50 downloads** a day, and downloads are the
limit you actually hit. The plugin therefore checks the download quota before offering any subdl
result and, when it is spent, returns nothing so Jellyfin falls through to SubSource. Without that
check the search would still succeed, Jellyfin would pick a subdl subtitle, the download would fail,
and SubSource would never be tried.

The check reads `/api/v2/me`, which does not count against the search quota, and is cached for a
minute. A failure to read it is treated as "quota available" so a hiccup cannot silently disable
the provider. The log says which way it went:

```
[WRN] SubDL: daily download quota is spent, handing 7 result(s) over to the next provider
```

## Automatic subtitle downloads

The plugin registers itself as a subtitle provider, so Jellyfin's existing subtitle
machinery does the work — there is no separate download task in the plugin.

To download subtitles automatically, set Persian as a subtitle download language on the
library (**Edit library → Subtitles → Download languages**) or server-wide under
**Dashboard → Subtitles**. The *Download missing subtitles* scheduled task then fills in
Persian subtitles for existing items, and new items get them as they are scanned. Run that
task manually to backfill a library you already have.

`Require a perfect subtitle match` maps onto subdl's `match_score`: with it enabled only
results scored 0.8 or higher are accepted. That bar is brutal for Persian — the catalogue is
mostly season packs, whose release names never resemble a single episode's filename, and on a
sample of 14 movies only one cleared 0.8. **Turn the setting off** (**Edit library → Subtitles**)
unless you would rather have no subtitle than an imperfect one. The plugin page warns you while
it is on.

The download task runs every 24 hours. Press **Run now** on the plugin page to start it
immediately.

## Checking that it works

The plugin config page (**Dashboard → Plugins → SubDL**) shows your SubDL plan and how many
searches and downloads are left today, the state of the download task, and a **Run now** button
that starts Jellyfin's *Download missing subtitles* task.

Every search, download and failure is logged under the `SubDL` prefix and is visible at
**Dashboard → Logs**. Turn the log level up to `Debug` there for per-item detail. A successful
search logs a line like:

```
[INF] Jellyfin.Plugin.SubDL.Providers.SubdlSubtitleProvider: SubDL: 2 Persian subtitle(s) for Anora.2024.2160p.AMZN.WEB-DL.DDP5.1.H.265-FLUX.mkv
```

## Archiving orphaned subtitles

When you delete a film, rippers and scrapers often leave its subtitle behind. The plugin page has
an **Orphaned subtitles** section that finds those files and moves them out of the library.

- **Scan** walks every library folder for subtitle files (`.srt`, `.ass`, `.ssa`, `.vtt`, `.sub`)
  and lists the ones with no video beside them. It changes nothing.
- **Archive** moves exactly what the scan found into the configured **subtitle archive path** and
  reports what moved. It will not overwrite a file already in the archive; a clash gets a
  `.1`, `.2` suffix.

A subtitle counts as orphaned when no video file in the same folder has the same name once
language and flag tags are peeled off, so `Movie.2024.Group.fa.srt` and
`Movie.2024.Group.en.forced.srt` both match `Movie.2024.Group.mkv`. If a video was *renamed* rather
than deleted, its subtitle looks orphaned and will be archived — which is why the scan shows you
the list before anything moves.

Library folders that sit inside one another are scanned once, so a nested library is not processed
twice.

## API quota

A free SubDL key allows 2,000 searches and 50 downloads per day. The plugin makes one
search per item and one download per subtitle, so a backfill of a few hundred items takes several
days; the scheduled task picks up where it left off on the next run.

A subdl quota that runs out mid-run is not an error: SubDL reports nothing, Jellyfin moves on to
SubSource, and the log shows the failed subdl call next to the successful SubSource one.

## How it works

- `Search` calls subdl's `/api/v2/files/search` with the video's release filename, filtered
  to `languages=fa`. That endpoint resolves the title and scopes TV results to the episode
  parsed out of the filename, and returns a `match_score` per result.
- `GetSubtitles` downloads the matched archive from `/api/v2/subtitles/{nId}/download` and
  picks the subtitle file for the right episode out of it.

## Building

```
dotnet build Jellyfin.Plugin.SubDL/Jellyfin.Plugin.SubDL.csproj -c Release
```

The plugin targets `net10.0` and `targetAbi` 12.2.0.0, so it requires Jellyfin 12.2.

Pushing a tag like `v1.0.0.0` builds the package, creates a GitHub release, and rewrites
`manifest.json` so the Jellyfin repository points at the new version.
