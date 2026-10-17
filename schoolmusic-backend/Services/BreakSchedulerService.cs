using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Hubs;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Services
{
    public interface IBreakSchedulerService
    {
        Task<bool> SkipCurrentTrackAsync();
        ScheduledBreakPlan? GetCurrentPlan();
        int? GetActiveBreakId();
    }

    /// <summary>
    /// Usługa działająca w tle, monitorująca cykl życia przerw i automatycznie sterująca odtwarzaniem muzyki
    /// </summary>
    public class BreakSchedulerService : BackgroundService, IBreakSchedulerService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IAudioPlayerService _playerService;
        private readonly IHubContext<QueueHub> _hubContext;
        private readonly ILogger<BreakSchedulerService> _logger;
        private SystemStatusDto? _lastKioskStatus;

        private int? _activeBreakId = null;
        private ScheduledBreakPlan? _currentPlan = null;
        private int _currentTrackIndex = -1;
        private readonly object _planLock = new();

        public BreakSchedulerService(
            IServiceScopeFactory scopeFactory,
            IAudioPlayerService playerService,
            IHubContext<QueueHub> hubContext,
            ILogger<BreakSchedulerService> logger)
        {
            _scopeFactory = scopeFactory;
            _playerService = playerService;
            _hubContext = hubContext;
            _logger = logger;

            _playerService.MediaEnded += OnMediaEnded;
        }

        public ScheduledBreakPlan? GetCurrentPlan()
        {
            lock (_planLock)
            {
                return _currentPlan;
            }
        }

        public int? GetActiveBreakId()
        {
            lock (_planLock)
            {
                return _activeBreakId;
            }
        }

        public async Task<bool> SkipCurrentTrackAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();

            ScheduledTrackDto? currentTrack = null;
            lock (_planLock)
            {
                if (_currentPlan != null && _currentTrackIndex >= 0 && _currentTrackIndex < _currentPlan.Tracks.Count)
                {
                    currentTrack = _currentPlan.Tracks[_currentTrackIndex];
                }
            }

            if (currentTrack != null)
            {
                var queueItem = await db.QueueItems.FindAsync(currentTrack.QueueItemId);
                if (queueItem != null)
                {
                    queueItem.Status = "skipped";
                    await db.SaveChangesAsync();
                }
            }

            await _playerService.StopAsync();
            await PlayNextTrackAsync(scope);
            return true;
        }

        private void OnMediaEnded()
        {
            _logger.LogInformation("Zdarzenie MediaEnded: utwór zakończony. Przełączanie na następny.");
            Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();

                ScheduledTrackDto? finishedTrack = null;
                lock (_planLock)
                {
                    if (_currentPlan != null && _currentTrackIndex >= 0 && _currentTrackIndex < _currentPlan.Tracks.Count)
                    {
                        finishedTrack = _currentPlan.Tracks[_currentTrackIndex];
                    }
                }

                if (finishedTrack != null)
                {
                    var queueItem = await db.QueueItems.FindAsync(finishedTrack.QueueItemId);
                    if (queueItem != null && queueItem.Status == "playing")
                    {
                        queueItem.Status = "played";
                        await db.SaveChangesAsync();
                    }
                }

                await PlayNextTrackAsync(scope);
            });
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("BreakSchedulerService został uruchomiony.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Błąd w pętli BreakSchedulerService: {Message}", ex.Message);
                }

                await Task.Delay(5000, stoppingToken);
            }

            _logger.LogInformation("BreakSchedulerService zatrzymany.");
        }

        private async Task TickAsync(CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var breakService = scope.ServiceProvider.GetRequiredService<IBreakService>();
            var queueService = scope.ServiceProvider.GetRequiredService<IQueueService>();

            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            sbyte weekday = (sbyte)now.DayOfWeek;
            if (weekday == 0) weekday = 7; // ISO: 1 = Poniedziałek, 7 = Niedziela

            // 1. Sprawdzenie exception_day (dzień wolny / zakaz muzyki)
            bool isDayOff = await db.ExceptionDays.AnyAsync(e =>
                e.StartDate <= today && today <= e.EndDate &&
                (e.DayType == "day_off" || e.DayType == "no_music"), stoppingToken);

            if (isDayOff)
            {
                if (_activeBreakId != null)
                {
                    _logger.LogInformation("Dzień oznaczony jako wolny/bez muzyki. Zatrzymywanie odtwarzacza.");
                    await CleanupBreakAsync(db, queueService, _activeBreakId.Value);
                }
                await BroadcastSchoolStatusIfChangedAsync(scope, stoppingToken);
                return;
            }

            // 2. Wyszukanie aktualnie trwającej przerwy
            var activeBreak = await db.Breaks.FirstOrDefaultAsync(b =>
                b.BreakDate == today && b.StartAt <= now && now < b.EndsAt, stoppingToken);

            // 3. Sprawdzenie czy poprzednia przerwa dobiegła końca
            if (_activeBreakId != null && (activeBreak == null || activeBreak.Id != _activeBreakId))
            {
                _logger.LogInformation("Przerwa {BreakId} zakończyła się. Archiwizacja i zatrzymanie.", _activeBreakId);
                await CleanupBreakAsync(db, queueService, _activeBreakId.Value);
            }

            if (activeBreak == null)
            {
                await BroadcastSchoolStatusIfChangedAsync(scope, stoppingToken);
                return;
            }

            // 4. Sprawdzenie czy muzyka jest włączona wg hierarchii priorytetów
            bool musicEnabled = await DetermineMusicEnabledAsync(db, today, weekday, activeBreak, stoppingToken);
            if (!musicEnabled)
            {
                if (_activeBreakId != null)
                {
                    _logger.LogInformation("Muzyka wyłączona dla przerwy {BreakId}. Zatrzymywanie odtwarzania.", activeBreak.Id);
                    await CleanupBreakAsync(db, queueService, _activeBreakId.Value);
                }
                await BroadcastSchoolStatusIfChangedAsync(scope, stoppingToken);
                return;
            }

            // 5. Start nowej przerwy
            if (_activeBreakId == null)
            {
                _logger.LogInformation("Rozpoczynanie odtwarzania dla przerwy #{BreakNo} (ID: {BreakId})",
                    activeBreak.BreakNumber, activeBreak.Id);

                int remainingSeconds = Math.Max(0, (int)(activeBreak.EndsAt - now).TotalSeconds);

                var approvedQueue = await db.QueueItems
                    .Where(q => q.BreakId == activeBreak.Id &&
                                (q.ModerationStatus == "approved" || q.ModerationStatus == null) &&
                                q.Status != "played" && q.Status != "skipped")
                    .Include(q => q.Song)
                        .ThenInclude(s => s.Artist)
                    .Include(q => q.Votes)
                    .ToListAsync(stoppingToken);

                var plan = breakService.CalculateBreak(activeBreak.Id, remainingSeconds, approvedQueue);

                lock (_planLock)
                {
                    _activeBreakId = activeBreak.Id;
                    _currentPlan = plan;
                    _currentTrackIndex = -1;
                }

                await PlayNextTrackAsync(scope);
            }
            else
            {
                // Przerwa trwa - sprawdzamy czy odtwarzacz nie zatrzymał się niespodziewanie
                var status = _playerService.GetStatus();
                if (!status.IsPlaying && !status.IsPaused)
                {
                    bool hasMoreTracks = false;
                    lock (_planLock)
                    {
                        hasMoreTracks = _currentPlan != null && (_currentTrackIndex + 1) < _currentPlan.Tracks.Count;
                    }

                    if (hasMoreTracks)
                    {
                        _logger.LogInformation("Odtwarzacz bezczynny w trakcie przerwy. Uruchamianie kolejnego utworu.");
                        await PlayNextTrackAsync(scope);
                    }
                }
            }

            await BroadcastSchoolStatusIfChangedAsync(scope, stoppingToken);
        }

        private async Task BroadcastSchoolStatusIfChangedAsync(IServiceScope scope, CancellationToken cancellationToken)
        {
            var statusService = scope.ServiceProvider.GetRequiredService<ISystemStatusService>();
            var status = await statusService.GetStatusAsync(cancellationToken);
            var previous = _lastKioskStatus;
            if (previous != null && !HasKioskStatusChanged(previous, status))
            {
                return;
            }

            await _hubContext.Clients.Group(QueueHub.KioskGroup)
                .SendAsync("SchoolStatusChanged", status, cancellationToken);
            _lastKioskStatus = status;
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
            CancellationToken ct)
        {
            // 1. Wyjątek dla konkretnej przerwy w danym dniu
            var exceptionBreak = await db.ExceptionBreaks.FirstOrDefaultAsync(eb =>
                eb.BreakDate == today && eb.BreakNumber == breakEntity.BreakNumber, ct);
            if (exceptionBreak != null)
            {
                return exceptionBreak.MusicEnabled;
            }

            // 2. Ustawienie tygodniowe dla konkretnej przerwy
            var weeklyBreak = await db.WeeklyBreakSettings.FirstOrDefaultAsync(wb =>
                wb.Weekday == weekday && wb.BreakNumber == breakEntity.BreakNumber, ct);
            if (weeklyBreak != null)
            {
                return weeklyBreak.MusicEnabled;
            }

            // 3. Ustawienie tygodniowe dla dnia
            var weeklySchedule = await db.WeeklySchedules.FirstOrDefaultAsync(ws =>
                ws.Weekday == weekday, ct);
            if (weeklySchedule != null)
            {
                return weeklySchedule.MusicEnabled;
            }

            // 4. Domyślna flaga z rekordu przerwy
            return breakEntity.MusicEnabled;
        }

        private async Task PlayNextTrackAsync(IServiceScope scope)
        {
            ScheduledTrackDto? trackToPlay = null;
            int breakId = 0;

            lock (_planLock)
            {
                if (_currentPlan == null) return;
                _currentTrackIndex++;
                if (_currentTrackIndex < _currentPlan.Tracks.Count)
                {
                    trackToPlay = _currentPlan.Tracks[_currentTrackIndex];
                    breakId = _currentPlan.BreakId;
                }
            }

            if (trackToPlay == null)
            {
                _logger.LogInformation("Wszystkie zaplanowane utwory dla bieżącej przerwy zostały odtworzone.");
                return;
            }

            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var queueItem = await db.QueueItems
                .Include(q => q.Song)
                    .ThenInclude(s => s.Artist)
                .FirstOrDefaultAsync(q => q.Id == trackToPlay.QueueItemId);

            string? songUrl = queueItem?.Song.SongUrl;
            if (string.IsNullOrWhiteSpace(songUrl))
            {
                _logger.LogWarning("Brak adresu URL dla utworu {SongId} ({Title}), pomijanie.", trackToPlay.SongId, trackToPlay.Title);
                await PlayNextTrackAsync(scope);
                return;
            }

            _logger.LogInformation("Odtwarzanie: {Title} - {Artist} ({Url})", trackToPlay.Title, trackToPlay.Artist, songUrl);
            var result = await _playerService.PlayFileOrUrlAsync(songUrl, trackToPlay.Title, trackToPlay.Artist);

            if (result.Success)
            {
                if (queueItem != null)
                {
                    queueItem.Status = "playing";
                }

                var pb = await db.PlaybackStates.FirstOrDefaultAsync(p => p.Id == 1);
                if (pb == null)
                {
                    pb = new PlaybackState { Id = 1 };
                    db.PlaybackStates.Add(pb);
                }
                pb.CurrentQueueItemId = trackToPlay.QueueItemId;
                pb.StartedAt = DateTime.Now;
                pb.IsPaused = false;
                pb.UpdatedAt = DateTime.Now;

                await db.SaveChangesAsync();

                var queueService = scope.ServiceProvider.GetRequiredService<IQueueService>();
                await queueService.BroadcastQueueAsync(breakId);
            }
            else
            {
                _logger.LogWarning("Nie udało się odtworzyć utworu: {Message}. Przechodzenie do kolejnego.", result.Message);
                await PlayNextTrackAsync(scope);
            }
        }

        private async Task CleanupBreakAsync(schoolmusicContext db, IQueueService queueService, int breakId)
        {
            await _playerService.StopAsync();

            try
            {
                await queueService.ArchiveBreakAsync(breakId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Błąd podczas archiwizacji przerwy {BreakId}: {Message}", breakId, ex.Message);
            }

            var pb = await db.PlaybackStates.FirstOrDefaultAsync(p => p.Id == 1);
            if (pb != null)
            {
                pb.CurrentQueueItemId = null;
                pb.StartedAt = null;
                pb.IsPaused = false;
                pb.UpdatedAt = DateTime.Now;
                await db.SaveChangesAsync();
            }

            lock (_planLock)
            {
                _activeBreakId = null;
                _currentPlan = null;
                _currentTrackIndex = -1;
            }
        }
    }
}
