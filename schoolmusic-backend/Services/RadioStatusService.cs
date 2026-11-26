using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Services
{
    public sealed record RadioCurrentTrackDto(
        int QueueItemId,
        int SongId,
        string Title,
        int ArtistId,
        string ArtistName,
        string? Cover,
        int DurationMs,
        string? Genre,
        string Source,
        string? SongUrl,
        string? SpotifyId,
        int VotesCount,
        string SubmittedBy
    );

    public sealed record RadioUpcomingLeadTrackDto(
        int QueueItemId,
        int SongId,
        string Title,
        int ArtistId,
        string ArtistName,
        string? Cover,
        int DurationMs,
        string? Genre,
        string Source,
        string? SongUrl,
        string? SpotifyId,
        int VotesCount
    );

    public sealed record RadioBreakInfoDto(
        int? BreakId = null,
        int? BreakNumber = null,
        DateTime? EndsAt = null,
        int? SecondsRemaining = null,
        int? NextBreakId = null,
        int? NextBreakNumber = null,
        DateTime? StartsAt = null,
        int? StartsInMinutes = null,
        RadioUpcomingLeadTrackDto? LeadingSong = null
    );

    public sealed record RadioNowPlayingResponseDto(
        bool IsPlaying,
        bool IsMuted,
        string? Reason,
        string Message,
        RadioCurrentTrackDto? CurrentTrack,
        RadioBreakInfoDto? BreakInfo
    );

    public interface IRadioStatusService
    {
        Task<object> GetNowPlayingAsync();
        Task<RadioNowPlayingResponseDto> GetNowPlayingDtoAsync();
    }

    /// <summary>
    /// Udostępnia aktualny stan radiowęzła — informację czy odtwarzane jest nagranie, czy radiowęzeł jest wyciszony,
    /// bieżący utwór wraz z metadanymi Spotify oraz dane o trwającej lub nadchodzącej przerwie.
    /// </summary>
    public class RadioStatusService : IRadioStatusService
    {
        private readonly schoolmusicContext _context;
        private readonly TimeProvider _timeProvider;

        public RadioStatusService(schoolmusicContext context, TimeProvider? timeProvider = null)
        {
            _context = context;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public async Task<object> GetNowPlayingAsync() => await GetNowPlayingDtoAsync();

        public async Task<RadioNowPlayingResponseDto> GetNowPlayingDtoAsync()
        {
            var now = _timeProvider.GetLocalNow().DateTime;
            var today = DateOnly.FromDateTime(now);

            var activeExceptionDay = await _context.ExceptionDays
                .Where(e => e.StartDate <= today && today <= e.EndDate)
                .FirstOrDefaultAsync();

            if (activeExceptionDay != null)
            {
                return new RadioNowPlayingResponseDto(
                    IsPlaying: false,
                    IsMuted: true,
                    Reason: activeExceptionDay.Description,
                    Message: $"Radiowęzeł wyłączony: {activeExceptionDay.Description}.",
                    CurrentTrack: null,
                    BreakInfo: null);
            }

            var allBreaks = await _context.Breaks.OrderBy(b => b.BreakNumber).ToListAsync();

            var todayBreaks = allBreaks.Select(b => new
            {
                Entity = b,
                StartAt = today.ToDateTime(TimeOnly.FromDateTime(b.StartAt)),
                EndsAt = today.ToDateTime(TimeOnly.FromDateTime(b.EndsAt))
            }).ToList();

            var currentBreak = todayBreaks.FirstOrDefault(b => now >= b.StartAt && now <= b.EndsAt);
            var nextBreak = todayBreaks.Where(b => b.StartAt > now).OrderBy(b => b.StartAt).FirstOrDefault();

            bool isMusicAllowed = true;
            string? muteReason = null;

            if (currentBreak != null)
            {
                sbyte weekday = (sbyte)now.DayOfWeek;
                if (weekday == 0) weekday = 7;

                var exceptionBreak = await _context.ExceptionBreaks
                    .FirstOrDefaultAsync(eb => eb.BreakDate == today && eb.BreakNumber == currentBreak.Entity.BreakNumber);

                if (exceptionBreak != null)
                {
                    isMusicAllowed = exceptionBreak.MusicEnabled;
                    if (!isMusicAllowed)
                    {
                        muteReason = exceptionBreak.Description ?? "Wyjątek kalendarzowy";
                    }
                }
                else
                {
                    var weeklyBreak = await _context.WeeklyBreakSettings
                        .FirstOrDefaultAsync(wb => wb.Weekday == weekday && wb.BreakNumber == currentBreak.Entity.BreakNumber);

                    if (weeklyBreak != null)
                    {
                        isMusicAllowed = weeklyBreak.MusicEnabled;
                        if (!isMusicAllowed)
                        {
                            muteReason = "Wyłączona w harmonogramie tygodniowym dla tej przerwy";
                        }
                    }
                    else
                    {
                        var weeklyDay = await _context.WeeklySchedules
                            .FirstOrDefaultAsync(ws => ws.Weekday == weekday);

                        if (weeklyDay != null)
                        {
                            isMusicAllowed = weeklyDay.MusicEnabled;
                            if (!isMusicAllowed)
                            {
                                muteReason = "Wyłączona w harmonogramie tygodniowym dla tego dnia";
                            }
                        }
                        else
                        {
                            isMusicAllowed = currentBreak.Entity.MusicEnabled;
                            if (!isMusicAllowed)
                            {
                                muteReason = "Wyłączona w konfiguracji przerwy";
                            }
                        }
                    }
                }
            }

            if (currentBreak != null)
            {
                var breakInfo = new RadioBreakInfoDto(
                    BreakId: currentBreak.Entity.Id,
                    BreakNumber: currentBreak.Entity.BreakNumber,
                    EndsAt: currentBreak.EndsAt,
                    SecondsRemaining: Math.Max(0, (int)(currentBreak.EndsAt - now).TotalSeconds));

                if (!isMusicAllowed)
                {
                    return new RadioNowPlayingResponseDto(
                        IsPlaying: false,
                        IsMuted: true,
                        Reason: muteReason,
                        Message: $"Trwa przerwa nr {currentBreak.Entity.BreakNumber}, ale muzyka jest wyłączona ({muteReason}).",
                        CurrentTrack: null,
                        BreakInfo: breakInfo);
                }

                var topTrack = await _context.QueueItems
                    .Where(q => q.BreakId == currentBreak.Entity.Id && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
                    .Include(q => q.Song)
                        .ThenInclude(s => s.Artist)
                    .Include(q => q.User)
                    .Include(q => q.Votes)
                    .OrderByDescending(q => q.Votes.Count)
                    .ThenBy(q => q.OrderIndex)
                    .FirstOrDefaultAsync();

                return new RadioNowPlayingResponseDto(
                    IsPlaying: topTrack != null,
                    IsMuted: false,
                    Reason: null,
                    Message: topTrack != null
                        ? $"Trwa odtwarzanie na przerwie nr {currentBreak.Entity.BreakNumber}"
                        : "Trwa przerwa, ale kolejka utworów jest pusta.",
                    CurrentTrack: topTrack == null ? null : new RadioCurrentTrackDto(
                        QueueItemId: topTrack.Id,
                        SongId: topTrack.SongId,
                        Title: topTrack.Song.Title,
                        ArtistId: topTrack.Song.ArtistId,
                        ArtistName: topTrack.Song.Artist?.Name ?? "Nieznany wykonawca",
                        Cover: topTrack.Song.Cover,
                        DurationMs: topTrack.Song.DurationMs,
                        Genre: topTrack.Song.Genre,
                        Source: topTrack.Song.Source,
                        SongUrl: topTrack.Song.SongUrl,
                        SpotifyId: topTrack.Song.SpotifyId,
                        VotesCount: topTrack.Votes.Count,
                        SubmittedBy: topTrack.User?.UserLogin ?? "Uczeń"),
                    BreakInfo: breakInfo);
            }

            RadioUpcomingLeadTrackDto? upcomingLeadTrack = null;
            if (nextBreak != null)
            {
                var leadingTrack = await _context.QueueItems
                    .Where(q => q.BreakId == nextBreak.Entity.Id && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
                    .Include(q => q.Song)
                        .ThenInclude(s => s.Artist)
                    .Include(q => q.User)
                    .Include(q => q.Votes)
                    .OrderByDescending(q => q.Votes.Count)
                    .ThenBy(q => q.OrderIndex)
                    .FirstOrDefaultAsync();

                if (leadingTrack != null)
                {
                    upcomingLeadTrack = new RadioUpcomingLeadTrackDto(
                        QueueItemId: leadingTrack.Id,
                        SongId: leadingTrack.SongId,
                        Title: leadingTrack.Song.Title,
                        ArtistId: leadingTrack.Song.ArtistId,
                        ArtistName: leadingTrack.Song.Artist?.Name ?? "Nieznany wykonawca",
                        Cover: leadingTrack.Song.Cover,
                        DurationMs: leadingTrack.Song.DurationMs,
                        Genre: leadingTrack.Song.Genre,
                        Source: leadingTrack.Song.Source,
                        SongUrl: leadingTrack.Song.SongUrl,
                        SpotifyId: leadingTrack.Song.SpotifyId,
                        VotesCount: leadingTrack.Votes.Count);
                }
            }

            return new RadioNowPlayingResponseDto(
                IsPlaying: false,
                IsMuted: false,
                Reason: null,
                Message: nextBreak != null
                    ? $"Trwają lekcje. Kolejna przerwa (nr {nextBreak.Entity.BreakNumber}) rozpocznie się o {nextBreak.StartAt:HH:mm}."
                    : "Wszystkie przerwy na dziś dobiegły końca.",
                CurrentTrack: null,
                BreakInfo: nextBreak == null ? null : new RadioBreakInfoDto(
                    NextBreakId: nextBreak.Entity.Id,
                    NextBreakNumber: nextBreak.Entity.BreakNumber,
                    StartsAt: nextBreak.StartAt,
                    StartsInMinutes: (int)Math.Max(0, (nextBreak.StartAt - now).TotalMinutes),
                    LeadingSong: upcomingLeadTrack));
        }
    }
}
