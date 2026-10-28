using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
        Mock<IConnectionMultiplexer>? redisConnection = null,
        IConfiguration? configuration = null)
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
            defaultDb.Setup(db => db.StringSetAsync(
                    It.IsAny<RedisKey>(),
                    It.IsAny<RedisValue>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<When>()))
                .ReturnsAsync(true);
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

        var service = new QueueService(context, redisConnection.Object, mockHub.Object, songSource.Object, configuration);
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
        database.Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<When>()))
            .ReturnsAsync(true);

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

    [Fact]
    public async Task ProposeSongAsync_WhenVotingAllowedOutsideSchedule_AllowsProposalAtAnyTime()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Voting:AllowVotingOutsideSchedule"] = "true"
            })
            .Build();

        var (service, context) = CreateService(
            nameof(ProposeSongAsync_WhenVotingAllowedOutsideSchedule_AllowsProposalAtAnyTime),
            configuration: config);

        var now = DateTime.Now;
        var breakEntity = new Break
        {
            Id = 1, BreakDate = DateOnly.FromDateTime(now), BreakNumber = 1,
            StartAt = now.AddMinutes(-5), EndsAt = now.AddMinutes(5), VoteEnds = now.AddMinutes(-5),
            MusicEnabled = true
        };
        context.Breaks.Add(breakEntity);
        await context.SaveChangesAsync();

        var dto = new ProposeSongDto(
            BreakId: 1,
            Title: "Zezwolony Utwór",
            Artist: "Artysta",
            Genre: "Rock"
        );

        var result = await service.ProposeSongAsync(dto, currentUserId: 1);

        Assert.True(result.Success);
        Assert.Contains(result.StatusCode, new[] { 200, 202 });
    }

    [Fact]
    public async Task VoteAsync_WhenVotingAllowedOutsideSchedule_AllowsVoteAtAnyTime()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Voting:AllowVotingOutsideSchedule"] = "true"
            })
            .Build();

        var (service, context) = CreateService(
            nameof(VoteAsync_WhenVotingAllowedOutsideSchedule_AllowsVoteAtAnyTime),
            configuration: config);

        var now = DateTime.Now;
        var breakEntity = new Break
        {
            Id = 1, BreakDate = DateOnly.FromDateTime(now), BreakNumber = 1,
            StartAt = now.AddMinutes(-5), EndsAt = now.AddMinutes(5), VoteEnds = now.AddMinutes(-5),
            MusicEnabled = true
        };
        context.Breaks.Add(breakEntity);

        var song = new Song { Id = 1, Title = "Głosowany Utwór", DurationMs = 200000, Source = "spotify" };
        context.Songs.Add(song);

        var queueItem = new QueueItem
        {
            Id = 1, BreakId = 1, SongId = 1, UserId = 2,
            ModerationStatus = "approved", Status = "queued", OrderIndex = 1
        };
        context.QueueItems.Add(queueItem);
        await context.SaveChangesAsync();

        var dto = new VoteRequestDto(QueueItemId: 1);
        var result = await service.VoteAsync(dto, currentUserId: 1);

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task ProposeSongAsync_WhenVotingNotAllowedOutsideSchedule_BlocksDuringBreakWindow()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Voting:AllowVotingOutsideSchedule"] = "false"
            })
            .Build();

        var (service, context) = CreateService(
            nameof(ProposeSongAsync_WhenVotingNotAllowedOutsideSchedule_BlocksDuringBreakWindow),
            configuration: config);

        var now = DateTime.Now;
        var breakEntity = new Break
        {
            Id = 1, BreakDate = DateOnly.FromDateTime(now), BreakNumber = 1,
            StartAt = now.AddMinutes(-2), EndsAt = now.AddMinutes(8), VoteEnds = now.AddMinutes(-2),
            MusicEnabled = true
        };
        context.Breaks.Add(breakEntity);
        await context.SaveChangesAsync();

        var dto = new ProposeSongDto(
            BreakId: 1,
            Title: "Zablokowany Utwór",
            Artist: "Artysta",
            Genre: "Rock"
        );

        var result = await service.ProposeSongAsync(dto, currentUserId: 1);

        Assert.False(result.Success);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("Głosowanie na tę przerwę zostało już zamknięte.", result.Message);
    }

    [Fact]
    public async Task VoteAsync_WhenVotingNotAllowedOutsideSchedule_BlocksDuringBreakWindow()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Voting:AllowVotingOutsideSchedule"] = "false"
            })
            .Build();

        var (service, context) = CreateService(
            nameof(VoteAsync_WhenVotingNotAllowedOutsideSchedule_BlocksDuringBreakWindow),
            configuration: config);

        var now = DateTime.Now;
        var breakEntity = new Break
        {
            Id = 1, BreakDate = DateOnly.FromDateTime(now), BreakNumber = 1,
            StartAt = now.AddMinutes(-2), EndsAt = now.AddMinutes(8), VoteEnds = now.AddMinutes(-2),
            MusicEnabled = true
        };
        context.Breaks.Add(breakEntity);

        var song = new Song { Id = 1, Title = "Utwór", DurationMs = 200000, Source = "spotify" };
        context.Songs.Add(song);

        var queueItem = new QueueItem
        {
            Id = 1, BreakId = 1, SongId = 1, UserId = 2,
            ModerationStatus = "approved", Status = "queued", OrderIndex = 1
        };
        context.QueueItems.Add(queueItem);
        await context.SaveChangesAsync();

        var dto = new VoteRequestDto(QueueItemId: 1);
        var result = await service.VoteAsync(dto, currentUserId: 1);

        Assert.False(result.Success);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("Głosowanie na tę przerwę dobiegło już końca.", result.Message);
    }

    [Fact]
    public async Task ProposeSongAsync_WhenVotingOutsideSchedule_AllowsProposalForNextDayCycle()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Voting:AllowVotingOutsideSchedule"] = "false"
            })
            .Build();

        var (service, context) = CreateService(
            nameof(ProposeSongAsync_WhenVotingOutsideSchedule_AllowsProposalForNextDayCycle),
            configuration: config);

        var now = DateTime.Now;
        var breakEntity = new Break
        {
            Id = 1, BreakDate = DateOnly.FromDateTime(now), BreakNumber = 1,
            StartAt = now.AddHours(-5), EndsAt = now.AddHours(-4), VoteEnds = now.AddHours(-5),
            MusicEnabled = true
        };
        context.Breaks.Add(breakEntity);
        await context.SaveChangesAsync();

        var dto = new ProposeSongDto(
            BreakId: 1,
            Title: "Wieczorne Zgłoszenie",
            Artist: "Artysta",
            Genre: "Rock"
        );

        var result = await service.ProposeSongAsync(dto, currentUserId: 1);

        Assert.True(result.Success);
        Assert.Contains(result.StatusCode, new[] { 200, 202 });
    }

    [Fact]
    public async Task VoteAsync_WhenTrackIsAlreadyPlaying_ReturnsBadRequest()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Voting:AllowVotingOutsideSchedule"] = "true"
            })
            .Build();
        var (service, context) = CreateService(
            nameof(VoteAsync_WhenTrackIsAlreadyPlaying_ReturnsBadRequest),
            configuration: config);
        var now = DateTime.Now;
        context.Breaks.Add(new Break
        {
            Id = 1, BreakDate = DateOnly.FromDateTime(now), BreakNumber = 1,
            StartAt = now, EndsAt = now.AddMinutes(10), MusicEnabled = true
        });
        context.QueueItems.Add(new QueueItem
        {
            Id = 1, BreakId = 1, SongId = 1, Status = "playing",
            ModerationStatus = "approved", OrderIndex = 1
        });
        await context.SaveChangesAsync();

        var result = await service.VoteAsync(new VoteRequestDto(1), currentUserId: 5);

        Assert.False(result.Success);
        Assert.Equal(400, result.StatusCode);
        Assert.Empty(context.Votes);
    }

    [Fact]
    public async Task VoteAsync_WhenSameVoteIsSubmittedConcurrently_StoresOnlyOneVote()
    {
        var databaseName = nameof(VoteAsync_WhenSameVoteIsSubmittedConcurrently_StoresOnlyOneVote);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Voting:AllowVotingOutsideSchedule"] = "true"
            })
            .Build();
        var (firstService, firstContext) = CreateService(databaseName, configuration: config);
        var (secondService, secondContext) = CreateService(databaseName, configuration: config);
        var now = DateTime.Now;
        var artist = new Artist { Id = 1, Name = "Artysta", Source = "other" };
        var song = new Song { Id = 1, Title = "Utwór", ArtistId = 1, Artist = artist, DurationMs = 180000, Source = "other" };
        var submitter = new User { Id = 2, UserLogin = "submitter", UserPassword = "hash", RankId = 1 };
        var breakEntity = new Break
        {
            Id = 1, BreakDate = DateOnly.FromDateTime(now), BreakNumber = 1,
            StartAt = now, EndsAt = now.AddMinutes(10), MusicEnabled = true
        };
        firstContext.AddRange(artist, song, submitter, breakEntity);
        firstContext.QueueItems.Add(new QueueItem
        {
            Id = 1, BreakId = 1, Break = breakEntity, SongId = 1, Song = song,
            UserId = 2, User = submitter, Status = "queued", ModerationStatus = "approved", OrderIndex = 1
        });
        await firstContext.SaveChangesAsync();

        var results = await Task.WhenAll(
            firstService.VoteAsync(new VoteRequestDto(1), currentUserId: 5),
            secondService.VoteAsync(new VoteRequestDto(1), currentUserId: 5));

        Assert.Single(results, result => result.Success);
        Assert.Single(results, result => result.StatusCode == 409);
        await using var verification = CreateInMemoryContext(databaseName);
        Assert.Equal(1, await verification.Votes.CountAsync(item => item.UserId == 5 && item.QueueItemId == 1));
        await firstContext.DisposeAsync();
        await secondContext.DisposeAsync();
    }

    [Fact]
    public async Task ArchiveBreakAsync_WhenCalledTwice_DoesNotDuplicateHistory()
    {
        var (service, context) = CreateService(nameof(ArchiveBreakAsync_WhenCalledTwice_DoesNotDuplicateHistory));
        var now = DateTime.Now;
        var breakEntity = new Break
        {
            Id = 1, BreakDate = DateOnly.FromDateTime(now), BreakNumber = 1,
            StartAt = now.AddMinutes(-10), EndsAt = now, MusicEnabled = true
        };
        var artist = new Artist { Id = 1, Name = "Artysta", Source = "other" };
        var song = new Song { Id = 1, Title = "Utwór", ArtistId = 1, Artist = artist, DurationMs = 180000, Source = "other" };
        context.AddRange(breakEntity, artist, song);
        context.QueueItems.Add(new QueueItem
        {
            Id = 1, BreakId = 1, Break = breakEntity, SongId = 1, Song = song,
            Status = "played", ModerationStatus = "approved", OrderIndex = 1
        });
        await context.SaveChangesAsync();

        var first = await service.ArchiveBreakAsync(1);
        var second = await service.ArchiveBreakAsync(1);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Single(context.Histories);
    }

    [Fact]
    public async Task ArchiveBreakAsync_WhenBreakDoesNotExist_ReturnsNotFoundWithoutArchivingAnotherBreak()
    {
        var (service, context) = CreateService(nameof(ArchiveBreakAsync_WhenBreakDoesNotExist_ReturnsNotFoundWithoutArchivingAnotherBreak));
        context.Breaks.Add(new Break
        {
            Id = 1, BreakDate = DateOnly.FromDateTime(DateTime.Today), BreakNumber = 1,
            StartAt = DateTime.Today, EndsAt = DateTime.Today.AddMinutes(10), MusicEnabled = true
        });
        context.QueueItems.Add(new QueueItem
        {
            Id = 1, BreakId = 1, SongId = 1, Status = "queued", ModerationStatus = "approved", OrderIndex = 1
        });
        await context.SaveChangesAsync();

        var result = await service.ArchiveBreakAsync(999);

        Assert.False(result.Success);
        Assert.Equal(404, result.StatusCode);
        Assert.Single(context.QueueItems);
    }

    [Fact]
    public async Task ProposeSongAsync_WhenSamePendingProposalIsSubmittedConcurrently_StoresOnlyOneRedisEntry()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        var database = new Mock<IDatabase>();
        var reservation = 0;
        database.Setup(item => item.ListRangeAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(Array.Empty<RedisValue>());
        database.Setup(item => item.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), When.NotExists))
            .ReturnsAsync(() => Interlocked.CompareExchange(ref reservation, 1, 0) == 0);
        database.Setup(item => item.ListRightPushAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(1);
        redis.Setup(item => item.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);

        var databaseName = nameof(ProposeSongAsync_WhenSamePendingProposalIsSubmittedConcurrently_StoresOnlyOneRedisEntry);
        var (firstService, firstContext) = CreateService(databaseName, redisConnection: redis);
        var (secondService, secondContext) = CreateService(databaseName, redisConnection: redis);
        var now = DateTime.Now;
        var artist = new Artist { Id = 1, Name = "Artysta", Source = "other" };
        var song = new Song
        {
            Id = 1, Title = "Utwór", ArtistId = 1, Artist = artist,
            DurationMs = 180000, Source = "other", CreatedAt = DateTime.UtcNow
        };
        firstContext.AddRange(
            artist,
            song,
            new User { Id = 1, UserLogin = "first", UserPassword = "hash", RankId = 1 },
            new User { Id = 2, UserLogin = "second", UserPassword = "hash", RankId = 1 },
            new Break
            {
                Id = 1, BreakDate = DateOnly.FromDateTime(now), BreakNumber = 1,
                StartAt = now.AddHours(1), EndsAt = now.AddHours(2), VoteEnds = now.AddMinutes(30), MusicEnabled = true
            });
        await firstContext.SaveChangesAsync();

        var results = await Task.WhenAll(
            firstService.ProposeSongAsync(new ProposeSongDto(SongId: 1, BreakId: 1), 1),
            secondService.ProposeSongAsync(new ProposeSongDto(SongId: 1, BreakId: 1), 2));

        Assert.Single(results, result => result.StatusCode == 202);
        Assert.Single(results, result => result.StatusCode == 409);
        database.Verify(item => item.ListRightPushAsync(
            PendingProposalStore.ListKey,
            It.IsAny<RedisValue>(),
            When.Always,
            CommandFlags.None), Times.Once);
        await firstContext.DisposeAsync();
        await secondContext.DisposeAsync();
    }
}
