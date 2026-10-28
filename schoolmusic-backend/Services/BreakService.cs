using schoolmusic_backend.Models;

namespace schoolmusic_backend.Services
{
    /// <summary>
    /// Reprezentuje pojedyńczy utwór zaplanowany do odtworzenia
    /// </summary>
    public record ScheduledTrackDto
    (    
        int QueueItemId,
        int SongId,    
        string Title,
        string Artist,
        string Cover,
        int DurationMs,
        int VotesCount,
        int StartOffsetSeconds,
        int PlayOrder
    );

    /// <summary>
    /// Reprezentuje wyliczony harmonogram odtwarzania
    /// muzyki na danej przerwie
    /// </summary>
    public record ScheduledBreakPlan(
        int BreakId,
        int TotalBreakDurationSeconds,
        int ScheduledMusicDurationSeconds,
        int RemainingSeconds,
        List<ScheduledTrackDto> Tracks
    );

    public interface IBreakService
    {
        ScheduledBreakPlan CalculateBreak(int breakId, int breakDurationSeconds, List<QueueItem> approvedQueue);
    }

    /// <summary>
    /// Serwis wyliczający harmonogram do odtwarzania utworów na przerwę
    /// </summary>
    public class BreakService : IBreakService
    {
        /// <summary>
        /// Wylicza optymalny harmonogram odtwarzania utworów na daną przerwę,
        /// uwzględniając liczbę głosów oraz dopasowanie czasu piosenek do czasu przerwy
        /// </summary>
        public ScheduledBreakPlan CalculateBreak(int breakId, int breakDurationSeconds, List<QueueItem> approvedQueue)
        {
            if (breakDurationSeconds <= 0)
            {
                return new ScheduledBreakPlan(breakId, Math.Max(0, breakDurationSeconds), 0, Math.Max(0, breakDurationSeconds), new List<ScheduledTrackDto>());
            }
            if (approvedQueue == null || approvedQueue.Count == 0)
            {
                return new ScheduledBreakPlan(breakId, breakDurationSeconds, 0, breakDurationSeconds, new List<ScheduledTrackDto>());
            }
            int currentOffset = 0;
            var playlist = new List<ScheduledTrackDto>();

            List<QueueItem> pool = approvedQueue
                .OrderByDescending(q => q.Votes.Count)
                .ThenBy(q => q.OrderIndex)
                .ToList();

            // Po restarcie trwający utwór musi pozostać w planie nawet wtedy, gdy jego pełna długość
            // jest większa od czasu pozostałego do końca przerwy. Faktyczny postęp uzgadnia scheduler ze Spotify.
            var currentlyPlaying = pool.FirstOrDefault(item => item.Status == QueueItemStatuses.Playing);
            if (currentlyPlaying != null)
            {
                int durationSec = currentlyPlaying.Song.DurationMs / 1000;
                playlist.Add(new ScheduledTrackDto(
                    currentlyPlaying.Id,
                    currentlyPlaying.SongId,
                    currentlyPlaying.Song.Title,
                    currentlyPlaying.Song.Artist?.Name ?? "",
                    currentlyPlaying.Song.Cover ?? "",
                    currentlyPlaying.Song.DurationMs,
                    currentlyPlaying.Votes.Count,
                    0,
                    1));
                currentOffset = Math.Min(durationSec, breakDurationSeconds);
                pool.Remove(currentlyPlaying);
            }

            for(int i = 0; i < pool.Count; i++)
            {
                var item = pool[i];
                double durationSec = item.Song.DurationMs / 1000d;
                if (currentOffset + durationSec <= breakDurationSeconds)
                {
                    playlist.Add(new ScheduledTrackDto(
                        item.Id,
                        item.SongId,
                        item.Song.Title,
                        item.Song.Artist?.Name ?? "",
                        item.Song.Cover ?? "",
                        item.Song.DurationMs,
                        item.Votes.Count,
                        currentOffset,
                        playlist.Count + 1
                    ));

                    currentOffset += (int)durationSec;
                    pool.RemoveAt(i);
                    i--;
                }
            }

            // Gap filler: dobieramy piosenkę o czasie najbardziej zbliżonym do pozostałego czasu przerwy
            int remainingSeconds = breakDurationSeconds - currentOffset;
            if (remainingSeconds > 0 && pool.Count > 0)
            {
                var gapFiller = pool
                    .Where(q => (q.Song.DurationMs / 1000) <= remainingSeconds)
                    .OrderBy(q => Math.Abs((q.Song.DurationMs / 1000) - remainingSeconds))
                    .ThenByDescending(q => q.Votes.Count)
                    .ThenBy(q => q.OrderIndex)
                    .FirstOrDefault();

                if (gapFiller != null)
                {
                    int durationSec = gapFiller.Song.DurationMs / 1000;

                    playlist.Add(new ScheduledTrackDto(
                        gapFiller.Id,
                        gapFiller.SongId,
                        gapFiller.Song.Title,
                        gapFiller.Song.Artist?.Name ?? "",
                        gapFiller.Song.Cover ?? "",
                        gapFiller.Song.DurationMs,
                        gapFiller.Votes.Count,
                        currentOffset,
                        playlist.Count + 1
                    ));

                    currentOffset += durationSec;
                    pool.Remove(gapFiller);
                }
            }

            return new ScheduledBreakPlan(
                breakId,
                breakDurationSeconds,
                currentOffset,
                Math.Max(0, breakDurationSeconds - currentOffset),
                playlist
            );
        }
    }
}
