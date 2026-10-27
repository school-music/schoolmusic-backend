using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using schoolmusic_backend.Hubs;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;
using StackExchange.Redis;

namespace schoolmusic_backend.Tests;

public class QueueServiceTests
{
    private schoolmusicContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<schoolmusicContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new schoolmusicContext(options);
    }

    private (IQueueService service, schoolmusicContext context) CreateService(
        string dbName,
        Mock<ISongSourceService>? songSource = null,
        bool spotifyConfigured = true,
        Mock<IConnectionMultiplexer>? redisConnection = null)
    {
        var context = CreateInMemoryContext(dbName);
        if (redisConnection == null)
        {
            var defaultRedis = new Mock<IConnectionMultiplexer>();
            var defaultDb = new Mock<IDatabase>();
            defaultDb.Setup(db => db.ListRangeAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(Array.Empty<RedisValue>());
            defaultDb.Setup(db => db.ListRightPushAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(1);
            defaultRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
                .Returns(defaultDb.Object);
            redisConnection = defaultRedis;
        }
        var mockHub = new Mock<IHubContext<QueueHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

        songSource ??= new Mock<ISongSourceService>();
        songSource.SetupGet(source => source.IsConfigured).Returns(spotifyConfigured);

        var service = new QueueService(context, redisConnection.Object, mockHub.Object, songSource.Object);
        return (service, context);
    }

    [Fact]
    public async Task VoteAsync_WhenUserIsBanned_ReturnsForbidden()
    {
        var (service, context) = CreateService(nameof(VoteAsync_WhenUserIsBanned_ReturnsForbidden));

        context.BannedUsers.Add(new BannedUser
        {
            UserId = 10,
            BannedAt = DateTime.UtcNow,
            Reason = "Spam"
        });
        await context.SaveChangesAsync();

        var dto = new VoteRequestDto(QueueItemId: 1);
        var result = await service.VoteAsync(dto, currentUserId: 10);

        Assert.False(result.Success);
        Assert.Equal(403, result.StatusCode);
        Assert.Contains("zablokowane", result.Message);
    }

    [Fact]
    public async Task VoteAsync_WhenQueueItemNotFound_ReturnsNotFound()
    {
        var (service, context) = CreateService(nameof(VoteAsync_WhenQueueItemNotFound_ReturnsNotFound));

        var dto = new VoteRequestDto(QueueItemId: 999);
        var result = await service.VoteAsync(dto, currentUserId: 1);

        Assert.False(result.Success);
        Assert.Equal(404, result.StatusCode);
    }

    [Fact]
    public async Task VoteAsync_WhenAlreadyVoted_ReturnsConflict()
    {
        var (service, context) = CreateService(nameof(VoteAsync_WhenAlreadyVoted_ReturnsConflict));

        var brk = new Break
        {
            Id = 1,
            BreakDate = DateOnly.FromDateTime(DateTime.Now),
            BreakNumber = 1,
            StartAt = DateTime.Now.AddHours(1),
            EndsAt = DateTime.Now.AddHours(2),
            VoteEnds = DateTime.Now.AddHours(1),
            MusicEnabled = true
        };
        var item = new QueueItem
        {
            Id = 100,
            BreakId = 1,
            SongId = 1,
            UserId = 2,
            ModerationStatus = "approved",
            Status = "queued",
            OrderIndex = 1
        };
        var existingVote = new Vote
        {
            Id = 1,
            UserId = 5,
            QueueItemId = 100,
            VotedAt = DateTime.UtcNow
        };

        context.Breaks.Add(brk);
        context.QueueItems.Add(item);
        context.Votes.Add(existingVote);
        await context.SaveChangesAsync();

        var dto = new VoteRequestDto(QueueItemId: 100);
        var result = await service.VoteAsync(dto, currentUserId: 5);

        Assert.False(result.Success);
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("dwukrotnie", result.Message);
    }

    [Fact]
    public async Task VoteAsync_WhenValidVote_SucceedsAndRecordsVote()
    {
        var (service, context) = CreateService(nameof(VoteAsync_WhenValidVote_SucceedsAndRecordsVote));

        var artist = new Artist { Id = 1, Name = "Test Artist", Source = "other" };
        var song = new Song { Id = 1, Title = "Test Song", ArtistId = 1, Artist = artist, DurationMs = 180000, Source = "other", CreatedAt = DateTime.UtcNow };
        var user = new User { Id = 2, UserLogin = "testuser", UserPassword = "pwd", RankId = 1, CreatedAt = DateTime.UtcNow };
        var brk = new Break
        {
            Id = 2,
            BreakDate = DateOnly.FromDateTime(DateTime.Now),
            BreakNumber = 2,
            StartAt = DateTime.Now.AddHours(1),
            EndsAt = DateTime.Now.AddHours(2),
            VoteEnds = DateTime.Now.AddHours(1),
            MusicEnabled = true
        };
        var item = new QueueItem
        {
            Id = 200,
            BreakId = 2,
            SongId = 1,
            Song = song,
            UserId = 2,
            User = user,
            ModerationStatus = "approved",
            Status = "queued",
            OrderIndex = 1
        };

        context.Artists.Add(artist);
        context.Songs.Add(song);
        context.Users.Add(user);
        context.Breaks.Add(brk);
        context.QueueItems.Add(item);
        await context.SaveChangesAsync();

        var dto = new VoteRequestDto(QueueItemId: 200);
        var result = await service.VoteAsync(dto, currentUserId: 5);

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        var recordedVote = await context.Votes.FirstOrDefaultAsync(v => v.UserId == 5 && v.QueueItemId == 200);
        Assert.NotNull(recordedVote);
    }

    [Fact]
    public async Task ProposeSongAsync_WhenSpotifyIsNotConfigured_ReturnsServiceUnavailable()
    {
        var (service, _) = CreateService(
            nameof(ProposeSongAsync_WhenSpotifyIsNotConfigured_ReturnsServiceUnavailable),
            spotifyConfigured: false);

        var result = await service.ProposeSongAsync(new ProposeSongDto(null, "spotify-id", null), currentUserId: 1);

        Assert.False(result.Success);
        Assert.Equal(503, result.StatusCode);
        Assert.Contains("Spotify:ClientId", result.Message);
    }

    [Fact]
    public async Task ProposeSongAsync_ImportsMissingSpotifyTrackBeforeSubmitting()
    {
        var spotifyTrack = new ExternalTrackDto("spotify-id", "New Song", "New Artist", "cover.jpg", 123000, "https://preview.test/track.mp3", "spotify");
        var songSource = new Mock<ISongSourceService>();
        songSource.Setup(source => source.GetTrackByIdAsync("spotify-id")).ReturnsAsync(spotifyTrack);

        var redis = new Mock<IConnectionMultiplexer>();
        var database = new Mock<IDatabase>();
        redis.Setup(connection => connection.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(database.Object);
        database.Setup(db => db.ListRangeAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(Array.Empty<RedisValue>());
        database.Setup(db => db.ListRightPushAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(1);

        var (service, context) = CreateService(
            nameof(ProposeSongAsync_ImportsMissingSpotifyTrackBeforeSubmitting),
            songSource,
            redisConnection: redis);
        context.Breaks.Add(new Break
        {
            Id = 10,
            BreakDate = DateOnly.FromDateTime(DateTime.Now),
            BreakNumber = 1,
            StartAt = DateTime.Now.AddHours(1),
            EndsAt = DateTime.Now.AddHours(2),
            VoteEnds = DateTime.Now.AddMinutes(30),
            MusicEnabled = true
        });
        await context.SaveChangesAsync();

        var result = await service.ProposeSongAsync(
            new ProposeSongDto(null, "spotify-id", 10),
            currentUserId: 1);

        Assert.True(result.Success);
        Assert.Equal(202, result.StatusCode);
        var song = await context.Songs.Include(candidate => candidate.Artist).SingleAsync();
        Assert.Equal("spotify-id", song.SpotifyId);
        Assert.Equal("New Song", song.Title);
        Assert.Equal("New Artist", song.Artist.Name);
        Assert.Equal("https://preview.test/track.mp3", song.SongUrl);
        songSource.Verify(source => source.GetTrackByIdAsync("spotify-id"), Times.Once);
        database.Verify(db => db.ListRightPushAsync(
            "proposals:pending",
            It.IsAny<RedisValue>(),
            When.Always,
            CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task ArchiveBreakAsync_RecordsOnlyTracksReportedAsPlayedBySpotifyPolling()
    {
        var (service, context) = CreateService(nameof(ArchiveBreakAsync_RecordsOnlyTracksReportedAsPlayedBySpotifyPolling));
        var now = DateTime.Now;
        var breakEntity = new Break
        {
            Id = 1, BreakDate = DateOnly.FromDateTime(now), BreakNumber = 1,
            StartAt = now.AddMinutes(-10), EndsAt = now, MusicEnabled = true
        };
        var artist = new Artist { Id = 1, Name = "Artist", Source = "spotify" };
        context.Breaks.Add(breakEntity);
        context.Artists.Add(artist);
        for (var id = 1; id <= 3; id++)
        {
            var song = new Song
            {
                Id = id, Title = $"Track {id}", ArtistId = 1, Artist = artist,
                Source = "spotify", SpotifyId = $"track{id}", DurationMs = 60000, CreatedAt = now
            };
            context.Songs.Add(song);
            context.QueueItems.Add(new QueueItem
            {
                Id = id, BreakId = 1, Break = breakEntity, SongId = id, Song = song,
                ModerationStatus = "approved",
                Status = id == 1 ? "played" : id == 2 ? "queued" : "playing",
                OrderIndex = id, CreatedAt = now
            });
        }
        await context.SaveChangesAsync();

        var result = await service.ArchiveBreakAsync(1);

        Assert.True(result.Success);
        Assert.Equal(new[] { 1, 3 }, await context.Histories.OrderBy(item => item.SongId)
            .Select(item => item.SongId).ToArrayAsync());
        Assert.Empty(await context.QueueItems.ToListAsync());
    }

    [Fact]
    public async Task ProposeSongAsync_WhenTitleAndArtistProvided_CreatesSongAndProposes()
    {
        var (service, context) = CreateService(nameof(ProposeSongAsync_WhenTitleAndArtistProvided_CreatesSongAndProposes));
        var now = DateTime.Now;
        var breakEntity = new Break
        {
            Id = 1, BreakDate = DateOnly.FromDateTime(now), BreakNumber = 1,
            StartAt = now.AddMinutes(10), EndsAt = now.AddMinutes(20),
            VoteEnds = now.AddMinutes(10), MusicEnabled = true
        };
        context.Breaks.Add(breakEntity);
        await context.SaveChangesAsync();

        var dto = new ProposeSongDto(
            SongId: null,
            SpotifyId: null,
            BreakId: 1,
            Title: "Nowy Utwór",
            Artist: "Nowy Artysta",
            Genre: "Pop"
        );

        var result = await service.ProposeSongAsync(dto, currentUserId: 1);

        Assert.True(result.Success);
        var createdSong = await context.Songs.Include(s => s.Artist)
            .FirstOrDefaultAsync(s => s.Title == "Nowy Utwór");
        Assert.NotNull(createdSong);
        Assert.Equal("Nowy Artysta", createdSong.Artist.Name);
    }

    [Fact]
    public async Task GetQueueForBreakAsync_WhenRequestedBreakNotFound_FallsBackToExistingBreak()
    {
        var (service, context) = CreateService(nameof(GetQueueForBreakAsync_WhenRequestedBreakNotFound_FallsBackToExistingBreak));
        var now = DateTime.Now;
        var breakEntity = new Break
        {
            Id = 10, BreakDate = DateOnly.FromDateTime(now), BreakNumber = 1,
            StartAt = now, EndsAt = now.AddMinutes(10), MusicEnabled = true
        };
        context.Breaks.Add(breakEntity);
        await context.SaveChangesAsync();

        // Żądanie dla nieistniejącej przerwy 999
        var result = await service.GetQueueForBreakAsync(999, currentUserId: 1);

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
    }
}