using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubDL.Movies;

/// <summary>
/// Queues the movie subtitle task once a library scan has stopped adding movies for a while,
/// so new movies get their subtitles without waiting for the daily run.
/// </summary>
public sealed class NewMovieWatcher : IHostedService
{
    // A scan adds movies one at a time; waiting for a quiet spell runs the task once per scan.
    private static readonly TimeSpan QuietDelay = TimeSpan.FromMinutes(5);

    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<NewMovieWatcher> _logger;
    private readonly Lock _lock = new();

    private Timer? _timer;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewMovieWatcher"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public NewMovieWatcher(ILibraryManager libraryManager, ITaskManager taskManager, ILogger<NewMovieWatcher> logger)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _timer = new Timer(OnQuiet, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;

        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }

        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Movie || (Plugin.Instance?.Configuration.SubtitlesPerMovie ?? 0) <= 0)
        {
            return;
        }

        lock (_lock)
        {
            // Each new movie pushes the run back, so it starts once the scan has gone quiet.
            _timer?.Change(QuietDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnQuiet(object? state)
    {
        try
        {
            _logger.LogInformation("SubDL: new movies were added, queueing the Persian subtitle task");
            _taskManager.QueueIfNotRunning<MovieSubtitleTask>();
        }
        catch (Exception ex)
        {
            // A timer callback that throws would take the server down.
            _logger.LogError(ex, "SubDL: could not queue the Persian subtitle task");
        }
    }
}
