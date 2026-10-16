# jellyfin-subdl

A Jellyfin plugin that downloads Persian (FA) subtitles from [subdl.com](https://subdl.com).

## Installation

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add:

   ```
   https://raw.githubusercontent.com/HosenM/jellyfin-subdl/main/manifest.json
   ```

2. Go to the **Catalog** tab, find **SubDL** and install it.
3. Restart Jellyfin.
4. Open **Dashboard → Plugins → SubDL** and paste your SubDL API key. Get one from your
   [subdl.com](https://subdl.com) account settings.

## Automatic subtitle downloads

The plugin registers itself as a subtitle provider, so Jellyfin's existing subtitle
machinery does the work — there is no separate download task in the plugin.

To download subtitles automatically, set Persian as a subtitle download language on the
library (**Edit library → Subtitles → Download languages**) or server-wide under
**Dashboard → Subtitles**. The *Download missing subtitles* scheduled task then fills in
Persian subtitles for existing items, and new items get them as they are scanned. Run that
task manually to backfill a library you already have.

`Require a perfect subtitle match` maps onto subdl's `match_score`: with it enabled only
results scored 0.8 or higher are accepted. Persian subtitles are often season packs that
score lower than that, so enabling this may result in nothing being downloaded.

## Checking that it works

The plugin config page (**Dashboard → Plugins → SubDL**) shows your SubDL plan and how many
searches and downloads are left today. Press **Refresh** after a scan to watch the counters move.

Every search, download and failure is logged under the `SubDL` prefix and is visible at
**Dashboard → Logs**. Turn the log level up to `Debug` there for per-item detail. A successful
search logs a line like:

```
[INF] Jellyfin.Plugin.SubDL.Providers.SubdlSubtitleProvider: SubDL: 2 Persian subtitle(s) for Anora.2024.2160p.AMZN.WEB-DL.DDP5.1.H.265-FLUX.mkv
```

## API quota

A free SubDL key allows 2,000 searches and 50 downloads per day. The plugin makes one
search per item and one download per subtitle, so a large backfill can exhaust the daily
download quota; the scheduled task picks up where it left off on the next run.

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
