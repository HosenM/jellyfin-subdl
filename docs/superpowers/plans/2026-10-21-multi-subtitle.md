# Several Persian subtitles per movie — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A scheduled task that downloads up to N subdl Persian subtitles per movie, one per uploader, each named `<video stem>.<title> - <uploader>.fa.<ext>`.

**Architecture:** Pure logic (file naming, picking one-per-uploader, the finished-movies record) lives in small static/plain classes under `Movies/` with unit tests. The subdl and SubSource HTTP code moves out of the two providers into `SubdlClient` / `SubSourceClient` singletons so the providers and the new `MovieSubtitleTask` share it. An `IHostedService` queues the task five minutes after movies are added.

**Tech Stack:** C# / `net10.0`, Jellyfin 12.2 (`Jellyfin.Controller`, `Jellyfin.Model`), xUnit for the new test project.

**Spec:** `docs/superpowers/specs/2026-10-21-multi-subtitle-design.md`

## Global Constraints

- Jellyfin 12.2, `targetAbi` 12.2.0.0, `net10.0`. No new package references in the plugin project.
- Prerequisite: the .NET 10 SDK. This machine has none installed (`dotnet` reports "No .NET SDKs were found"), so install it before Task 1. Every `dotnet` command below depends on it.
- New test project `tests/Jellyfin.Plugin.SubDL.Tests` (xUnit). It is never packaged; `release.yml` keeps building only the plugin project.
- Setting name `SubtitlesPerMovie` (int), default `0`, where 0 means the feature is off and everything behaves as before.
- File name format `<video stem>.<title> - <uploader>.fa.<format>`, with the whole file name at most 200 characters (shorten `<title>`).
- Movies only. Order: newest `DateCreated` first. Fill each movie completely before moving to the next one.
- Each movie is searched once. It is marked finished unless the subdl download quota cut it off.
- Never overwrite or delete a file. Writes use `FileMode.CreateNew`.
- Logs use the `SubDL:` prefix, plus `SubSource:` for SubSource calls. Code style matches the existing files: XML docs on public members, `CultureInfo.InvariantCulture`, `ConfigureAwait(false)`, and comments that say why.

## Review Focus

1. **Release names or uploaders with characters Windows forbids** (`:`, `?`, `"`, `/`, `|`), or very long ones. Expect a valid file name of 200 characters or fewer. Tests are in Task 1.
2. **Release names containing tokens Jellyfin reads as flags or languages** (`.HI.`, `.CC.`, `.en.`, `.forced.`). Expect a title with no dots, so Jellyfin sees one title token and the language stays `fa`. Tests are in Task 1.
3. **An uploader name containing ` - `**, or two uploaders that differ only by case or spacing. Expect round-tripping through the file name to give back the same uploader key, and the two near-duplicates to count as one uploader. Tests are in Tasks 1 and 2.
4. **A state file that is missing, empty or corrupt.** Expect it treated as empty: no crash, and every movie is a candidate again. Tests are in Task 3.
5. **The quota running out partway through a movie.** Expect the movie not to be marked finished, and the next run to skip uploaders that already have a file. Tests for the pieces are in Tasks 1 (`TryGetUploader`) and 2 (`have` set). The flow is checked in Task 6's manual verification.

---

### Task 1: File naming

**Files:**
- Create: `tests/Jellyfin.Plugin.SubDL.Tests/Jellyfin.Plugin.SubDL.Tests.csproj` (xUnit 2.9.3, `Microsoft.NET.Test.Sdk` 17.14.1, `xunit.runner.visualstudio` 3.1.4; `ProjectReference` to the plugin; `PackageReference` to `Jellyfin.Model` and `Jellyfin.Controller` 12.2.0 **without** `ExcludeAssets`, so the types load at test time)
- Modify: `Jellyfin.Plugin.SubDL/Jellyfin.Plugin.SubDL.csproj` (add `<InternalsVisibleTo Include="Jellyfin.Plugin.SubDL.Tests" />`)
- Create: `Jellyfin.Plugin.SubDL/Movies/SubtitleNaming.cs`
- Test: `tests/Jellyfin.Plugin.SubDL.Tests/SubtitleNamingTests.cs`

