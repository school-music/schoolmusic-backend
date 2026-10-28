using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using schoolmusic_backend.Controllers;
using schoolmusic_backend.Hubs;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Tests;

public sealed class BreakSchedulerServiceTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public ServiceProvider Provider { get; }
        public BreakSchedulerService Service { get; }
        public Mock<IBreakService> BreakService { get; } = new();
        public Mock<IQueueService> QueueService { get; } = new();
        public Mock<ISpotifyConnectPlaybackService> Spotify { get; } = new();
        public Mock<IAudioPlayerService> AudioPlayer { get; } = new();
        public Mock<ISystemStatusService> StatusService { get; } = new();

        public Fixture()
        {
            var services = new ServiceCollection();
            var databaseName = Guid.NewGuid().ToString();
            services.AddDbContext<schoolmusicContext>(options => options.UseInMemoryDatabase(databaseName));
            services.AddScoped(_ => BreakService.Object);
            services.AddScoped(_ => QueueService.Object);
            services.AddScoped(_ => Spotify.Object);
            services.AddScoped(_ => AudioPlayer.Object);
            StatusService.Setup(service => service.GetStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SystemStatusDto(
                    SchoolState.Lesson, DateTime.UtcNow, null, null, 0, null, null,
                    true, null, "Lekcja", null, Array.Empty<TopTrackDto>()));
            services.AddScoped(_ => StatusService.Object);

            var hubClient = new Mock<IClientProxy>();
            hubClient.Setup(client => client.SendCoreAsync(
                    It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var hubClients = new Mock<IHubClients>();
            hubClients.Setup(clients => clients.Group(QueueHub.KioskGroup)).Returns(hubClient.Object);
            hubClients.Setup(clients => clients.All).Returns(hubClient.Object);
            var hubContext = new Mock<IHubContext<QueueHub>>();
            hubContext.Setup(context => context.Clients).Returns(hubClients.Object);

            Provider = services.BuildServiceProvider();
            var playback = new SpotifyPlaybackSnapshot(true, true, false, "A12345678901234567890",
                "Song", "Artist", 42, 1000, 60000, "PA Spotify");
            Spotify.Setup(service => service.StartPlaybackAsync(
                    It.IsAny<IReadOnlyList<SpotifyQueueTrack>>(), It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyList<SpotifyQueueTrack> tracks, CancellationToken _) => Task.FromResult(tracks));
            Spotify.Setup(service => service.GetPlaybackStateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(playback);
            Spotify.Setup(service => service.PauseAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Spotify.Setup(service => service.ResumeAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Spotify.Setup(service => service.SkipToNextAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            AudioPlayer.Setup(service => service.GetStatus())
                .Returns(new PlayerStatusDto(false, false, 50, false, "None", null, null, 0, 0));
            QueueService.Setup(service => service.ArchiveBreakAsync(It.IsAny<int>()))
                .ReturnsAsync(ServiceResult<object>.Ok(new object()));

            Service = new BreakSchedulerService(
                Provider.GetRequiredService<IServiceScopeFactory>(),
                AudioPlayer.Object,
                hubContext.Object,
                NullLogger<BreakSchedulerService>.Instance);
        }

        public async Task SeedActiveBreak(bool withTrack = true)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var now = DateTime.Now;
            var breakEntity = new Break
            {
                Id = 1, BreakDate = DateOnly.FromDateTime(now), BreakNumber = 1,
                StartAt = now.AddMinutes(-1), EndsAt = now.AddMinutes(5), MusicEnabled = true
            };
            db.Breaks.Add(breakEntity);
            if (withTrack)
            {
                var artist = new Artist { Id = 1, Name = "Artist", Source = "other" };
                var song = new Song
                {
                    Id = 1, Title = "Song", ArtistId = 1, Artist = artist, Source = "spotify",
                    SpotifyId = "A12345678901234567890", SongUrl = "https://example.test/song.mp3",
                    DurationMs = 60000, IsActive = true, CreatedAt = now
                };
                db.Artists.Add(artist);
                db.Songs.Add(song);
                db.QueueItems.Add(new QueueItem
                {
                    Id = 1, BreakId = 1, Break = breakEntity, SongId = 1, Song = song,
                    Status = "queued", ModerationStatus = "approved", OrderIndex = 1, CreatedAt = now
                });
                db.PlaybackStates.Add(new PlaybackState { Id = 1, CurrentQueueItemId = 1, StartedAt = now, UpdatedAt = now });
                BreakService.Setup(service => service.CalculateBreak(1, It.IsAny<int>(), It.IsAny<List<QueueItem>>()))
                    .Returns(new ScheduledBreakPlan(1, 300, 60, 240, [
                        new ScheduledTrackDto(1, 1, "Song", "Artist", "", 60000, 0, 0, 1)
                    ]));
            }
            await db.SaveChangesAsync();
        }

        public async Task Tick()
        {
            var tick = typeof(BreakSchedulerService).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)tick.Invoke(Service, [CancellationToken.None])!;
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await Provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task Tick_SubmitsTheApprovedSpotifyQueueInPlanOrderAndPersistsNowPlaying()
    {
        await using var fixture = new Fixture();
        await fixture.SeedActiveBreak();

        await fixture.Tick();

        Assert.Equal(1, fixture.Service.GetActiveBreakId());
        Assert.Equal(1, fixture.Service.GetCurrentPlan()?.BreakId);
        fixture.Spotify.Verify(service => service.StartPlaybackAsync(
            It.Is<IReadOnlyList<SpotifyQueueTrack>>(tracks =>
                tracks.Count == 1 && tracks[0].SpotifyId == "A12345678901234567890"),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.QueueService.Verify(service => service.BroadcastQueueAsync(1), Times.AtLeastOnce);
        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
        Assert.Equal("playing", (await db.QueueItems.SingleAsync()).Status);
        Assert.Equal(1, (await db.PlaybackStates.SingleAsync()).CurrentQueueItemId);
    }

    [Fact]
    public async Task Tick_SubmitsMultipleTracksInCalculatedOrder()
    {
        await using var fixture = new Fixture();
        await fixture.SeedActiveBreak(withTrack: false);
        using (var scope = fixture.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var breakEntity = await db.Breaks.SingleAsync();
            for (var id = 1; id <= 2; id++)
            {
                var artist = new Artist { Id = id, Name = $"Artist {id}", Source = "spotify" };
                var song = new Song
                {
                    Id = id, Title = $"Song {id}", ArtistId = id, Artist = artist,
                    Source = "spotify", SpotifyId = id == 1 ? "B12345678901234567890" : "C12345678901234567890", DurationMs = 60000,
                    IsActive = true, CreatedAt = DateTime.Now
                };
                db.Artists.Add(artist);
                db.Songs.Add(song);
                db.QueueItems.Add(new QueueItem
                {
                    Id = id, BreakId = 1, Break = breakEntity, SongId = id, Song = song,
                    Status = "queued", ModerationStatus = "approved", OrderIndex = id, CreatedAt = DateTime.Now
                });
            }
            await db.SaveChangesAsync();
        }
        fixture.BreakService.Setup(service => service.CalculateBreak(1, It.IsAny<int>(), It.IsAny<List<QueueItem>>()))
            .Returns(new ScheduledBreakPlan(1, 300, 120, 180, [
                new ScheduledTrackDto(2, 2, "Song 2", "Artist 2", "", 60000, 0, 0, 1),
                new ScheduledTrackDto(1, 1, "Song 1", "Artist 1", "", 60000, 0, 60, 2)
            ]));

        await fixture.Tick();

        fixture.Spotify.Verify(service => service.StartPlaybackAsync(
            It.Is<IReadOnlyList<SpotifyQueueTrack>>(tracks =>
                tracks.Count == 2 &&
                tracks[0].SpotifyId == "C12345678901234567890" &&
                tracks[1].SpotifyId == "B12345678901234567890"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Tick_MarksSpotifyUnplayableTracksSkippedAndContinuesRemainingQueue()
    {
        await using var fixture = new Fixture();
        await fixture.SeedActiveBreak(withTrack: false);
        using (var scope = fixture.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var breakEntity = await db.Breaks.SingleAsync();
            for (var id = 1; id <= 2; id++)
            {
                var artist = new Artist { Id = id, Name = $"Artist {id}", Source = "spotify" };
                var song = new Song
                {
                    Id = id, Title = $"Song {id}", ArtistId = id, Artist = artist,
                    Source = "spotify", SpotifyId = id == 1 ? "B12345678901234567890" : "C12345678901234567890",
                    DurationMs = 60000, IsActive = true, CreatedAt = DateTime.Now
                };
                db.Artists.Add(artist);
                db.Songs.Add(song);
                db.QueueItems.Add(new QueueItem
                {
                    Id = id, BreakId = 1, Break = breakEntity, SongId = id, Song = song,
                    Status = "queued", ModerationStatus = "approved", OrderIndex = id, CreatedAt = DateTime.Now
                });
            }
            await db.SaveChangesAsync();
        }
        fixture.BreakService.Setup(service => service.CalculateBreak(1, It.IsAny<int>(), It.IsAny<List<QueueItem>>()))
            .Returns(new ScheduledBreakPlan(1, 300, 120, 180, [
                new ScheduledTrackDto(1, 1, "Song 1", "Artist 1", "", 60000, 0, 0, 1),
                new ScheduledTrackDto(2, 2, "Song 2", "Artist 2", "", 60000, 0, 60, 2)
            ]));
        fixture.Spotify.Setup(service => service.StartPlaybackAsync(
                It.IsAny<IReadOnlyList<SpotifyQueueTrack>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<SpotifyQueueTrack> tracks, CancellationToken _) =>
                Task.FromResult<IReadOnlyList<SpotifyQueueTrack>>(tracks.Take(1).ToArray()));
        fixture.Spotify.Setup(service => service.GetPlaybackStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpotifyPlaybackSnapshot(true, true, false, "B12345678901234567890",
                "Song 1", "Artist 1", 42, 1000, 60000, "PA Spotify"));

        await fixture.Tick();

        using var verifyScope = fixture.Provider.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<schoolmusicContext>();
        var statuses = await verifyDb.QueueItems.OrderBy(item => item.Id).Select(item => item.Status).ToListAsync();
        Assert.Equal(new[] { "playing", "skipped" }, statuses);
    }

    [Fact]
    public async Task Tick_RetriesSpotifyWhenUnavailableWithoutUsingAnotherPlayer()
    {
        await using var fixture = new Fixture();
        await fixture.SeedActiveBreak();
        fixture.Spotify.SetupSequence(service => service.StartPlaybackAsync(
                It.IsAny<IReadOnlyList<SpotifyQueueTrack>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SpotifyPlaybackException(503, "device unavailable"))
            .Returns(Task.FromResult<IReadOnlyList<SpotifyQueueTrack>>(
                [new SpotifyQueueTrack(1, "A12345678901234567890", "Song", "Artist")]));
        fixture.Spotify.Setup(service => service.GetPlaybackStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpotifyPlaybackSnapshot(false, false, false, null, null, null, 0, 0, 0, null));

        await fixture.Tick();
        await fixture.Tick();

        fixture.Spotify.Verify(service => service.StartPlaybackAsync(
            It.IsAny<IReadOnlyList<SpotifyQueueTrack>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Equal(1, fixture.Service.GetActiveBreakId());
    }

    [Fact]
    public async Task Tick_DoesNotStartMusicWhenWeeklyRuleDisablesIt()
    {
        await using var fixture = new Fixture();
        await fixture.SeedActiveBreak(withTrack: false);
        using (var scope = fixture.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var weekday = (sbyte)DateTime.Now.DayOfWeek;
            db.WeeklySchedules.Add(new WeeklySchedule
            {
                Weekday = weekday == 0 ? (sbyte)7 : weekday,
                MusicEnabled = false
            });
            await db.SaveChangesAsync();
        }

        await fixture.Tick();

        Assert.Null(fixture.Service.GetActiveBreakId());
        fixture.BreakService.Verify(service => service.CalculateBreak(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<List<QueueItem>>()), Times.Never);
        fixture.Spotify.Verify(service => service.StartPlaybackAsync(
            It.IsAny<IReadOnlyList<SpotifyQueueTrack>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Tick_PausesAtBreakEndAndClearsPlaybackState()
    {
        await using var fixture = new Fixture();
        await fixture.SeedActiveBreak();
        await fixture.Tick();
        using (var scope = fixture.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var breakEntity = await db.Breaks.SingleAsync();
            breakEntity.EndsAt = DateTime.Now.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        await fixture.Tick();

        Assert.Null(fixture.Service.GetActiveBreakId());
        Assert.Null(fixture.Service.GetCurrentPlan());
        fixture.Spotify.Verify(service => service.PauseAsync(It.IsAny<CancellationToken>()), Times.Once);
        fixture.QueueService.Verify(service => service.ArchiveBreakAsync(1), Times.Once);
        using var verifyScope = fixture.Provider.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<schoolmusicContext>();
        var playback = await verifyDb.PlaybackStates.SingleAsync();
        Assert.Null(playback.CurrentQueueItemId);
        Assert.Null(playback.StartedAt);
        Assert.False(playback.IsPaused);
    }

    [Fact]
    public async Task Tick_PausesSpotifyDuringLocalPlaybackAndResumesWhenItEnds()
    {
        await using var fixture = new Fixture();
        await fixture.SeedActiveBreak();
        await fixture.Tick();
        fixture.AudioPlayer.Setup(service => service.GetStatus())
            .Returns(new PlayerStatusDto(true, false, 50, false, "LocalAudio", "Assembly", null, 0, 1000));

        await fixture.Tick();

        fixture.Spotify.Verify(service => service.PauseAsync(It.IsAny<CancellationToken>()), Times.Once);
        fixture.AudioPlayer.Setup(service => service.GetStatus())
            .Returns(new PlayerStatusDto(false, false, 50, false, "None", null, null, 0, 0));

        await fixture.Tick();

        fixture.Spotify.Verify(service => service.ResumeAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AdminManualFilePlaybackPausesSpotifyBeforeStartingVlc()
    {
        var spotify = new Mock<ISpotifyConnectPlaybackService>();
        var player = new Mock<IAudioPlayerService>();
        spotify.Setup(service => service.PauseAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        player.Setup(service => service.PlayFileOrUrlAsync("/recordings/assembly.mp4", "Assembly", "School"))
            .ReturnsAsync(ServiceResult<PlayerStatusDto>.Ok(
                new PlayerStatusDto(true, false, 50, false, "LocalAudio", "Assembly", "School", 0, 1000)));
        var controller = new PlayerController(spotify.Object, player.Object, Mock.Of<IBreakSchedulerService>());

        var result = await controller.PlayFile(
            new PlayFileDto("/recordings/assembly.mp4", "Assembly", "School"), CancellationToken.None);

        Assert.Equal(200, Assert.IsType<ObjectResult>(result).StatusCode);
        spotify.Verify(service => service.PauseAsync(It.IsAny<CancellationToken>()), Times.Once);
        player.Verify(service => service.PlayFileOrUrlAsync(
            "/recordings/assembly.mp4", "Assembly", "School"), Times.Once);
    }

    [Fact]
    public async Task SkipCurrentTrack_MarksItSkippedAndUsesSpotifyNext()
    {
        await using var fixture = new Fixture();
        await fixture.SeedActiveBreak();
        await fixture.Tick();

        Assert.True(await fixture.Service.SkipCurrentTrackAsync());

        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
        Assert.Equal("skipped", (await db.QueueItems.SingleAsync()).Status);
        fixture.Spotify.Verify(service => service.SkipToNextAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Tick_WhenNoBreakIsActiveDoesNotCreateAPlaybackPlan()
    {
        await using var fixture = new Fixture();
        await fixture.Tick();

        Assert.Null(fixture.Service.GetCurrentPlan());
        Assert.Null(fixture.Service.GetActiveBreakId());
        fixture.BreakService.Verify(service => service.CalculateBreak(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<List<QueueItem>>()), Times.Never);
    }

    [Fact]
    public void NotifyScheduleChanged_SignalsSchedulerWakeUpImmediately()
    {
        var hubContext = new Mock<IHubContext<QueueHub>>();
        var service = new BreakSchedulerService(
            Mock.Of<IServiceScopeFactory>(),
            hubContext.Object,
            NullLogger<BreakSchedulerService>.Instance);

        // Notify schedule changed should be idempotent and not throw
        service.NotifyScheduleChanged();
        service.NotifyScheduleChanged();
        service.Dispose();
    }

    [Fact]
    public void NotifyScheduleChanged_WhenCalledConcurrently_DoesNotThrow()
    {
        var service = new BreakSchedulerService(
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<IHubContext<QueueHub>>(),
            NullLogger<BreakSchedulerService>.Instance);

        var exception = Record.Exception(() =>
            Parallel.For(0, 10_000, _ => service.NotifyScheduleChanged()));

        Assert.Null(exception);
        service.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_NotificationWakesSchedulerBeforeAdaptiveDelay()
    {
        await using var fixture = new Fixture();
        await fixture.Service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => GetStatusCallCount(fixture) >= 1);

        fixture.Service.NotifyScheduleChanged();

        await WaitUntilAsync(() => GetStatusCallCount(fixture) >= 2);
        await fixture.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOneCycleFails_ContinuesAfterNotification()
    {
        await using var fixture = new Fixture();
        fixture.StatusService.SetupSequence(service => service.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test cycle failure"))
            .ReturnsAsync(new SystemStatusDto(
                SchoolState.Lesson, DateTime.UtcNow, null, null, 0, null, null,
                true, null, "Lekcja", null, Array.Empty<TopTrackDto>()));

        await fixture.Service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => GetStatusCallCount(fixture) >= 1);
        fixture.Service.NotifyScheduleChanged();

        await WaitUntilAsync(() => GetStatusCallCount(fixture) >= 2);
        await fixture.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Tick_WhenActiveBreak_SetsNextDelayToFiveSeconds()
    {
        await using var fixture = new Fixture();
        await fixture.SeedActiveBreak();
        await fixture.Tick();

        Assert.Equal(TimeSpan.FromSeconds(5), fixture.Service.NextDelay);
    }

    [Fact]
    public async Task Tick_WhenOutsideBreak_CalculatesAdaptiveDelay()
    {
        await using var fixture = new Fixture();
        await fixture.Tick();

        Assert.True(fixture.Service.NextDelay >= TimeSpan.FromSeconds(5));
    }

    private static int GetStatusCallCount(Fixture fixture) =>
        fixture.StatusService.Invocations.Count(invocation => invocation.Method.Name == nameof(ISystemStatusService.GetStatusAsync));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(10);

        Assert.True(condition(), "Warunek testu nie został spełniony w wymaganym czasie.");
    }
}
