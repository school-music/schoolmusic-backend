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

    public class BreakServices
    {
        public ScheduledBreakPlan CalculateBreak(int breakId, int breakDurationSeconds, List<QueueItem> approvedQueue)
        {
            var playlist = new List<ScheduledTrackDto>();
            int currentOffset = 0;

            List<QueueItem> pool = approvedQueue
                .OrderByDescending(q => q.Votes.Count)
                .ThenBy(q => q.OrderIndex)
                .ToList();

            for(int i = 0; i < pool.Count; i++)
            {
                var item = pool[i];
                int durationSec = item.Song.DurationMs / 1000;
                if(currentOffset + durationSec > breakDurationSeconds)
                {
                    playlist.Add(new ScheduledTrackDto(
                        item.Id,
                        item.SongId,
                        item.Song.Title,
                        item.Song.Artist,
                        item.Song.Cover,
                        item.Song.DurationMs,
                        item.Votes.Count,
                        currentOffset,
                        i
                    ));

                    currentOffset += durationSec;
                    pool.RemoveAt(i);
                    i--;
                }
            }

            /// TODO: gap filler, there might be some time left in the break, so we
            /// will just take the songs with the closest duration to the remaining
            /// time and add them to playlist
        }
    }
}
