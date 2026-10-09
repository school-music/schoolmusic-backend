using schoolmusic_backend.Models;
using System.Linq.Expressions;

namespace schoolmusic_backend.Services
{
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

    public class BreakService : IBreakService
    {
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
                        item.Song.Cover,
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
                        gapFiller.Song.Cover,
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