**Interfaces:**
- Produces: `internal static class SubtitleNaming` with
  - `const int MaxFileNameLength = 200`
  - `string Clean(string? value)`: dots become spaces; `Path.GetInvalidFileNameChars()` plus `<>:"/\|?*` are removed; ` - ` becomes a space (so the separator stays unique); whitespace runs collapse to one space; the result is trimmed. Empty → `""`.
  - `string BuildFileName(string videoStem, string title, string uploader, string format)`: returns `{stem}.{Clean(title)} - {Clean(uploader) or "Unknown"}.fa.{format lowercased}`. If longer than 200 characters, shorten the cleaned title, never the stem or uploader. An empty cleaned title becomes `SubDL`.
  - `bool TryGetUploader(string videoStem, string fileName, out string uploader)`: true when `fileName` starts with `stem + "."` (case-insensitive), ends with `.fa.<ext>` where `<ext>` is one of srt/ass/ssa/vtt/sub, and the middle contains ` - `. `uploader` is the text after the last ` - `.

- [ ] **Step 1: Create the test project and write failing tests**

```csharp
[Fact] public void BuildFileName_UsesTitleUploaderAndFa()
    => Assert.Equal("Anora.2024.2160p.Anora 2024 1080p BluRay - Ali Rezaei.fa.srt",
        SubtitleNaming.BuildFileName("Anora.2024.2160p", "Anora.2024.1080p.BluRay", "Ali Rezaei", "SRT"));

[Fact] public void Clean_RemovesDotsSoNoTokenIsReadAsAFlag()
    => Assert.Equal("Movie 2024 HI en forced CC WEB", SubtitleNaming.Clean("Movie.2024.HI.en.forced.CC.WEB"));

[Fact] public void Clean_StripsInvalidCharacters()
    => Assert.Equal("Who Cares Yes No", SubtitleNaming.Clean("Who: Cares? \"Yes\" / No|"));

[Fact] public void Clean_ReplacesSeparatorInsideUploader()
    => Assert.Equal("Team A B", SubtitleNaming.Clean("Team A - B"));

[Fact] public void BuildFileName_CapsLengthByShorteningTitle()
{
    var name = SubtitleNaming.BuildFileName("Stem", new string('x', 400), "Uploader", "srt");
    Assert.True(name.Length <= SubtitleNaming.MaxFileNameLength);
    Assert.StartsWith("Stem.", name);
    Assert.EndsWith(" - Uploader.fa.srt", name);
}

[Fact] public void BuildFileName_MissingUploaderIsUnknown()
    => Assert.EndsWith(" - Unknown.fa.srt", SubtitleNaming.BuildFileName("S", "T", "  ", "srt"));

[Fact] public void TryGetUploader_RoundTripsBuildFileName()
{
    var name = SubtitleNaming.BuildFileName("Movie.2024", "Movie 2024 - Extended", "Team A - B", "ass");
    Assert.True(SubtitleNaming.TryGetUploader("Movie.2024", name, out var uploader));
    Assert.Equal("Team A B", uploader);
}

[Theory]
[InlineData("Movie.2024.fa.srt")]            // plain Jellyfin name, not ours
[InlineData("Other.T - U.fa.srt")]           // different video
[InlineData("Movie.2024.T - U.en.srt")]      // not Persian
[InlineData("Movie.2024.T - U.fa.txt")]      // not a subtitle
public void TryGetUploader_RejectsFilesThatAreNotOurs(string file)
    => Assert.False(SubtitleNaming.TryGetUploader("Movie.2024", file, out _));
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test tests/Jellyfin.Plugin.SubDL.Tests`
Expected: build error, because `SubtitleNaming` does not exist.

- [ ] **Step 3: Implement `SubtitleNaming` in `Jellyfin.Plugin.SubDL/Movies/SubtitleNaming.cs`**

