using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Services
{
    public interface IRadioStatusService
    {
        Task<object> GetNowPlayingAsync();
    }

    /// <summary>
    /// Udostępnia aktualny stan radiowęzła — informację czy odtwarzane jest nagranie, czy radiowęzeł jest wyciszony,
    /// bieżący utwór oraz dane o trwającej lub nadchodzącej przerwie.
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

        /// <summary>
        /// Zwraca aktualny stan odtwarzania radiowęzła: czy odtwarzanie trwa, czy jest wyciszone, komunikat, dane
        /// aktualnego utworu oraz informacje o trwającej lub nadchodzącej przerwie.
        /// </summary>
        /// <returns>Task zawierający anonimowy obiekt z polami: isPlaying (bool), isMuted (bool), message (string), currentTrack
        /// (obiekt z danymi utworu lub null), breakInfo (obiekt z informacjami o przerwie lub null). W przypadku dni
        /// wyjątkowych lub wyciszonych przerw obiekt może dodatkowo zawierać pole reason z opisem powodu.</returns>
        public async Task<object> GetNowPlayingAsync()
        {
            var now = _timeProvider.GetLocalNow().DateTime;
            var today = DateOnly.FromDateTime(now);

            // Sprawdzenie dnia wyjątkowego (np. żałoba, dzień bez muzyki)
            var activeExceptionDay = await _context.ExceptionDays
                .Where(e => e.StartDate <= today && today <= e.EndDate)
                .FirstOrDefaultAsync();

            if (activeExceptionDay != null)
            {
                return new
                {
                    isPlaying = false,
                    isMuted = true,
                    reason = activeExceptionDay.Description,
                    message = $"Radiowęzeł wyłączony: {activeExceptionDay.Description}.",
                    currentTrack = (object?)null,
                    breakInfo = (object?)null
                };
            }

            // Pobranie harmonogramu przerw na dziś
            var allBreaks = await _context.Breaks.OrderBy(b => b.BreakNumber).ToListAsync();

            var todayBreaks = allBreaks.Select(b => new
            {
                Entity = b,
                StartAt = today.ToDateTime(TimeOnly.FromDateTime(b.StartAt)),
                EndsAt = today.ToDateTime(TimeOnly.FromDateTime(b.EndsAt))
            }).ToList();

            var currentBreak = todayBreaks.FirstOrDefault(b => now >= b.StartAt && now <= b.EndsAt);
            var nextBreak = todayBreaks.Where(b => b.StartAt > now).OrderBy(b => b.StartAt).FirstOrDefault();

            // Sprawdzenie czy muzyka jest włączona na bieżącej przerwie wg hierarchii priorytetów
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

            // Jeśli trwa przerwa
            if (currentBreak != null)
            {
                if (!isMusicAllowed)
                {
                    return new
                    {
                        isPlaying = false,
                        isMuted = true,
                        reason = muteReason,
                        message = $"Trwa przerwa nr {currentBreak.Entity.BreakNumber}, ale muzyka jest wyłączona ({muteReason}).",
                        currentTrack = (object?)null,
                        breakInfo = new
                        {
                            breakId = currentBreak.Entity.Id,
                            breakNumber = currentBreak.Entity.BreakNumber,
                            endsAt = currentBreak.EndsAt,
                            secondsRemaining = (int)(currentBreak.EndsAt - now).TotalSeconds
                        }
                    };
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

                return new
                {
                    isPlaying = topTrack != null,
                    isMuted = false,
                    message = topTrack != null
                        ? $"Trwa odtwarzanie na przerwie nr {currentBreak.Entity.BreakNumber}"
                        : "Trwa przerwa, ale kolejka utworów jest pusta.",
                    currentTrack = topTrack == null ? null : new
                    {
                        queueItemId = topTrack.Id,
                        songId = topTrack.SongId,
                        title = topTrack.Song.Title,
                        artistName = topTrack.Song.Artist?.Name ?? "Nieznany wykonawca",
                        cover = topTrack.Song.Cover,
                        durationMs = topTrack.Song.DurationMs,
                        votesCount = topTrack.Votes.Count,
                        submittedBy = topTrack.User?.UserLogin ?? "Uczeń"
                    },
                    breakInfo = new
                    {
                        breakId = currentBreak.Entity.Id,
                        breakNumber = currentBreak.Entity.BreakNumber,
                        endsAt = currentBreak.EndsAt,
                        secondsRemaining = (int)(currentBreak.EndsAt - now).TotalSeconds
                    }
                };
            }

            // Jeśli trwają lekcje (oczekiwanie na kolejną przerwę)
            object? upcomingLeadTrack = null;
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
                    upcomingLeadTrack = new
                    {
                        queueItemId = leadingTrack.Id,
                        songId = leadingTrack.SongId,
                        title = leadingTrack.Song.Title,
                        artistName = leadingTrack.Song.Artist?.Name ?? "Nieznany wykonawca",
                        cover = leadingTrack.Song.Cover,
                        durationMs = leadingTrack.Song.DurationMs,
                        votesCount = leadingTrack.Votes.Count
                    };
                }
            }

            return new
            {
                isPlaying = false,
                isMuted = false,
                message = nextBreak != null
                    ? $"Trwają lekcje. Kolejna przerwa (nr {nextBreak.Entity.BreakNumber}) rozpocznie się o {nextBreak.StartAt:HH:mm}."
                    : "Wszystkie przerwy na dziś dobiegły końca.",
                currentTrack = (object?)null,
                breakInfo = nextBreak == null ? null : new
                {
                    nextBreakId = nextBreak.Entity.Id,
                    nextBreakNumber = nextBreak.Entity.BreakNumber,
                    startsAt = nextBreak.StartAt,
                    startsInMinutes = (int)Math.Max(0, (nextBreak.StartAt - now).TotalMinutes),
                    leadingSong = upcomingLeadTrack
                }
            };
        }
    }
}
