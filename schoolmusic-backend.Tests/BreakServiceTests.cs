using schoolmusic_backend.Models;
using schoolmusic_backend.Services;
using Xunit;

namespace schoolmusic_backend.Tests
{
public class BreakServiceTests
    {
        private readonly BreakService _breakService = new();

        [Fact]
        public void CalculateBreak_WithEmptyQueue_ReturnsEmptyPlanWithFullRemainingSeconds()
        {
            // Arrange
            int breakId = 1;
            int breakDurationSeconds = 600; // 10 minut
            var queue = new List<QueueItem>();

            // Act
            var plan = _breakService.CalculateBreak(breakId, breakDurationSeconds, queue);

            // Assert
            Assert.NotNull(plan);
            Assert.Equal(breakId, plan.BreakId);
            Assert.Equal(breakDurationSeconds, plan.TotalBreakDurationSeconds);
            Assert.Equal(0, plan.ScheduledMusicDurationSeconds);
            Assert.Equal(breakDurationSeconds, plan.RemainingSeconds);
            Assert.Empty(plan.Tracks);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-10)]
        public void CalculateBreak_WithZeroOrNegativeDuration_ReturnsZeroDurationPlan(int duration)
        {
            // Arrange
            var queue = new List<QueueItem>
            {
                CreateQueueItem(1, 1, "Track 1", 180000, 5)
            };

            // Act
            var plan = _breakService.CalculateBreak(1, duration, queue);

            // Assert
            Assert.NotNull(plan);
            Assert.Empty(plan.Tracks);
            Assert.Equal(0, plan.ScheduledMusicDurationSeconds);
        }

        [Fact]
        public void CalculateBreak_PicksHigherVotedTracksFirst()
        {
            // Arrange: 3 utwory, każdy po 180s (3 min). Przerwa: 400s (zmieści 2 utwory).
            // Track A: 2 głosy, Track B: 10 głosów, Track C: 5 głosów
            var trackA = CreateQueueItem(1, 101, "Low Votes", 180000, 2);
            var trackB = CreateQueueItem(2, 102, "High Votes", 180000, 10);
            var trackC = CreateQueueItem(3, 103, "Medium Votes", 180000, 5);

            var queue = new List<QueueItem> { trackA, trackB, trackC };

            // Act
            var plan = _breakService.CalculateBreak(1, 400, queue);

            // Assert
            Assert.Equal(2, plan.Tracks.Count);
            // Pierwszy powinien być trackB (10 głosów)
            Assert.Equal("High Votes", plan.Tracks[0].Title);
            Assert.Equal(1, plan.Tracks[0].PlayOrder);
            Assert.Equal(0, plan.Tracks[0].StartOffsetSeconds);

            // Drugi powinien być trackC (5 głosów)
            Assert.Equal("Medium Votes", plan.Tracks[1].Title);
            Assert.Equal(2, plan.Tracks[1].PlayOrder);
            Assert.Equal(180, plan.Tracks[1].StartOffsetSeconds);

            // Czas muzyki: 360s, pozostało 40s
            Assert.Equal(360, plan.ScheduledMusicDurationSeconds);
            Assert.Equal(40, plan.RemainingSeconds);
        }

        [Fact]
        public void CalculateBreak_WhenBreakIsShorterThanAnyTrack_ReturnsEmptyTracks()
        {
            // Arrange: Utwór 200 sekund, przerwa tylko 100 sekund
            var track = CreateQueueItem(1, 1, "Long Song", 200000, 10);
            var queue = new List<QueueItem> { track };

            // Act
            var plan = _breakService.CalculateBreak(1, 100, queue);

            // Assert
            Assert.Empty(plan.Tracks);
            Assert.Equal(0, plan.ScheduledMusicDurationSeconds);
            Assert.Equal(100, plan.RemainingSeconds);
        }

        [Fact]
        public void CalculateBreak_AfterRestart_KeepsCurrentlyPlayingTrackEvenWhenItExceedsRemainingTime()
        {
            var track = CreateQueueItem(1, 1, "Long Song", 200000, 10);
            track.Status = QueueItemStatuses.Playing;

            var plan = _breakService.CalculateBreak(1, 100, [track]);

            var scheduled = Assert.Single(plan.Tracks);
            Assert.Equal(track.Id, scheduled.QueueItemId);
            Assert.Equal(100, plan.ScheduledMusicDurationSeconds);
            Assert.Equal(0, plan.RemainingSeconds);
        }

        [Fact]
        public void CalculateBreak_ExactFit_SchedulesAllTracksWithZeroRemaining()
        {
            // Arrange: 2 utwory po 150s (2.5 min) = 300s. Przerwa dokładnie 300s.
            var track1 = CreateQueueItem(1, 1, "Song 1", 150000, 5);
            var track2 = CreateQueueItem(2, 2, "Song 2", 150000, 3);

            var queue = new List<QueueItem> { track1, track2 };

            // Act
            var plan = _breakService.CalculateBreak(1, 300, queue);

            // Assert
            Assert.Equal(2, plan.Tracks.Count);
            Assert.Equal(300, plan.ScheduledMusicDurationSeconds);
            Assert.Equal(0, plan.RemainingSeconds);
        }

        private static QueueItem CreateQueueItem(int id, int songId, string title, int durationMs, int voteCount)
        {
            var votes = new List<Vote>();
            for (int i = 0; i < voteCount; i++)
            {
                votes.Add(new Vote { Id = i + 1, UserId = i + 1, QueueItemId = id });
            }

            return new QueueItem
            {
                Id = id,
                SongId = songId,
                BreakId = 1,
                ModerationStatus = "approved",
                Status = QueueItemStatuses.Queued,
                OrderIndex = id,
                Song = new Song
                {
                    Id = songId,
                    Title = title,
                    DurationMs = durationMs,
                    Cover = "https://example.com/cover.jpg",
                    Artist = new Artist { Id = 1, Name = "Test Artist", Source = "other" }
                },
                Votes = votes
            };
        }
    }
}
