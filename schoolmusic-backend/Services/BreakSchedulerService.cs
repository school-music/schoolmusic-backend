using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Hubs;
using schoolmusic_backend.Models;
using System.Threading.Channels;

namespace schoolmusic_backend.Services;

public interface IBreakSchedulerService
{
    Task<bool> SkipCurrentTrackAsync();
    ScheduledBreakPlan? GetCurrentPlan();
    int? GetActiveBreakId();
    Task ProcessTickAsync(CancellationToken stoppingToken = default);
    void NotifyScheduleChanged();
}

public sealed class BreakSchedulerService : BackgroundService, IBreakSchedulerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<QueueHub> _hubContext;
    private readonly ILogger<BreakSchedulerService> _logger;
    private readonly IAudioPlayerService? _audioPlayer;
    private readonly Channel<bool> _wakeUpSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly object _planLock = new();
    private TimeSpan _nextDelay = TimeSpan.FromSeconds(5);
    private SystemStatusDto? _lastKioskStatus;
    private PlayerStatusDto? _lastPlayerStatus;
    private int? _activeBreakId;
    private ScheduledBreakPlan? _currentPlan;
    private List<SpotifyQueueTrack> _spotifyTracks = [];
    private int _lastPlayingTrackIndex = -1;
    private bool _queueSubmitted;
    private bool _pausePending;
    private bool _localPlaybackActive;
    private bool _resumePending;
    private int? _activeSpecialEventId;

    public TimeSpan NextDelay => _nextDelay;

    public BreakSchedulerService(
        IServiceScopeFactory scopeFactory,
        IHubContext<QueueHub> hubContext,
        ILogger<BreakSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _logger = logger;
    }

    public BreakSchedulerService(
        IServiceScopeFactory scopeFactory,
        IAudioPlayerService audioPlayer,
        IHubContext<QueueHub> hubContext,
        ILogger<BreakSchedulerService> logger)
        : this(scopeFactory, hubContext, logger)
    {
        _audioPlayer = audioPlayer;
    }

    public ScheduledBreakPlan? GetCurrentPlan()
    {
        lock (_planLock) return _currentPlan;
    }

    public int? GetActiveBreakId()
    {
        lock (_planLock) return _activeBreakId;
    }

    public Task ProcessTickAsync(CancellationToken stoppingToken = default) => TickAsync(stoppingToken);

    public void NotifyScheduleChanged()
    {
        if (_wakeUpSignal.Writer.TryWrite(true))
            _logger.LogDebug("Scheduler received a schedule change notification.");
    }

    public async Task<bool> SkipCurrentTrackAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
        var spotify = scope.ServiceProvider.GetRequiredService<ISpotifyConnectPlaybackService>();
        ScheduledTrackDto? current;
        lock (_planLock)
        {
            current = _currentPlan != null && _lastPlayingTrackIndex >= 0 &&
                _lastPlayingTrackIndex < _spotifyTracks.Count
                ? _currentPlan.Tracks.FirstOrDefault(track => track.QueueItemId == _spotifyTracks[_lastPlayingTrackIndex].QueueItemId)
                : null;
        }

        if (current == null) return false;
        var item = await db.QueueItems.FindAsync(current.QueueItemId);
        if (item == null || !QueueItemStatuses.TryTransition(item, QueueItemStatuses.Skipped))
            return false;
        await db.SaveChangesAsync();

        await spotify.SkipToNextAsync();
        await scope.ServiceProvider.GetRequiredService<IQueueService>().BroadcastQueueAsync(item.BreakId);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Spotify Connect break scheduler started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Break scheduler tick failed.");
            }

            try
            {
                await WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task WaitForNextTickAsync(CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(_nextDelay);

        try
        {
            await _wakeUpSignal.Reader.ReadAsync(timeout.Token);
            while (_wakeUpSignal.Reader.TryRead(out _))
            {
                // Wiele zmian harmonogramu może zostać obsłużonych przez jeden kolejny cykl.
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Upłynął adaptacyjny czas oczekiwania — wykonujemy kolejny standardowy cykl.
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
        var breakService = scope.ServiceProvider.GetRequiredService<IBreakService>();
        var queueService = scope.ServiceProvider.GetRequiredService<IQueueService>();
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var weekday = (sbyte)now.DayOfWeek;
        if (weekday == 0) weekday = 7;

        var pendingEvent = await db.SpecialEvents
            .Where(item => item.EventStatus == "pending" && item.StartsAt <= now)
            .OrderBy(item => item.StartsAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (pendingEvent != null)
        {
            pendingEvent.EventStatus = "playing";
            await db.SaveChangesAsync(cancellationToken);
            var player = _audioPlayer ?? scope.ServiceProvider.GetRequiredService<IAudioPlayerService>();
            var result = await player.PlayFileOrUrlAsync(
                pendingEvent.FileUrl,
                pendingEvent.Title ?? "School announcement",
                "School announcement");
            if (result.Success)
            {
                _activeSpecialEventId = pendingEvent.Id;
            }
            else
            {
                pendingEvent.EventStatus = "pending";
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        var localPlayer = _audioPlayer ?? scope.ServiceProvider.GetService<IAudioPlayerService>();
        if (localPlayer?.GetStatus().Source is not null and not "None")
        {
            if (!_localPlaybackActive || _pausePending)
                await PauseSpotifyForLocalPlaybackAsync(scope.ServiceProvider, cancellationToken);
            _localPlaybackActive = true;
            await BroadcastSchoolStatusIfChangedAsync(scope, cancellationToken);
            _nextDelay = TimeSpan.FromSeconds(5);
            return;
        }

        var localPlaybackJustEnded = _localPlaybackActive;
        if (localPlaybackJustEnded)
        {
            _localPlaybackActive = false;
            _pausePending = false;
            if (_activeSpecialEventId is int eventId)
            {
                var completedEvent = await db.SpecialEvents.FindAsync([eventId], cancellationToken);
                if (completedEvent != null)
                {
                    completedEvent.EventStatus = "done";
                    await db.SaveChangesAsync(cancellationToken);
                }
                _activeSpecialEventId = null;
            }

            var spotifyService = scope.ServiceProvider.GetService<ISpotifyConnectPlaybackService>();
            if (spotifyService != null)
            {
                try
                {
                    await spotifyService.ResumeAsync(cancellationToken);
                    _resumePending = false;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                    ex is SpotifyPlaybackException or HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    _logger.LogWarning(ex, "Could not resume Spotify after local playback; resume will be retried.");
                    _resumePending = true;
                }
            }
        }

        if (_resumePending && !_localPlaybackActive && !localPlaybackJustEnded)
        {
            var spotifyService = scope.ServiceProvider.GetService<ISpotifyConnectPlaybackService>();
            if (spotifyService != null)
            {
                try
                {
                    await spotifyService.ResumeAsync(cancellationToken);
                    _resumePending = false;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                    ex is SpotifyPlaybackException or HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    _logger.LogWarning(ex, "Spotify resume remains pending after local playback.");
                }
            }
        }

        var isDayOff = await db.ExceptionDays.AnyAsync(item =>
            item.StartDate <= today && today <= item.EndDate &&
            (item.DayType == "day_off" || item.DayType == "no_music"), cancellationToken);
        if (isDayOff)
        {
            if (_activeBreakId.HasValue)
                await CleanupBreakAsync(db, queueService,
                    scope.ServiceProvider.GetRequiredService<ISpotifyConnectPlaybackService>(),
                    _activeBreakId.Value, cancellationToken);
            var status = await BroadcastSchoolStatusIfChangedAsync(scope, cancellationToken);
            _nextDelay = await CalculateNextDelayAsync(db, status, now, cancellationToken);
            return;
        }

        var activeBreak = await db.Breaks.FirstOrDefaultAsync(item =>
            item.BreakDate == today && item.StartAt <= now && now < item.EndsAt, cancellationToken);

        if (_activeBreakId.HasValue && (activeBreak == null || activeBreak.Id != _activeBreakId))
            await CleanupBreakAsync(db, queueService,
                scope.ServiceProvider.GetRequiredService<ISpotifyConnectPlaybackService>(),
                _activeBreakId.Value, cancellationToken);

        if (activeBreak == null)
        {
            var status = await BroadcastSchoolStatusIfChangedAsync(scope, cancellationToken);
            _nextDelay = await CalculateNextDelayAsync(db, status, now, cancellationToken);
            return;
        }

        var musicEnabled = await DetermineMusicEnabledAsync(db, today, weekday, activeBreak, cancellationToken);
        if (!musicEnabled)
        {
            if (_activeBreakId.HasValue)
                await CleanupBreakAsync(db, queueService,
                    scope.ServiceProvider.GetRequiredService<ISpotifyConnectPlaybackService>(),
                    _activeBreakId.Value, cancellationToken);
            var status = await BroadcastSchoolStatusIfChangedAsync(scope, cancellationToken);
            _nextDelay = await CalculateNextDelayAsync(db, status, now, cancellationToken);
            return;
        }

        if (_activeBreakId == null)
        {
            var remainingSeconds = Math.Max(0, (int)(activeBreak.EndsAt - now).TotalSeconds);
            var approvedQueue = await db.QueueItems
                .Where(item => item.BreakId == activeBreak.Id &&
                    (item.ModerationStatus == "approved" || item.ModerationStatus == null) &&
                    item.Status != "played" && item.Status != "skipped")
                .Include(item => item.Song).ThenInclude(song => song.Artist)
                .Include(item => item.Votes)
                .ToListAsync(cancellationToken);
            if (approvedQueue.Count == 0)
            {
                var defaults = await db.DefaultSongs
                    .Include(item => item.Song).ThenInclude(song => song.Artist)
                    .Where(item => item.Song.IsActive)
                    .OrderBy(item => item.Id)
                    .ToListAsync(cancellationToken);
                var existingSongIds = await db.QueueItems
                    .Where(item => item.BreakId == activeBreak.Id)
                    .Select(item => item.SongId)
                    .ToListAsync(cancellationToken);
                var nextOrder = await db.QueueItems
                    .Where(item => item.BreakId == activeBreak.Id)
                    .MaxAsync(item => (int?)item.OrderIndex, cancellationToken) + 1 ?? 1;
                foreach (var defaultSong in defaults.Where(item => !existingSongIds.Contains(item.SongId)))
                {
                    db.QueueItems.Add(new QueueItem
                    {
                        BreakId = activeBreak.Id,
                        SongId = defaultSong.SongId,
                        Song = defaultSong.Song,
                        ModerationStatus = "approved",
                        Status = QueueItemStatuses.Queued,
                        OrderIndex = nextOrder++,
                        CreatedAt = now
                    });
                }
                await db.SaveChangesAsync(cancellationToken);
                approvedQueue = await db.QueueItems
                    .Where(item => item.BreakId == activeBreak.Id &&
                        (item.ModerationStatus == "approved" || item.ModerationStatus == null) &&
                        item.Status != "played" && item.Status != "skipped")
                    .Include(item => item.Song).ThenInclude(song => song.Artist)
                    .Include(item => item.Votes)
                    .ToListAsync(cancellationToken);
            }
            var plan = breakService.CalculateBreak(activeBreak.Id, remainingSeconds, approvedQueue);
            var queueById = approvedQueue.ToDictionary(item => item.Id);
            var tracks = new List<SpotifyQueueTrack>();
            foreach (var scheduled in plan.Tracks)
            {
                if (!queueById.TryGetValue(scheduled.QueueItemId, out var item) ||
                    !IsSpotifyTrackId(item.Song.SpotifyId))
                {
                    if (queueById.TryGetValue(scheduled.QueueItemId, out item))
                        QueueItemStatuses.TryTransition(item, QueueItemStatuses.Skipped);
                    continue;
                }

                tracks.Add(new SpotifyQueueTrack(
                    item.Id,
                    item.Song.SpotifyId!,
                    item.Song.Title,
                    item.Song.Artist?.Name ?? ""));
            }
            if (tracks.Count != plan.Tracks.Count)
                await db.SaveChangesAsync(cancellationToken);

            lock (_planLock)
            {
                _activeBreakId = activeBreak.Id;
                _currentPlan = plan;
                _spotifyTracks = tracks;
                _lastPlayingTrackIndex = -1;
                _queueSubmitted = false;
            }
            await queueService.BroadcastQueueAsync(activeBreak.Id);
            if (tracks.Count == 0)
            {
                var status = await BroadcastSchoolStatusIfChangedAsync(scope, cancellationToken);
                _nextDelay = await CalculateNextDelayAsync(db, status, now, cancellationToken);
                return;
            }
        }

        var spotify = scope.ServiceProvider.GetRequiredService<ISpotifyConnectPlaybackService>();
        if (_pausePending)
            await RetryPauseAsync(spotify, cancellationToken);

        if (_spotifyTracks.Count == 0)
        {
            var status = await BroadcastSchoolStatusIfChangedAsync(scope, cancellationToken);
            _nextDelay = await CalculateNextDelayAsync(db, status, now, cancellationToken);
            return;
        }

        await SubmitQueueIfNeededAsync(db, queueService, spotify, cancellationToken);
        await PollPlaybackAsync(db, queueService, spotify, cancellationToken);
        var finalStatus = await BroadcastSchoolStatusIfChangedAsync(scope, cancellationToken);
        _nextDelay = await CalculateNextDelayAsync(db, finalStatus, now, cancellationToken);
    }

    private async Task SubmitQueueIfNeededAsync(
        schoolmusicContext db,
        IQueueService queueService,
        ISpotifyConnectPlaybackService spotify,
        CancellationToken cancellationToken)
    {
        if (_queueSubmitted || _spotifyTracks.Count == 0)
            return;

        try
        {
            var plannedTracks = _spotifyTracks;
            var playableTracks = await spotify.StartPlaybackAsync(plannedTracks, cancellationToken);
            _queueSubmitted = true;
            _spotifyTracks = playableTracks.ToList();

            var playableQueueItemIds = playableTracks.Select(track => track.QueueItemId).ToHashSet();
            var skippedQueueItemIds = plannedTracks
                .Where(track => !playableQueueItemIds.Contains(track.QueueItemId))
                .Select(track => track.QueueItemId)
                .ToArray();
            if (skippedQueueItemIds.Length > 0)
            {
                var skippedItems = await db.QueueItems
                    .Where(item => skippedQueueItemIds.Contains(item.Id) && item.Status == QueueItemStatuses.Queued)
                    .ToListAsync(cancellationToken);
                foreach (var item in skippedItems)
                    QueueItemStatuses.TryTransition(item, QueueItemStatuses.Skipped);
                await db.SaveChangesAsync(cancellationToken);
                await queueService.BroadcastQueueAsync(_activeBreakId ?? 0);
            }

            _logger.LogInformation("Submitted {TrackCount} ordered Spotify tracks for break {BreakId}.",
                playableTracks.Count, _activeBreakId);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is SpotifyPlaybackException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Spotify Connect is unavailable; playback will be retried while the break is active.");
        }
    }

    private async Task PollPlaybackAsync(
        schoolmusicContext db,
        IQueueService queueService,
        ISpotifyConnectPlaybackService spotify,
        CancellationToken cancellationToken)
    {
        SpotifyPlaybackSnapshot snapshot;
        try
        {
            snapshot = await spotify.GetPlaybackStateAsync(cancellationToken);
            if (!snapshot.DeviceAvailable)
                _queueSubmitted = false;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is SpotifyPlaybackException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _queueSubmitted = false;
            _logger.LogWarning(ex, "Spotify playback state is unavailable; playback will be retried during this break.");
            snapshot = new SpotifyPlaybackSnapshot(false, false, false, null, null, null, 0, 0, 0, null);
        }

        var currentIndex = snapshot.SpotifyTrackId == null
            ? -1
            : _spotifyTracks.FindIndex(track => SpotifyId(track.SpotifyId) == snapshot.SpotifyTrackId);
        if (currentIndex >= 0)
            await UpdateQueuePlaybackStateAsync(db, queueService, currentIndex, snapshot, cancellationToken);

        var status = new PlayerStatusDto(
            snapshot.IsPlaying,
            snapshot.IsPaused,
            snapshot.Volume,
            snapshot.Volume == 0,
            snapshot.SpotifyTrackId == null ? "None" : "SpotifyConnect",
            snapshot.Title,
            snapshot.Artist,
            snapshot.ProgressMs,
            snapshot.DurationMs);
        await BroadcastPlayerStatusAsync(status, cancellationToken);
    }

    private async Task BroadcastPlayerStatusAsync(PlayerStatusDto status, CancellationToken cancellationToken)
    {
        if (_lastPlayerStatus == status)
            return;

        await _hubContext.Clients.All.SendAsync("PlaybackStatusChanged", status, cancellationToken);
        await _hubContext.Clients.Group(QueueHub.KioskGroup).SendAsync("NowPlayingChanged", status, cancellationToken);
        _lastPlayerStatus = status;
    }

    private async Task UpdateQueuePlaybackStateAsync(
        schoolmusicContext db,
        IQueueService queueService,
        int currentIndex,
        SpotifyPlaybackSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var queueIds = _spotifyTracks.Select(track => track.QueueItemId).ToArray();
        var items = await db.QueueItems.Where(item => queueIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var changed = false;

        var firstUnobserved = _lastPlayingTrackIndex < 0 ? 0 : _lastPlayingTrackIndex + 1;
        for (var index = firstUnobserved; index < currentIndex; index++)
        {
            if (items.TryGetValue(_spotifyTracks[index].QueueItemId, out var skipped) &&
                QueueItemStatuses.TryTransition(skipped, QueueItemStatuses.Skipped))
            {
                changed = true;
            }
        }
        if (_lastPlayingTrackIndex >= 0 && _lastPlayingTrackIndex < currentIndex &&
            items.TryGetValue(_spotifyTracks[_lastPlayingTrackIndex].QueueItemId, out var finished) &&
            QueueItemStatuses.TryTransition(finished, QueueItemStatuses.Played))
        {
            changed = true;
        }

        var current = _spotifyTracks[currentIndex];
        if (items.TryGetValue(current.QueueItemId, out var playing))
        {
            if (QueueItemStatuses.TryTransition(playing, QueueItemStatuses.Playing))
            {
                changed = true;
            }
        }

        var playbackState = await db.PlaybackStates.FirstOrDefaultAsync(state => state.Id == 1, cancellationToken);
        if (playbackState == null)
        {
            playbackState = new PlaybackState { Id = 1 };
            db.PlaybackStates.Add(playbackState);
        }
        playbackState.CurrentQueueItemId = current.QueueItemId;
        playbackState.StartedAt = DateTime.Now.AddMilliseconds(-snapshot.ProgressMs);
        playbackState.IsPaused = snapshot.IsPaused;
        playbackState.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(cancellationToken);
        _lastPlayingTrackIndex = currentIndex;

        if (changed)
            await queueService.BroadcastQueueAsync(_activeBreakId ?? 0);
    }

    private async Task RetryPauseAsync(ISpotifyConnectPlaybackService spotify, CancellationToken cancellationToken)
    {
        try
        {
            await spotify.PauseAsync(cancellationToken);
            _pausePending = false;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is SpotifyPlaybackException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Spotify pause is pending and will be retried.");
        }
    }

    private async Task PauseSpotifyForLocalPlaybackAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var spotify = services.GetService<ISpotifyConnectPlaybackService>();
        if (spotify == null)
            return;

        try
        {
            await spotify.PauseAsync(cancellationToken);
            _pausePending = false;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is SpotifyPlaybackException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _pausePending = true;
        }
    }

    private async Task CleanupBreakAsync(
        schoolmusicContext db,
        IQueueService queueService,
        ISpotifyConnectPlaybackService spotify,
        int breakId,
        CancellationToken cancellationToken)
    {
        var pauseSucceeded = false;
        try
        {
            await spotify.PauseAsync(cancellationToken);
            _pausePending = false;
            pauseSucceeded = true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is SpotifyPlaybackException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _pausePending = true;
            _logger.LogWarning(ex, "Could not pause Spotify at break end; pause will be retried.");
        }

        if (pauseSucceeded && _lastPlayerStatus is { Source: "SpotifyConnect" } previous)
            await BroadcastPlayerStatusAsync(previous with { IsPlaying = false, IsPaused = true }, cancellationToken);

        try
        {
            await queueService.ArchiveBreakAsync(breakId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not archive break {BreakId}.", breakId);
        }

        var playback = await db.PlaybackStates.FirstOrDefaultAsync(state => state.Id == 1, cancellationToken);
        if (playback != null)
        {
            playback.CurrentQueueItemId = null;
            playback.StartedAt = null;
            playback.IsPaused = false;
            playback.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync(cancellationToken);
        }

        lock (_planLock)
        {
            _activeBreakId = null;
            _currentPlan = null;
            _spotifyTracks = [];
            _lastPlayingTrackIndex = -1;
            _queueSubmitted = false;
        }
    }

    private async Task<SystemStatusDto> BroadcastSchoolStatusIfChangedAsync(IServiceScope scope, CancellationToken cancellationToken)
    {
        var status = await scope.ServiceProvider.GetRequiredService<ISystemStatusService>()
            .GetStatusAsync(cancellationToken);
        if (_lastKioskStatus == null || HasKioskStatusChanged(_lastKioskStatus, status))
        {
            await _hubContext.Clients.Group(QueueHub.KioskGroup)
                .SendAsync("SchoolStatusChanged", status, cancellationToken);
            _lastKioskStatus = status;
        }
        return status;
    }

    private async Task<TimeSpan> CalculateNextDelayAsync(
        schoolmusicContext db,
        SystemStatusDto status,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (_activeBreakId.HasValue || _localPlaybackActive || _pausePending || _resumePending || status.CurrentState == SchoolState.Break)
        {
            return TimeSpan.FromSeconds(5);
        }

        try
        {
            DateTime? nextTargetTime = status.NextBreakStartsAt;

            var nextEventTime = await db.SpecialEvents
                .Where(e => e.EventStatus == "pending" && e.StartsAt > now)
                .OrderBy(e => e.StartsAt)
                .Select(e => (DateTime?)e.StartsAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (nextEventTime.HasValue)
            {
                if (!nextTargetTime.HasValue || nextEventTime.Value < nextTargetTime.Value)
                {
                    nextTargetTime = nextEventTime.Value;
                }
            }

            if (nextTargetTime.HasValue)
            {
                var secondsToTarget = (nextTargetTime.Value - now).TotalSeconds;
                if (secondsToTarget <= 15)
                {
                    return TimeSpan.FromSeconds(5);
                }

                var delaySeconds = Math.Clamp(secondsToTarget - 10, 5, 60);
                return TimeSpan.FromSeconds(delaySeconds);
            }

            return TimeSpan.FromMinutes(2);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to calculate adaptive delay; falling back to 5 seconds.");
            return TimeSpan.FromSeconds(5);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _wakeUpSignal.Writer.TryComplete();
    }

    private static bool HasKioskStatusChanged(SystemStatusDto previous, SystemStatusDto current) =>
        previous.CurrentState != current.CurrentState ||
        previous.BreakEndsAt != current.BreakEndsAt ||
        previous.NextBreakStartsAt != current.NextBreakStartsAt ||
        previous.CurrentBreakNumber != current.CurrentBreakNumber ||
        previous.NextBreakNumber != current.NextBreakNumber ||
        previous.MusicEnabled != current.MusicEnabled ||
        previous.MuteReason != current.MuteReason ||
        previous.NowPlaying?.QueueItemId != current.NowPlaying?.QueueItemId ||
        previous.NowPlaying?.IsPaused != current.NowPlaying?.IsPaused;

    private static async Task<bool> DetermineMusicEnabledAsync(
        schoolmusicContext db,
        DateOnly today,
        sbyte weekday,
        Break breakEntity,
        CancellationToken cancellationToken)
    {
        var exceptionBreak = await db.ExceptionBreaks.FirstOrDefaultAsync(item =>
            item.BreakDate == today && item.BreakNumber == breakEntity.BreakNumber, cancellationToken);
        if (exceptionBreak != null)
            return exceptionBreak.MusicEnabled;

        var weeklyBreak = await db.WeeklyBreakSettings.FirstOrDefaultAsync(item =>
            item.Weekday == weekday && item.BreakNumber == breakEntity.BreakNumber, cancellationToken);
        if (weeklyBreak != null)
            return weeklyBreak.MusicEnabled;

        var weeklySchedule = await db.WeeklySchedules.FirstOrDefaultAsync(item =>
            item.Weekday == weekday, cancellationToken);
        return weeklySchedule?.MusicEnabled ?? breakEntity.MusicEnabled;
    }

    private static bool IsSpotifyTrackId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        SpotifyId(value).Length is >= 10 and <= 64 &&
        SpotifyId(value).All(char.IsAsciiLetterOrDigit);

    private static string SpotifyId(string value) =>
        value.StartsWith("spotify:track:", StringComparison.Ordinal)
            ? value["spotify:track:".Length..]
            : value;
}