`Clean` is used for both the title and the uploader, so a cleaned uploader never contains ` - `, and splitting on the last ` - ` is enough.

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test tests/Jellyfin.Plugin.SubDL.Tests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add tests Jellyfin.Plugin.SubDL/Jellyfin.Plugin.SubDL.csproj Jellyfin.Plugin.SubDL/Movies/SubtitleNaming.cs
git commit -m "Add naming for per-uploader movie subtitles"
```

### Task 2: Picking one subtitle per uploader

**Files:**
- Create: `Jellyfin.Plugin.SubDL/Movies/SubtitlePicker.cs`
- Test: `tests/Jellyfin.Plugin.SubDL.Tests/SubtitlePickerTests.cs`

**Interfaces:**
- Consumes: `SubtitleNaming.Clean`
- Produces:
  - `internal sealed record SubtitleCandidate(string Id, string Title, string? Uploader, double Score)`
  - `internal static class SubtitlePicker` with
    - `string UploaderKey(string? uploader)`: `Clean(uploader)` in lowercase, or `"unknown"` when the cleaned value is empty.
    - `IReadOnlyList<SubtitleCandidate> Pick(IEnumerable<SubtitleCandidate> candidates, IReadOnlySet<string> have, int count)`: group by `UploaderKey`, drop groups whose key is in `have` (the keys of uploaders already on disk), keep the highest score per group, order groups by that score (highest first), and take `count`. If `count <= 0`, return an empty list.

- [ ] **Step 1: Write failing tests**

```csharp
static SubtitleCandidate C(string id, string? up, double s) => new(id, "T" + id, up, s);

[Fact] public void Pick_KeepsBestPerUploaderAndOrdersByScore()
{
    var picked = SubtitlePicker.Pick(
        [C("1", "Ali", 0.3), C("2", "Ali", 0.6), C("3", "Sara", 0.5), C("4", "Reza", 0.2)],
        new HashSet<string>(), 2);
    Assert.Equal(["2", "3"], picked.Select(p => p.Id));
}

[Fact] public void Pick_TreatsCaseAndSpacingAsSameUploader()
    => Assert.Single(SubtitlePicker.Pick([C("1", "Ali  Rezaei", 0.4), C("2", "ali rezaei", 0.5)], new HashSet<string>(), 5));

[Fact] public void Pick_GroupsMissingAuthorsAsOneUnknown()
    => Assert.Single(SubtitlePicker.Pick([C("1", null, 0.4), C("2", " ", 0.5)], new HashSet<string>(), 5));

[Fact] public void Pick_SkipsUploadersAlreadyOnDisk()
{
    var have = new HashSet<string> { SubtitlePicker.UploaderKey("Ali") };
    var picked = SubtitlePicker.Pick([C("1", "Ali", 0.9), C("2", "Sara", 0.1)], have, 5);
    Assert.Equal(["2"], picked.Select(p => p.Id));
}

[Fact] public void Pick_ZeroCountIsEmpty()
    => Assert.Empty(SubtitlePicker.Pick([C("1", "Ali", 0.9)], new HashSet<string>(), 0));
