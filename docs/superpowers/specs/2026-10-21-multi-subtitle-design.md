# Several Persian subtitles per movie — design

Date: 2026-10-21

## Intent

subdl often has many Persian subtitles for one movie (18 for the movie that prompted this),
made by different translators. Today the plugin ends up with exactly one, because Jellyfin's
subtitle download keeps one file per language. The owner wants several on disk so they can pick
in the player, but not every version: one uploader often posts several near-identical versions of
the same translation, and those are noise.

So: for movies with no Persian subtitle, download up to a configured number of subdl subtitles,
at most one per uploader, each saved under a name that shows its subdl title and uploader in the
player's subtitle menu.

## Goals

- A plugin setting for how many Persian subtitles to download per movie.
- At most one subtitle per uploader; when an uploader has several, keep their best-matching one.
- Each file named so the player shows `<subdl title> - <uploader>`, for example
  `Anora 2024 1080p BluRay - Ali Rezaei`. The release quality (`1080p BluRay`) is already part of
  subdl's title and is not added separately.
- Movies added later get their subtitles without waiting a day.

## Non-goals

- TV episodes. Movies only for now; episodes keep today's behaviour.
- Movies that already have a Persian subtitle. They are left alone: nothing is added, counted, renamed
  or removed.
- SubSource as an equal source. It stays a backup, used only when subdl has nothing for a movie.
- Getting around the free-tier quota (50 downloads a day). The owner accepts that a backfill is slow.

## Anti-goals

- Several files that are really the same translation from the same uploader.
- Overwriting or deleting any subtitle file the plugin did not write.
- Burning quota on movies that are already done, or on downloads that are certain to fail.
- A Persian subtitle file Jellyfin does not recognise as Persian.

## Constraints

- Jellyfin 12.2, `net10.0`, same project and conventions as the existing plugin (see
  `2026-10-16-subdl-plugin-design.md`). No new dependencies.
- **Jellyfin cannot do this through its own subtitle download.** `SubtitleManager.TrySaveSubtitle`
  always names the file `<video stem>.<language>[.forced][.sdh].<format>`, adding a numeric
  counter if that name is taken, and its *Download missing subtitles* task fetches one subtitle
  per missing language. The plugin therefore writes these files itself.
- **How Jellyfin reads an external subtitle name** (`Emby.Naming/ExternalFiles/ExternalPathParser`):
  the part after the video stem is split on `.` and read right to left. The first language token
  sets the language, `forced`/`default` flags match by substring, hearing-impaired flags (`sdh`,
  `hi`, `cc`) match exactly, and every unrecognised token becomes the stream's title. A matched
  token is removed from the whole remaining string, not just at its own position.
- subdl free tier: 50 downloads/day, and a download is one zip per subtitle. Quota is read from
  `/api/v2/me` as the existing provider already does (`HasDownloadQuotaAsync`).

## How it works

### Setting

`PluginConfiguration` gets `SubtitlesPerMovie` (int). The plugin page shows it as
"Persian subtitles per movie". **0 means the feature is off** and the plugin behaves exactly as
today. Default 0, so an upgrade changes nothing until the owner sets a number. *(My call.)*

### Which movies

A movie is a candidate when:

- it is a movie (not an episode, not anything else) whose media path is a single video file on
  disk. `.strm` files and DVD/Blu-ray folder rips are skipped. For a movie with several versions
  grouped as one item, only the primary version's file is searched for and named after; the other
  versions get nothing. *(My call.)*
- it has **no Persian subtitle stream of any kind**, embedded or external, except files the plugin
  itself wrote under this feature (see *Recognising our own files*), and
- it has not been finished already (see *Search record*).

When the quota runs out partway through a movie, it stays a candidate and the next run tops it
up, skipping uploaders it already has. *(My call: needed so a half-finished movie is not treated
as "has a subtitle" and abandoned.)*

