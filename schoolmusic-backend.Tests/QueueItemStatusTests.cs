using schoolmusic_backend.Models;

namespace schoolmusic_backend.Tests;

public sealed class QueueItemStatusTests
{
    [Theory]
    [InlineData(QueueItemStatuses.Queued, QueueItemStatuses.Playing, true)]
    [InlineData(QueueItemStatuses.Queued, QueueItemStatuses.Skipped, true)]
    [InlineData(QueueItemStatuses.Playing, QueueItemStatuses.Played, true)]
    [InlineData(QueueItemStatuses.Playing, QueueItemStatuses.Skipped, true)]
    [InlineData(QueueItemStatuses.Played, QueueItemStatuses.Playing, false)]
    [InlineData(QueueItemStatuses.Skipped, QueueItemStatuses.Playing, false)]
    public void TryTransition_AllowsOnlyForwardPlaybackTransitions(string current, string target, bool expected)
    {
        var item = new QueueItem { Status = current };

        var changed = QueueItemStatuses.TryTransition(item, target);

        Assert.Equal(expected, changed);
        Assert.Equal(expected ? target : current, item.Status);
    }
}
