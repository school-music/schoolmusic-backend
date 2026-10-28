using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Services;

/// <summary>Stan szkoły na kiosku i ekranie głównym aplikacji mobilnej.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SchoolState
{
    Lesson,
    Break,
    Holiday,
    AfterHours
}

public sealed record NowPlayingDto(
    int QueueItemId,
    int SongId,
    string Title,
    string ArtistName,
    string? Cover,
    int DurationMs,
    int VotesCount,
    DateTime? StartedAt,
    int ElapsedSeconds,
    int RemainingSeconds,
    bool IsPaused
);

public sealed record TopTrackDto(
    int Position,
    int QueueItemId,
    int SongId,
    string Title,
    string ArtistName,
    string? Cover,
    int DurationMs,
    int VotesCount
);


public sealed record SystemStatusDto(
    SchoolState CurrentState,
    DateTime ServerTime,
    DateTime? BreakEndsAt,
    DateTime? NextBreakStartsAt,
    int SecondsRemaining,
    int? CurrentBreakNumber,
    int? NextBreakNumber,
    bool MusicEnabled,
    string? MuteReason,
    string Message,
    NowPlayingDto? NowPlaying,
    IReadOnlyList<TopTrackDto> TopTracks
);

public interface ISystemStatusService
{
    Task<SystemStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    void InvalidateScheduleCache();
}

public sealed class SystemStatusService : ISystemStatusService
{
    private const int TopTracksCount = 5;

    private readonly schoolmusicContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly IMemoryCache? _cache;

    public SystemStatusService(schoolmusicContext context, TimeProvider timeProvider, IMemoryCache? cache = null)
    {
        _context = context;
        _timeProvider = timeProvider;
        _cache = cache;
    }

