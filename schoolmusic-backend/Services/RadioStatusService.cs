using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Services
{
    public interface IRadioStatusService
    {
        Task<object> GetNowPlayingAsync();
    }

    public class RadioStatusService : IRadioStatusService
    {
        private readonly schoolmusicContext _context;

        public RadioStatusService(schoolmusicContext context)
        {
            _context = context;
        }

        public async Task<object> GetNowPlayingAsync()
        {
            var now = DateTime.Now;

            // Sprawdzenie dnia wyjątkowego (np. żałoba, dzień bez muzyki)
            var activeExceptionDay = await _context.ExceptionDays
                .Where(e => e.StartAt <= now && (e.EndsAt == null || e.EndsAt.Value >= now))
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

            // Sprawdzenie wyciszonej przerwy (np. apel, egzamin)
            var activeExceptionBreak = await _context.ExceptionBreaks
                .Where(eb => eb.StartsAt <= now && (eb.EndsAt == null || eb.EndsAt.Value >= now))
                .FirstOrDefaultAsync();

            // Pobranie harmonogramu przerw na dziś
            var today = DateOnly.FromDateTime(now);
            var allBreaks = await _context.Breaks.OrderBy(b => b.BreakNumber ?? b.Id).ToListAsync();

            var todayBreaks = allBreaks.Select(b => new
            {
                Entity = b,
                StartAt = today.ToDateTime(TimeOnly.FromDateTime(b.StartAt)),
                EndsAt = b.EndsAt.HasValue
                    ? today.ToDateTime(TimeOnly.FromDateTime(b.EndsAt.Value))
                    : today.ToDateTime(TimeOnly.FromDateTime(b.StartAt))
            }).ToList();

            var currentBreak = todayBreaks.FirstOrDefault(b => now >= b.StartAt && now <= b.EndsAt);
            var nextBreak = todayBreaks.Where(b => b.StartAt > now).OrderBy(b => b.StartAt).FirstOrDefault();

            // Jeśli trwa przerwa
            if (currentBreak != null)
            {
                if (activeExceptionBreak != null)
                {
                    return new
                    {
                        isPlaying = false,
                        isMuted = true,
                        reason = activeExceptionBreak.Description,
                        message = $"Trwa przerwa nr {currentBreak.Entity.BreakNumber}, ale muzyka jest wyciszona ({activeExceptionBreak.Description}).",
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
                        submittedBy = topTrack.User.UserLogin
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