**Order (owner's decision): newest first**, by date added to the library, so movies just added
get their subtitles before the backfill. **Each movie is filled completely** (up to the setting)
before moving on to the next. *(Owner's decision.)*

### Search record

**Each movie is searched once (owner's decision).** A small state file in the plugin's data
folder records the ids of finished movies. A movie is finished when the task got through it
without the quota cutting it off: it has the setting's number of subtitles, or the sources had
fewer (or none). A finished movie is never searched again, even if subdl gets more subtitles
later. A movie the quota cut off is not marked finished, so the next run continues it.

Raising the setting later does not reopen finished movies. *(My call, follows from "just once".)*

### Picking the subtitles

1. Search subdl exactly as `SubdlSubtitleProvider.Search` does today: `/api/v2/files/search`
   with the release filename, `languages=fa`, `type=movie`, and keep results that are Persian and
   have a `match_score`.
2. If the library has *Require a perfect subtitle match* on, drop results below 0.8, as the
   provider does today. *(My call, for consistency.)*
3. Group by uploader (subdl's `author`, compared case-insensitively and trimmed). Results with no
   author form one group, "Unknown". *(My call.)*
4. In each group keep the result with the highest `match_score`.
5. Order the groups by that score, highest first, and take up to `SubtitlesPerMovie` minus the
   number of our files the movie already has.

### Downloading and naming

For each picked subtitle, download the zip from `/api/v2/subtitles/{nId}/download?format=zip` and
pick the file inside with the existing `SubtitleHelpers.ExtractAsync` logic (no episode, so the
largest subtitle file).

Name it:

```
<video stem>.<title> - <uploader>.fa.<format>
```

- `<title>` is subdl's `release_name`, and `<uploader>` its `author`. Both are cleaned up: dots
  become spaces, characters that are invalid in a filename are removed, runs of whitespace collapse
  to one space, and the result is trimmed. With the dots gone, the parser above sees the title as one
  token, so words inside a release name (`HI`, `CC`, a language code) cannot be misread as flags or
  as the language.
- Language is always written as `fa`, so Jellyfin reads it as Persian.
- The whole file name is capped at a safe length (around 200 characters) by shortening `<title>`.
- If the name already exists, that subtitle is skipped. The plugin never overwrites.

Example, for `Anora.2024.2160p.AMZN.WEB-DL.mkv`:

```
Anora.2024.2160p.AMZN.WEB-DL.Anora 2024 1080p BluRay - Ali Rezaei.fa.srt
```

Where the file goes follows the library's *Save subtitles with media* option, the way Jellyfin
does it: beside the video when on, in the item's metadata folder otherwise. After writing a movie's
files, the plugin asks Jellyfin to refresh that item so the new streams show up.

### Recognising our own files

An external subtitle beside the movie (or in its metadata folder) counts as ours when its name is
`<video stem>.` + something containing ` - ` + `.fa.<subtitle extension>`. Plain names like
`Movie.fa.srt` are never ours. Pattern matching is good enough here; which files are ours is not
stored anywhere. Our files only matter for a movie that is not yet finished (a quota cut-off):
they tell the next run which uploaders it already has. *(My call.)*

### Keeping Jellyfin's own task away from movies (owner's decision)

Persian **stays** in the libraries' *Subtitle download languages*, because TV series still rely on
Jellyfin's *Download missing subtitles* task. To stop that task from saving a plain
`Movie.fa.srt` for a movie first (which would make the movie count as already having a
subtitle), **while `SubtitlesPerMovie` is above 0, `SubdlSubtitleProvider.Search` and
`SubSourceSubtitleProvider.Search` return nothing for movie requests**. Episode requests are not
affected.

Accepted cost: while the setting is above 0, searching for subtitles by hand from a movie's menu
no longer shows SubDL or SubSource results.

### Running it

- A new scheduled task, **"Download Persian subtitles"** (category: the plugin's), runs every 24
  hours. It walks candidate movies and stops cleanly when the subdl download quota is spent. The
  next run carries on.
- When a library scan adds movies, the task is queued once things have been quiet for about five
  minutes, so new movies are not stuck waiting a day. *(My call on the delay.)*
- Before each download the remaining quota is checked. A failed download of a single subtitle is
  logged and skipped. It does not stop the movie or the run, unless the failure is the quota.
- Every movie searched, every file written and every skip is logged with the `SubDL:` prefix,
  like the rest of the plugin.

### SubSource as backup

When subdl's search returns no Persian results at all for a candidate movie (not when subdl's
quota is spent, since that movie just waits, and not when subdl has results but only from
uploaders the movie already has), the task asks SubSource with the existing `SubSourceSubtitleProvider` lookup. Same naming,
same one-per-uploader rule, if SubSource's subtitle list says who uploaded each entry. If it does
not, take up to the setting's number of distinct release names and use `SubSource` as the uploader
in the name. *(My call, pending a look at the live SubSource response.)*

### Plugin page

- The new number field, with a note that while it is above 0, the manual subtitle search for
  movies shows no SubDL/SubSource results.
- Task status and a **Run now** button for both tasks: the new movie task, and Jellyfin's
  *Download missing subtitles* (still used for series). *(My call.)*
- No warning about download languages. Persian is meant to stay there.

### Unchanged

Episodes (automatic and manual search), Jellyfin's own *Download missing subtitles* task, the
orphan archiver and the status report all keep working as they do today. With the setting at 0,
everything is exactly as before.

## Left to the builder

- The Jellyfin 12.2 APIs for: listing movies, reading an item's subtitle streams and paths,
  library options (`SaveSubtitlesWithMedia`, `RequirePerfectSubtitleMatch`, download
  languages), the item metadata folder, refreshing an item, `IScheduledTask`, queueing a task
  from `ILibraryManager.ItemAdded`.
- How to share the search and quota code with `SubdlSubtitleProvider` without copying it.
- The exact length cap, and how the debounce is implemented.
- The state file's format and location.
- README updates: the new setting, the naming, that Persian stays in the download languages and
  manual movie search loses SubDL/SubSource results while the setting is on, and how quota
  behaves with several downloads per movie.