    public void InvalidateScheduleCache()
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        _cache?.Remove($"SystemStatus_ExceptionDay_{today:yyyyMMdd}");
        _cache?.Remove($"SystemStatus_Breaks_{today:yyyyMMdd}");
        _cache?.Remove("SystemStatus_FallbackBreaks");
    }

    public async Task<SystemStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetLocalNow().DateTime;
        var today = DateOnly.FromDateTime(now);

        sbyte weekday = (sbyte)now.DayOfWeek;
        if (weekday == 0) weekday = 7; 

        ExceptionDay? activeExceptionDay;
        if (_cache != null)
        {
            var cacheKey = $"SystemStatus_ExceptionDay_{today:yyyyMMdd}";
            activeExceptionDay = await _cache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
                return await _context.ExceptionDays
                    .AsNoTracking()
                    .Where(e => e.StartDate <= today && today <= e.EndDate)
                    .FirstOrDefaultAsync(cancellationToken);
            });
        }
        else
        {
            activeExceptionDay = await _context.ExceptionDays
                .AsNoTracking()
                .Where(e => e.StartDate <= today && today <= e.EndDate)
                .FirstOrDefaultAsync(cancellationToken);
        }

        List<Break> breaksForToday;
        if (_cache != null)
        {
            var cacheKey = $"SystemStatus_Breaks_{today:yyyyMMdd}";
            breaksForToday = (await _cache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
                return await _context.Breaks
                    .AsNoTracking()
                    .Where(b => b.BreakDate == today)
                    .ToListAsync(cancellationToken);
            })) ?? [];
        }
        else
        {
            breaksForToday = await _context.Breaks
                .AsNoTracking()
                .Where(b => b.BreakDate == today)
                .ToListAsync(cancellationToken);
        }

        bool isWeekend = weekday >= 6;
        bool isDayOff = activeExceptionDay?.DayType == "day_off" || (isWeekend && breaksForToday.Count == 0);

        if (isDayOff)
        {
            var reason = activeExceptionDay?.Description;
            return new SystemStatusDto(
                CurrentState: SchoolState.Holiday,
                ServerTime: now,
                BreakEndsAt: null,
                NextBreakStartsAt: null,
                SecondsRemaining: 0,
                CurrentBreakNumber: null,
                NextBreakNumber: null,
                MusicEnabled: false,
                MuteReason: reason,
                Message: string.IsNullOrWhiteSpace(reason) ? "Dzień wolny od zajęć." : $"Dzień wolny: {reason}.",
                NowPlaying: null,
                TopTracks: Array.Empty<TopTrackDto>()
            );
        }

        List<Break> scheduleRows;
        if (breaksForToday.Count > 0)
        {
            scheduleRows = breaksForToday;
        }
        else if (_cache != null)
        {
            scheduleRows = (await _cache.GetOrCreateAsync("SystemStatus_FallbackBreaks", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
                return (await _context.Breaks.AsNoTracking().OrderByDescending(b => b.BreakDate).ToListAsync(cancellationToken))
                    .DistinctBy(b => b.BreakNumber)
                    .ToList();
            })) ?? [];
        }
        else
        {
            scheduleRows = (await _context.Breaks.AsNoTracking().OrderByDescending(b => b.BreakDate).ToListAsync(cancellationToken))
                .DistinctBy(b => b.BreakNumber)
                .ToList();
        }

        var breaksToday = scheduleRows
            .Select(b => new
            {
                Entity = b,
                StartAt = today.ToDateTime(TimeOnly.FromDateTime(b.StartAt)),
                EndsAt = today.ToDateTime(TimeOnly.FromDateTime(b.EndsAt))
            })
            .OrderBy(b => b.StartAt)
            .ToList();

        var currentBreak = breaksToday.FirstOrDefault(b => now >= b.StartAt && now < b.EndsAt);
        var nextBreak = breaksToday
            .Where(b => b.StartAt > now)
            .OrderBy(b => b.StartAt)
            .FirstOrDefault();

        SchoolState state;
        DateTime? breakEndsAt = null;
        DateTime? nextBreakStartsAt = null;
        int secondsRemaining;

        if (currentBreak != null)
        {
            state = SchoolState.Break;
            breakEndsAt = currentBreak.EndsAt;
            secondsRemaining = Math.Max(0, (int)(currentBreak.EndsAt - now).TotalSeconds);
        }
        else if (nextBreak != null)
        {
            state = SchoolState.Lesson;
            nextBreakStartsAt = nextBreak.StartAt;
            secondsRemaining = Math.Max(0, (int)(nextBreak.StartAt - now).TotalSeconds);
        }
        else
        {
            state = SchoolState.AfterHours;
            secondsRemaining = 0;
        }

        bool musicEnabled = false;
        string? muteReason = null;

        if (activeExceptionDay is { DayType: "no_music" })
        {
            muteReason = activeExceptionDay.Description ?? "Dzień bez muzyki";
        }
        else if (currentBreak != null)
        {
            (musicEnabled, muteReason) = await ResolveMusicEnabledAsync(today, weekday, currentBreak.Entity, cancellationToken);
        }

        var nowPlaying = await GetNowPlayingAsync(_timeProvider.GetUtcNow().UtcDateTime, cancellationToken);

        int? targetBreakId = currentBreak?.Entity.Id
            ?? nextBreak?.Entity.Id
            ?? (breaksToday.Count > 0 ? breaksToday[^1].Entity.Id : null);

        var topTracks = await GetTopTracksAsync(targetBreakId, cancellationToken);

        return new SystemStatusDto(
            CurrentState: state,
            ServerTime: now,
            BreakEndsAt: breakEndsAt,
            NextBreakStartsAt: nextBreakStartsAt,
            SecondsRemaining: secondsRemaining,
            CurrentBreakNumber: currentBreak?.Entity.BreakNumber,
            NextBreakNumber: state == SchoolState.Lesson ? nextBreak?.Entity.BreakNumber : null,
            MusicEnabled: musicEnabled,
            MuteReason: muteReason,
            Message: BuildMessage(state, currentBreak?.Entity.BreakNumber, nextBreak?.Entity.BreakNumber, nextBreakStartsAt, musicEnabled, muteReason, nowPlaying),
            NowPlaying: nowPlaying,
            TopTracks: topTracks
        );
    }

    private async Task<(bool Enabled, string? Reason)> ResolveMusicEnabledAsync(
        DateOnly today,
        sbyte weekday,
        Break breakEntity,
        CancellationToken cancellationToken)
    {
        var exceptionBreak = await _context.ExceptionBreaks
            .AsNoTracking()
            .FirstOrDefaultAsync(eb => eb.BreakDate == today && eb.BreakNumber == breakEntity.BreakNumber, cancellationToken);
        if (exceptionBreak != null)
        {
            return (exceptionBreak.MusicEnabled, exceptionBreak.MusicEnabled ? null : exceptionBreak.Description ?? "Wyjątek kalendarzowy");
        }

        var weeklyBreak = await _context.WeeklyBreakSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(wb => wb.Weekday == weekday && wb.BreakNumber == breakEntity.BreakNumber, cancellationToken);
        if (weeklyBreak != null)
        {
            return (weeklyBreak.MusicEnabled, weeklyBreak.MusicEnabled ? null : "Muzyka wyłączona w harmonogramie tygodniowym dla tej przerwy");
        }

        var weeklyDay = await _context.WeeklySchedules
            .AsNoTracking()
            .FirstOrDefaultAsync(ws => ws.Weekday == weekday, cancellationToken);
        if (weeklyDay != null)
        {
            return (weeklyDay.MusicEnabled, weeklyDay.MusicEnabled ? null : "Muzyka wyłączona w harmonogramie tygodniowym dla tego dnia");
        }

        return (breakEntity.MusicEnabled, breakEntity.MusicEnabled ? null : "Muzyka wyłączona w konfiguracji przerwy");
    }

    private async Task<NowPlayingDto?> GetNowPlayingAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var playbackState = await _context.PlaybackStates
            .AsNoTracking()
            .Include(p => p.CurrentQueueItem!)
                .ThenInclude(q => q.Song)
                    .ThenInclude(s => s.Artist)
            .Include(p => p.CurrentQueueItem!)
                .ThenInclude(q => q.Votes)
            .FirstOrDefaultAsync(p => p.Id == 1, cancellationToken);

        var item = playbackState?.CurrentQueueItem;
        if (playbackState == null || item == null)
        {
            return null;
        }

        var startedAt = playbackState.StartedAt;
        int elapsedSeconds = startedAt.HasValue
            ? Math.Max(0, (int)(utcNow - startedAt.Value).TotalSeconds)
            : 0;

        int durationSeconds = Math.Max(0, item.Song.DurationMs / 1000);

        return new NowPlayingDto(
            QueueItemId: item.Id,
            SongId: item.SongId,
            Title: item.Song.Title,
            ArtistName: item.Song.Artist?.Name ?? "Nieznany wykonawca",
            Cover: item.Song.Cover,
            DurationMs: item.Song.DurationMs,
            VotesCount: item.Votes.Count,
            StartedAt: playbackState.StartedAt,
            ElapsedSeconds: elapsedSeconds,
            RemainingSeconds: Math.Max(0, durationSeconds - elapsedSeconds),
            IsPaused: playbackState.IsPaused
        );
    }

    private async Task<IReadOnlyList<TopTrackDto>> GetTopTracksAsync(int? breakId, CancellationToken cancellationToken)
    {
        if (breakId == null)
        {
            return Array.Empty<TopTrackDto>();
        }

        var items = await _context.QueueItems
            .AsNoTracking()
            .Where(q => q.BreakId == breakId.Value && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
            .Include(q => q.Song)
                .ThenInclude(s => s.Artist)
            .Include(q => q.Votes)
            .ToListAsync(cancellationToken);

        return items
            .OrderByDescending(q => q.Votes.Count)
            .ThenBy(q => q.OrderIndex)
            .Take(TopTracksCount)
            .Select((q, index) => new TopTrackDto(
                Position: index + 1,
                QueueItemId: q.Id,
                SongId: q.SongId,
                Title: q.Song.Title,
                ArtistName: q.Song.Artist?.Name ?? "Nieznany wykonawca",
                Cover: q.Song.Cover,
                DurationMs: q.Song.DurationMs,
                VotesCount: q.Votes.Count
            ))
            .ToList();
    }

    private static string BuildMessage(
        SchoolState state,
        int? currentBreakNumber,
        int? nextBreakNumber,
        DateTime? nextBreakStartsAt,
        bool musicEnabled,
        string? muteReason,
        NowPlayingDto? nowPlaying)
    {
        switch (state)
        {
            case SchoolState.Break:
                if (!musicEnabled)
                {
                    return $"Trwa przerwa nr {currentBreakNumber}, ale muzyka jest wyłączona ({muteReason}).";
                }
                return nowPlaying != null
                    ? $"Trwa przerwa nr {currentBreakNumber}. Gra: {nowPlaying.Title} — {nowPlaying.ArtistName}."
                    : $"Trwa przerwa nr {currentBreakNumber}. Kolejka jest pusta.";

            case SchoolState.Lesson:
                return nextBreakNumber != null
                    ? $"Trwają lekcje. Kolejna przerwa nr {nextBreakNumber} o {nextBreakStartsAt:HH:mm}."
                    : "Trwają lekcje.";

            case SchoolState.AfterHours:
                return "Po godzinach grania. Radiowęzeł nie odtwarza muzyki.";

            default:
                return "Dzień wolny od zajęć.";
        }
    }
}