```

- [ ] **Step 2: Run them and confirm they fail.** Run: `dotnet test tests/Jellyfin.Plugin.SubDL.Tests`. Expected: build error.
- [ ] **Step 3: Implement `SubtitlePicker`.**
- [ ] **Step 4: Run them and confirm they pass.** Expected: all pass.
- [ ] **Step 5: Commit.** Message: `Pick one subtitle per uploader`.

### Task 3: Finished-movies record

**Files:**
- Create: `Jellyfin.Plugin.SubDL/Movies/SearchRecord.cs`
- Test: `tests/Jellyfin.Plugin.SubDL.Tests/SearchRecordTests.cs`

**Interfaces:**
- Produces: `internal sealed class SearchRecord` with
  - `static SearchRecord Load(string path)`: reads a JSON array of GUIDs. A missing, empty or unreadable file (`IOException`, `JsonException`, `UnauthorizedAccessException`) gives an empty record.
  - `bool IsFinished(Guid movieId)`
  - `void MarkFinished(Guid movieId)`: adds the id and saves immediately, writing to `path + ".tmp"` and then `File.Move(..., overwrite: true)` so a crash cannot leave a half-written file. Creates the directory if needed.
- The task stores the file at `Path.Combine(Plugin.Instance!.DataFolderPath, "finished-movies.json")`.

- [ ] **Step 1: Write failing tests** in a temp directory per test:
  - `Load_MissingFile_IsEmpty`
  - `Load_CorruptFile_IsEmpty` (file contents `"{not json"`)
  - `Load_EmptyFile_IsEmpty`
  - `MarkFinished_PersistsAcrossLoad`: mark id A, `Load` again, `IsFinished(A)` is true and `IsFinished(B)` is false
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement `SearchRecord`.** Use `System.Text.Json`.
- [ ] **Step 4: Run them and confirm they pass.**
- [ ] **Step 5: Commit.** Message: `Record movies that have been searched`.

### Task 4: Shared subdl and SubSource clients, plus the movie gate in the providers

This is a refactor: the providers must behave exactly as before when the setting is 0.

**Files:**
- Create: `Jellyfin.Plugin.SubDL/SubdlClient.cs`, `Jellyfin.Plugin.SubDL/SubSourceClient.cs`
- Modify: `Jellyfin.Plugin.SubDL/Providers/SubdlSubtitleProvider.cs`, `Jellyfin.Plugin.SubDL/Providers/SubSourceSubtitleProvider.cs`, `Jellyfin.Plugin.SubDL/Providers/SubtitleHelpers.cs`, `Jellyfin.Plugin.SubDL/PluginServiceRegistrator.cs`, `Jellyfin.Plugin.SubDL/Configuration/PluginConfiguration.cs`
- Test: `tests/Jellyfin.Plugin.SubDL.Tests/SubtitleHelpersTests.cs`

**Interfaces:**
- Produces:
  - `PluginConfiguration.SubtitlesPerMovie` (`int`, default `0`), with XML doc.
  - `SubtitleHelpers.IsLeftToMovieTask(VideoContentType contentType, int subtitlesPerMovie) -> bool`: true when the content type is `Movie` and `subtitlesPerMovie > 0`.
  - `internal sealed record SubdlResult(string NId, string? ReleaseName, string? Author, double MatchScore)`
  - `public sealed class SubdlClient` (a DI singleton; it takes `IHttpClientFactory` and `ILogger<SubdlClient>`):
    - `Task<IReadOnlyList<SubdlResult>?> SearchAsync(string fileName, bool isEpisode, string apiKey, CancellationToken)`: the request and filtering that `Search` does today (Persian results that have an nId and a `match_score`), ordered by score with the highest first. Returns `null` when the HTTP call fails (logged as today) and an empty list for no results.
    - `Task<bool> HasDownloadQuotaAsync(string apiKey, CancellationToken)`: moved unchanged, including the one-minute cache. Its static fields become instance fields.
    - `void NoteDownloadUsed()`: decrements the cached remaining count, so several downloads within one cached minute don't overshoot the quota.
    - `Task<HttpResponseMessage> DownloadAsync(string nId, string apiKey, CancellationToken)`: the zip request as it is today.
  - `internal sealed record SubSourceResult(string Id, string? ReleaseName, string? Uploader)`
  - `public sealed class SubSourceClient` (a DI singleton):
    - `Task<IReadOnlyList<SubSourceResult>> SearchAsync(string? imdbId, string? title, int? year, bool isEpisode, int? season, int? episode, string apiKey, CancellationToken)`: today's `FindMovieIdAsync` and subtitle list. `ReleaseName` is the `releaseInfo` entries joined with `", "`. `Uploader` is null unless the subtitle list response has an uploader field (see Task 6, step 4).
    - `Task<HttpResponseMessage> DownloadAsync(string id, string apiKey, CancellationToken)`
  - Both providers take their client through the constructor and keep their public behaviour. In addition, `Search` first returns `[]` when `SubtitleHelpers.IsLeftToMovieTask(request.ContentType, Plugin.Instance?.Configuration.SubtitlesPerMovie ?? 0)`, logging at Debug.
  - `PluginServiceRegistrator` registers `SubdlClient` and `SubSourceClient` as singletons, before the providers.

- [ ] **Step 1: Write failing tests**

```csharp
[Theory]
[InlineData(VideoContentType.Movie, 3, true)]
[InlineData(VideoContentType.Movie, 0, false)]
[InlineData(VideoContentType.Episode, 3, false)]
public void IsLeftToMovieTask(VideoContentType type, int perMovie, bool expected)
    => Assert.Equal(expected, SubtitleHelpers.IsLeftToMovieTask(type, perMovie));
