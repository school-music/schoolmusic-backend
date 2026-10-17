using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using schoolmusic_backend.Hubs;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Tests;

public class BreakSchedulerServiceTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public ServiceProvider Provider { get; }
        public BreakSchedulerService Service { get; }
        public Mock<IBreakService> BreakService { get; } = new();
        public Mock<IQueueService> QueueService { get; } = new();
        public Mock<IAudioPlayerService> Player { get; } = new();

        public Fixture()
        {
            var databaseName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddDbContext<schoolmusicContext>(options =>
                options.UseInMemoryDatabase(databaseName));
            services.AddScoped(_ => BreakService.Object);
            services.AddScoped(_ => QueueService.Object);

            var statusService = new Mock<ISystemStatusService>();
            statusService.Setup(service => service.GetStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SystemStatusDto(
                    SchoolState.Lesson,
                    DateTime.UtcNow,
                    null,
                    null,
                    0,
                    null,
                    null,
                    true,
                    null,
                    "Lekcja",
                    null,
                    Array.Empty<TopTrackDto>()));
            services.AddScoped(_ => statusService.Object);

            var hubClient = new Mock<IClientProxy>();
            hubClient.Setup(client => client.SendCoreAsync(
                    It.IsAny<string>(),
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var hubClients = new Mock<IHubClients>();
            hubClients.Setup(clients => clients.Group(QueueHub.KioskGroup)).Returns(hubClient.Object);
            var hubContext = new Mock<IHubContext<QueueHub>>();
            hubContext.Setup(context => context.Clients).Returns(hubClients.Object);

            Provider = services.BuildServiceProvider();
            var playerStatus = new PlayerStatusDto(false, false, 50, false, "None", null, null, 0, 0);
            Player.Setup(p => p.GetStatus()).Returns(playerStatus);
            Player.Setup(p => p.PlayFileOrUrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync(ServiceResult<PlayerStatusDto>.Ok(playerStatus));
            Player.Setup(p => p.StopAsync()).ReturnsAsync(ServiceResult<PlayerStatusDto>.Ok(playerStatus));
            QueueService.Setup(q => q.ArchiveBreakAsync(It.IsAny<int>()))
                .ReturnsAsync(ServiceResult<object>.Ok(new object()));
            Service = new BreakSchedulerService(
                Provider.GetRequiredService<IServiceScopeFactory>(),
                Player.Object,
                hubContext.Object,
                NullLogger<BreakSchedulerService>.Instance);
        }

        public async Task SeedActiveBreak(bool withTrack = true)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var now = DateTime.Now;
            var brk = new Break
            {
                Id = 1, BreakDate = DateOnly.FromDateTime(now), BreakNumber = 1,
                StartAt = now.AddMinutes(-1), EndsAt = now.AddMinutes(5), MusicEnabled = true
            };
            db.Breaks.Add(brk);
            if (withTrack)
            {
                var artist = new Artist { Id = 1, Name = "Artist", Source = "other" };
                var song = new Song
                {
                    Id = 1, Title = "Song", ArtistId = 1, Artist = artist, Source = "other",
                    SongUrl = "https://example.test/song.mp3", DurationMs = 60000,
                    IsActive = true, CreatedAt = now
                };
                db.Artists.Add(artist);
                db.Songs.Add(song);
                db.QueueItems.Add(new QueueItem
                {
                    Id = 1, BreakId = 1, Break = brk, SongId = 1, Song = song,
                    Status = "queued", ModerationStatus = "approved", OrderIndex = 1,
                    CreatedAt = now
                });
                db.PlaybackStates.Add(new PlaybackState { Id = 1, CurrentQueueItemId = 1, StartedAt = now, UpdatedAt = now });
                BreakService.Setup(s => s.CalculateBreak(1, It.IsAny<int>(), It.IsAny<List<QueueItem>>()))
                    .Returns(new ScheduledBreakPlan(1, 300, 60, 240, [
                        new ScheduledTrackDto(1, 1, "Song", "Artist", "", 60000, 0, 0, 1)
                    ]));
            }
            await db.SaveChangesAsync();
        }

        public async Task Tick()
        {
            var tick = typeof(BreakSchedulerService).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var task = (Task)tick.Invoke(Service, [CancellationToken.None])!;
            await task;
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await Provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task Tick_StartsAnActiveMusicBreakAndPersistsPlaybackState()
    {
        await using var fixture = new Fixture();
        await fixture.SeedActiveBreak();

        await fixture.Tick();

        Assert.Equal(1, fixture.Service.GetActiveBreakId());
        Assert.Equal(1, fixture.Service.GetCurrentPlan()?.BreakId);
        fixture.Player.Verify(p => p.PlayFileOrUrlAsync("https://example.test/song.mp3", "Song", "Artist"), Times.Once);
        fixture.QueueService.Verify(q => q.BroadcastQueueAsync(1), Times.Once);
        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
        Assert.Equal("playing", (await db.QueueItems.SingleAsync()).Status);
        Assert.Equal(1, (await db.PlaybackStates.SingleAsync()).CurrentQueueItemId);
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
            db.WeeklySchedules.Add(new WeeklySchedule { Weekday = weekday == 0 ? (sbyte)7 : weekday, MusicEnabled = false });
            await db.SaveChangesAsync();
        }

        await fixture.Tick();

        Assert.Null(fixture.Service.GetActiveBreakId());
        fixture.BreakService.Verify(s => s.CalculateBreak(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<List<QueueItem>>()), Times.Never);
        fixture.Player.Verify(p => p.PlayFileOrUrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Tick_CleansUpWhenActiveBreakHasEnded()
    {
        await using var fixture = new Fixture();
        await fixture.SeedActiveBreak();
        await fixture.Tick();
        using (var scope = fixture.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var brk = await db.Breaks.SingleAsync();
            brk.EndsAt = DateTime.Now.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        await fixture.Tick();

        Assert.Null(fixture.Service.GetActiveBreakId());
        Assert.Null(fixture.Service.GetCurrentPlan());
        fixture.Player.Verify(p => p.StopAsync(), Times.Once);
        fixture.QueueService.Verify(q => q.ArchiveBreakAsync(1), Times.Once);
        using var verifyScope = fixture.Provider.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<schoolmusicContext>();
        var playback = await verifyDb.PlaybackStates.SingleAsync();
        Assert.Null(playback.CurrentQueueItemId);
        Assert.Null(playback.StartedAt);
        Assert.False(playback.IsPaused);
    }

    [Fact]
    public async Task SkipCurrentTrack_MarksQueueItemSkippedAndStopsPlayback()
    {
        await using var fixture = new Fixture();
        await fixture.SeedActiveBreak();
        await fixture.Tick();

        Assert.True(await fixture.Service.SkipCurrentTrackAsync());

        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
        Assert.Equal("skipped", (await db.QueueItems.SingleAsync()).Status);
        fixture.Player.Verify(p => p.StopAsync(), Times.Once);
        Assert.Equal(1, fixture.Service.GetActiveBreakId());
    }

    [Fact]
    public async Task Tick_WhenNoBreakIsActiveDoesNotCreateAPlaybackPlan()
    {
        await using var fixture = new Fixture();
        await fixture.Tick();

        Assert.Null(fixture.Service.GetCurrentPlan());
        Assert.Null(fixture.Service.GetActiveBreakId());
        fixture.BreakService.Verify(s => s.CalculateBreak(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<List<QueueItem>>()), Times.Never);
    }
}