```

- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Add the setting and the helper, move the HTTP code into the two clients, slim the providers down to use them, and register the clients.**
- [ ] **Step 4: Run the tests and the build.** Run: `dotnet test tests/Jellyfin.Plugin.SubDL.Tests` and `dotnet build Jellyfin.Plugin.SubDL/Jellyfin.Plugin.SubDL.csproj -c Release`. Expected: tests pass, and the build has 0 warnings and 0 errors.
- [ ] **Step 5: Commit.** Message: `Share subdl and SubSource clients and leave movies to the movie task`.

### Task 5: The movie subtitle task and new-movie trigger

**Files:**
- Create: `Jellyfin.Plugin.SubDL/Movies/MovieSubtitleTask.cs`, `Jellyfin.Plugin.SubDL/Movies/NewMovieWatcher.cs`
- Modify: `Jellyfin.Plugin.SubDL/PluginServiceRegistrator.cs`

**Interfaces:**
- Consumes: `SubtitleNaming`, `SubtitlePicker`, `SubtitleCandidate`, `SearchRecord`, `SubdlClient`, `SubSourceClient`, `SubtitleHelpers.ExtractAsync` / `IsPersianCode`
- Produces:
  - `public class MovieSubtitleTask : IScheduledTask` with Name `"Download Persian subtitles"`, Key `"SubDLMovieSubtitles"`, Category `"SubDL"`, and a description that names the setting. Its default trigger is an `IntervalTrigger` every 24 hours.
  - `public sealed class NewMovieWatcher : IHostedService`, registered with `serviceCollection.AddHostedService<NewMovieWatcher>()`.

The task can't be unit tested without a Jellyfin host, so it's verified in Task 6. Keep it a straight walk with the decisions below.

- [ ] **Step 1: Implement `MovieSubtitleTask.ExecuteAsync`.** Constructor dependencies: `ILibraryManager`, `IMediaSourceManager`, `IProviderManager`, `IFileSystem`, `SubdlClient`, `SubSourceClient`, `ILogger<MovieSubtitleTask>`.
  1. Return immediately if `SubtitlesPerMovie <= 0` or the subdl API key is empty.
  2. Query `ILibraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie], IsVirtualItem = false, OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)], Recursive = true })`.
  3. For each `Video` movie, report progress as `index / count * 100`, then skip it when any of these hold:
     - `IsShortcut` is true;
     - `VideoType != VideoType.VideoFile`;
     - `PrimaryVersionId` is set (an alternate version);
     - `SearchRecord.IsFinished(item.Id)`;
     - it has a subtitle stream (from `IMediaSourceManager.GetMediaStreams(item.Id)`, `Type == MediaStreamType.Subtitle`) where `SubtitleHelpers.IsPersianCode(stream.Language)` is true and that stream is not one of ours. A stream is ours when it is external and `SubtitleNaming.TryGetUploader(stem, Path.GetFileName(stream.Path), out _)` is true.
  4. `have` = the `SubtitlePicker.UploaderKey` of each of our streams. `wanted = SubtitlesPerMovie - have.Count`. If `wanted <= 0`, mark the movie finished and continue.
  5. Call subdl `SearchAsync(fileName, false, …)`. If it returns `null`, the call failed: skip the movie without marking it finished. If the library's `GetLibraryOptions(item).RequirePerfectSubtitleMatch` is on, drop results below 0.8. Map the results to `SubtitleCandidate(NId, ReleaseName ?? "SubDL", Author, MatchScore)`.
  6. If subdl gave **no results at all**, and a SubSource key is set, use SubSource instead:
     - If SubSource results carry uploaders, build candidates with score 0 in list order and pick with `SubtitlePicker.Pick` as for subdl.
     - Otherwise take the first `wanted` results with distinct cleaned release names, and use `"SubSource"` as the uploader in the file name. Grouping by a missing uploader would collapse them to one file.
     - SubSource has no quota check.
  7. Pick subdl candidates with `SubtitlePicker.Pick(candidates, have, wanted)`. For each pick:
     - For subdl: if `HasDownloadQuotaAsync` is false, log `SubDL: daily download quota is spent, stopping until the next run`, **save nothing as finished for this movie, and end the run**.
     - Download the subtitle. On a non-success status or an `InvalidOperationException` from extraction, log a warning and continue with the next pick. After a successful subdl download, call `NoteDownloadUsed()`.
     - `ExtractAsync(stream, "fa", null, null, ct)`, then `BuildFileName(stem, title, uploader, format)`.
     - The folder is `GetLibraryOptions(item).SaveSubtitlesWithMedia ? item.ContainingFolderPath : item.GetInternalMetadataPath()`.
     - Write with `new FileStream(path, FileMode.CreateNew)`. If the file exists (`IOException` while the file is there), log at Debug and skip it.
     - Log `SubDL: saved {File} for {Movie}`.
  8. After the movie, if at least one file was written, call `IProviderManager.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.Normal)`. Then `MarkFinished(item.Id)`, because the quota did not cut it off.
  9. Log a summary at the end: movies done, files written, and whether the quota stopped the run.
- [ ] **Step 2: Implement `NewMovieWatcher`.**
  - `StartAsync` subscribes to `ILibraryManager.ItemAdded`; `StopAsync` unsubscribes and disposes the timer.
  - When an added item is a `Movie` and `SubtitlesPerMovie > 0`, it resets a single `System.Threading.Timer` to fire in 5 minutes.
  - When the timer fires, it calls `ITaskManager.QueueIfNotRunning<MovieSubtitleTask>()`.
- [ ] **Step 3: Register both services.** `IScheduledTask` implementations are discovered by Jellyfin from the plugin assembly. If a test run in Task 6 shows the task missing, add `serviceCollection.AddSingleton<IScheduledTask, MovieSubtitleTask>()`.
- [ ] **Step 4: Build.** Run: `dotnet build Jellyfin.Plugin.SubDL/Jellyfin.Plugin.SubDL.csproj -c Release`. Expected: 0 warnings, 0 errors. Then run `dotnet test tests/Jellyfin.Plugin.SubDL.Tests`; expected: all pass.
- [ ] **Step 5: Commit.** Message: `Add the Persian movie subtitle task and new-movie trigger`.

### Task 6: Plugin page, README, and checking on a real server

**Files:**
- Modify: `Jellyfin.Plugin.SubDL/Configuration/configPage.html`, `README.md`, `Jellyfin.Plugin.SubDL/Jellyfin.Plugin.SubDL.csproj` (Version `1.3.0.0`)

- [ ] **Step 1: Update the plugin page.**
  - Add a number input `SubtitlesPerMovie` (min 0, max 50) labelled "Persian subtitles per movie". Its help text reads: "0 turns this off. While it is above 0, movies get their Persian subtitles from the 'Download Persian subtitles' task, one per uploader, and the subtitle search in a movie's menu shows no SubDL or SubSource results. Series are not affected."
  - Load and save it with the other fields, as `parseInt(value, 10) || 0`.
  - Generalise `getSubtitleTask` / `renderTask` / `runNow` / `pollTask` to take a task key and a target element id. Render two blocks: `SubDLMovieSubtitles` ("Download Persian subtitles", for movies) and `DownloadSubtitles` (Jellyfin's task, for series), each with its own **Run now**.
- [ ] **Step 2: Update the README.**
  - Add a "Several subtitles per movie" section: the setting, the naming example from the spec, one per uploader, newest first, each movie searched once, and SubSource as backup.
  - Note that Persian stays in the download languages, and that the manual movie search loses SubDL/SubSource results while the setting is on.
  - Under the quota section: each subtitle is one download, so the setting multiplies download use.
- [ ] **Step 3: Build and test.** Run the same two commands as in Task 5, step 4. Expected: both clean.
- [ ] **Step 4: Check on the owner's Jellyfin server** (install the built DLL in the server's plugin folder and restart).
  - Set the number to 3 and run **Download Persian subtitles** on a movie with no Persian subtitle.
  - Expect up to 3 files named `<stem>.<title> - <uploader>.fa.srt`, each from a different uploader, and the player's subtitle menu showing the title and uploader as Persian.
  - Run the task again: that movie is skipped (it's finished), with no new searches in the subdl status.
  - Open the movie's subtitle search: no SubDL/SubSource results. Do the same on an episode: results as before.
  - Look at one live SubSource `/subtitles` response with the owner's key. If it carries an uploader field, map it in `SubSourceClient`, then rebuild and commit.
- [ ] **Step 5: Commit.** Message: `Add the per-movie subtitle setting to the plugin page and README`.
