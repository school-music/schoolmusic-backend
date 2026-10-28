using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using schoolmusic_backend.Controllers;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;
using StackExchange.Redis;
using Xunit;

namespace schoolmusic_backend.Tests;

public class StatisticsServiceTests
{
    // Środa 15.10.2026 — środek tygodnia, wygodny punkt odniesienia dla okien czasowych.
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0);

    private static schoolmusicContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<schoolmusicContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new schoolmusicContext(options);
    }

    private static Mock<IDatabase> CreateMockDatabase(RedisValue? cachedValue = null)
    {
        var mockDatabase = new Mock<IDatabase>();
        mockDatabase
            .Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(cachedValue ?? RedisValue.Null);
        return mockDatabase;
    }

    private static (StatisticsService service, schoolmusicContext context, Mock<IDatabase> database) CreateService(
        string dbName,
        RedisValue? cachedValue = null,
        TimeProvider? timeProvider = null)
    {
        var context = CreateInMemoryContext(dbName);
        var mockDatabase = CreateMockDatabase(cachedValue);

        var mockRedis = new Mock<IConnectionMultiplexer>();
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        var clock = timeProvider ?? new ManualTimeProvider(Now);
        var service = new StatisticsService(context, mockRedis.Object, clock, NullLogger<StatisticsService>.Instance);

        return (service, context, mockDatabase);
    }

    private static (Artist artist, Song song) SeedSong(
        schoolmusicContext context,
        int id,
        string title,
        string artistName,
        string? genre,
        int artistId)
    {
        // Jeden wykonawca może obsługiwać kilka utworów — nie dodajemy go do kontekstu drugi raz.
        var artist = context.Artists.Local.FirstOrDefault(a => a.Id == artistId);
        if (artist == null)
        {
            artist = new Artist { Id = artistId, Name = artistName, Source = "other" };
            context.Artists.Add(artist);
        }

        var song = new Song
        {
            Id = id,
            Title = title,
            ArtistId = artistId,
            Artist = artist,
            Genre = genre,
            DurationMs = 200000,
            Source = "other",
            CreatedAt = Now
        };

        context.Songs.Add(song);
        return (artist, song);
    }

    private static Break CreateBreak(int id)
    {
        return new Break
        {
            Id = id,
            BreakDate = DateOnly.FromDateTime(Now),
            BreakNumber = (sbyte)id,
            StartAt = Now.Date.AddHours(9),
            EndsAt = Now.Date.AddHours(9) + TimeSpan.FromMinutes(10),
            MusicEnabled = true
        };
    }

    private static QueueItem SeedQueueItem(schoolmusicContext context, int id, int breakId, int songId, int orderIndex)
    {
        var item = new QueueItem
        {
            Id = id,
            BreakId = breakId,
            SongId = songId,
            ModerationStatus = "approved",
            Status = "queued",
            OrderIndex = orderIndex
        };
        context.QueueItems.Add(item);
        return item;
    }

    private static Vote SeedVote(schoolmusicContext context, int id, int queueItemId, int userId, DateTime votedAt)
    {
        var vote = new Vote
        {
            Id = id,
            QueueItemId = queueItemId,
            UserId = userId,
            VotedAt = votedAt
        };
        context.Votes.Add(vote);
        return vote;
    }

    [Fact]
    public async Task GetDashboardAsync_CountsVotes_ForTotalWeekAndToday()
    {
        var (service, context, _) = CreateService(nameof(GetDashboardAsync_CountsVotes_ForTotalWeekAndToday));

        var brk = CreateBreak(1);
        context.Breaks.Add(brk);
        SeedSong(context, 1, "Song", "Artist", "pop", 1);
        var item = SeedQueueItem(context, 1, brk.Id, 1, 1);

        SeedVote(context, 1, item.Id, 101, Now);                      // dzisiaj
        SeedVote(context, 2, item.Id, 102, Now.AddDays(-3));          // 3 dni temu — w tym tygodniu
        SeedVote(context, 3, item.Id, 103, Now.AddDays(-40));         // 40 dni temu — poza oknem
        await context.SaveChangesAsync();

        var dto = await service.GetDashboardAsync();

        Assert.Equal(3, dto.Votes.Total);
        Assert.Equal(2, dto.Votes.ThisWeek);
        Assert.Equal(1, dto.Votes.Today);
    }

    [Fact]
    public async Task GetDashboardAsync_CountsTodayUsingSchoolTimeZoneBoundary()
    {
        var utcClock = new ManualTimeProvider(new DateTime(2026, 10, 14, 22, 30, 0));
        var schoolClock = new SchoolTimeProvider(
            utcClock,
            SchoolTimeProvider.ResolveTimeZone("Europe/Warsaw"));
        var (service, context, _) = CreateService(
            nameof(GetDashboardAsync_CountsTodayUsingSchoolTimeZoneBoundary),
            timeProvider: schoolClock);
        context.Votes.AddRange(
            new Vote { Id = 1, UserId = 1, QueueItemId = 1, VotedAt = new DateTime(2026, 10, 14, 22, 15, 0) },
            new Vote { Id = 2, UserId = 2, QueueItemId = 1, VotedAt = new DateTime(2026, 10, 14, 21, 59, 0) });
        await context.SaveChangesAsync();

        var dto = await service.GetDashboardAsync();

        Assert.Equal(1, dto.Votes.Today);
        Assert.Equal(new DateOnly(2026, 10, 15), dto.DailyActivity[^1].Date);
        Assert.Equal(1, dto.DailyActivity[^1].VotesCount);
    }

    [Fact]
    public async Task GetDashboardAsync_WithNoData_ReturnsZeroCountersAndEmptyCollections()
    {
        var (service, _, _) = CreateService(nameof(GetDashboardAsync_WithNoData_ReturnsZeroCountersAndEmptyCollections));

        var dto = await service.GetDashboardAsync();

        Assert.Equal(0, dto.Votes.Total);
        Assert.Equal(0, dto.Votes.ThisWeek);
        Assert.Equal(0, dto.Votes.Today);
        Assert.Empty(dto.Genres);
        Assert.Empty(dto.TopSongs);
        Assert.Empty(dto.TopArtists);
        Assert.Equal(7, dto.DailyActivity.Count);
        Assert.All(dto.DailyActivity, day => Assert.Equal(0, day.VotesCount));
    }

    [Fact]
    public async Task GetDashboardAsync_GroupsGenres_ByPlays_SkippingSongsWithoutGenre()
    {
        var (service, context, _) = CreateService(nameof(GetDashboardAsync_GroupsGenres_ByPlays_SkippingSongsWithoutGenre));

        // pop: 3 odtworzenia, rock: 2 odtworzenia, brak gatunku: 1 odtworzenie.
        for (int i = 0; i < 3; i++)
        {
            SeedSong(context, 1 + i, $"Pop {i}", "Popper", "pop", 10 + i);
        }
        for (int i = 0; i < 2; i++)
        {
            SeedSong(context, 10 + i, $"Rock {i}", "Rocker", "rock", 20 + i);
        }
        SeedSong(context, 20, "No Genre", "Unknown", null, 30);

        var brk = CreateBreak(1);
        context.Breaks.Add(brk);

        int historyId = 1;
        for (int songId = 1; songId <= 3; songId++)
        {
            context.Histories.Add(new History { Id = historyId++, SongId = songId, BreakId = brk.Id, PlayedAt = Now.AddDays(-1), Votes = 2 });
        }
        for (int songId = 10; songId <= 11; songId++)
        {
            context.Histories.Add(new History { Id = historyId++, SongId = songId, BreakId = brk.Id, PlayedAt = Now.AddDays(-2), Votes = 1 });
        }
        context.Histories.Add(new History { Id = historyId++, SongId = 20, BreakId = brk.Id, PlayedAt = Now.AddDays(-1), Votes = 5 });

        await context.SaveChangesAsync();

        var dto = await service.GetDashboardAsync();

        Assert.Equal(2, dto.Genres.Count);
        Assert.Equal("pop", dto.Genres[0].Genre);
        Assert.Equal(3, dto.Genres[0].PlaysCount);
        Assert.Equal("rock", dto.Genres[1].Genre);
        Assert.Equal(2, dto.Genres[1].PlaysCount);

        // Udział liczony jest względem wszystkich odtworzeń w oknie (6, w tym 1 bez gatunku),
        // więc suma procentów z uwzglętnieniem pominiętego gatunku wynosi 100.
        Assert.Equal(50d, dto.Genres[0].SharePercent);
        Assert.Equal(100d, dto.Genres.Sum(g => g.SharePercent) + 16.67d, 2);
    }

    [Fact]
    public async Task GetDashboardAsync_ExcludesPlaysOlderThanThirtyDays_FromGenres()
    {
        var (service, context, _) = CreateService(nameof(GetDashboardAsync_ExcludesPlaysOlderThanThirtyDays_FromGenres));

        SeedSong(context, 1, "Fresh", "Artist", "pop", 1);
        SeedSong(context, 2, "Old", "Artist", "jazz", 1);

        var brk = CreateBreak(1);
        context.Breaks.Add(brk);

        context.Histories.Add(new History { Id = 1, SongId = 1, BreakId = brk.Id, PlayedAt = Now.AddDays(-10), Votes = 1 });
        context.Histories.Add(new History { Id = 2, SongId = 2, BreakId = brk.Id, PlayedAt = Now.AddDays(-40), Votes = 1 });

        await context.SaveChangesAsync();

        var dto = await service.GetDashboardAsync();

        Assert.Single(dto.Genres);
        Assert.Equal("pop", dto.Genres[0].Genre);
        Assert.Equal(100d, dto.Genres[0].SharePercent);
    }

    [Fact]
    public async Task GetDashboardAsync_ReturnsSevenDaysOfActivity_OldestFirst_WithZeroFilledDays()
    {
        var (service, context, _) = CreateService(nameof(GetDashboardAsync_ReturnsSevenDaysOfActivity_OldestFirst_WithZeroFilledDays));

        var brk = CreateBreak(1);
        context.Breaks.Add(brk);
        SeedSong(context, 1, "Song", "Artist", "pop", 1);

        var item = SeedQueueItem(context, 1, brk.Id, 1, 1);
        SeedVote(context, 1, item.Id, 201, Now.Date.AddHours(8));   // dzisiaj
        SeedVote(context, 2, item.Id, 202, Now.Date.AddDays(-1));     // wczoraj
        SeedVote(context, 3, item.Id, 203, Now.Date.AddDays(-20));    // poza oknem 7 dni

        context.Histories.Add(new History { Id = 1, SongId = 1, BreakId = brk.Id, PlayedAt = Now.Date.AddHours(9), Votes = 3 });
        await context.SaveChangesAsync();

        var dto = await service.GetDashboardAsync();

        Assert.Equal(7, dto.DailyActivity.Count);
        Assert.Equal(DateOnly.FromDateTime(Now.Date.AddDays(-6)), dto.DailyActivity[0].Date);
        Assert.Equal(DateOnly.FromDateTime(Now.Date), dto.DailyActivity[^1].Date);

        var today = dto.DailyActivity[^1];
        Assert.Equal(1, today.VotesCount);
        Assert.Equal(1, today.PlaysCount);

        var yesterday = dto.DailyActivity[^2];
        Assert.Equal(1, yesterday.VotesCount);
        Assert.Equal(0, yesterday.PlaysCount);

        Assert.Equal(2, dto.DailyActivity.Sum(d => d.VotesCount)); // głos sprzed 20 dni poza oknem
        Assert.Equal(0, dto.DailyActivity[0].VotesCount);
    }

    [Fact]
    public async Task GetDashboardAsync_ReturnsTopTenSongs_OrderedByPlaysThenVotes()
    {
        var (service, context, _) = CreateService(nameof(GetDashboardAsync_ReturnsTopTenSongs_OrderedByPlaysThenVotes));

        var brk = CreateBreak(1);
        context.Breaks.Add(brk);

        // 12 utworów: utwór 1 ma 3 odtworzenia, kolejne mają po 1, ale rosnąco liczbę głosów.
        int historyId = 1;
        for (int songId = 1; songId <= 12; songId++)
        {
            SeedSong(context, songId, $"Song {songId}", $"Artist {songId}", "pop", songId);
            int plays = songId == 1 ? 3 : 1;
            for (int p = 0; p < plays; p++)
            {
                context.Histories.Add(new History
                {
                    Id = historyId++,
                    SongId = songId,
                    BreakId = brk.Id,
                    PlayedAt = Now.AddDays(-1),
                    Votes = songId == 1 ? 10 : songId
                });
            }
        }

        await context.SaveChangesAsync();

        var dto = await service.GetDashboardAsync();

        Assert.Equal(10, dto.TopSongs.Count);
        Assert.Equal(1, dto.TopSongs[0].Position);
        Assert.Equal("Song 1", dto.TopSongs[0].Title);
        Assert.Equal(3, dto.TopSongs[0].PlaysCount);

        // Song 1 wygrywa liczbą odtworzeń; pozostałe miejsca zajmują utwory z jednym odtworzeniem,
        // uporządkowane malejąco po liczbie głosów (12 … 4 — dziewięć utworów mieści się w limitie 10).
        Assert.Equal("Song 12", dto.TopSongs[1].Title);
        Assert.Equal("Song 11", dto.TopSongs[2].Title);
        Assert.Equal("Song 4", dto.TopSongs[9].Title);
        Assert.DoesNotContain(dto.TopSongs, s => s.Title == "Song 3");
    }

    [Fact]
    public async Task GetDashboardAsync_RollsUpMultipleSongsIntoSingleArtistEntry()
    {
        var (service, context, _) = CreateService(nameof(GetDashboardAsync_RollsUpMultipleSongsIntoSingleArtistEntry));

        var brk = CreateBreak(1);
        context.Breaks.Add(brk);

        // Wykonawca 1 ma trzy utwory (łącznie 3 odtworzenia), wykonawca 2 jeden utwór (1 odtworzenie).
        SeedSong(context, 1, "A1", "Daft Punk", "electronic", 1);
        SeedSong(context, 2, "A2", "Daft Punk", "electronic", 1);
        SeedSong(context, 3, "A3", "Daft Punk", "electronic", 1);
        SeedSong(context, 4, "B1", "Solo Artist", "pop", 2);

        context.Histories.Add(new History { Id = 1, SongId = 1, BreakId = brk.Id, PlayedAt = Now.AddDays(-1), Votes = 3 });
        context.Histories.Add(new History { Id = 2, SongId = 2, BreakId = brk.Id, PlayedAt = Now.AddDays(-1), Votes = 2 });
        context.Histories.Add(new History { Id = 3, SongId = 3, BreakId = brk.Id, PlayedAt = Now.AddDays(-2), Votes = 1 });
        context.Histories.Add(new History { Id = 4, SongId = 4, BreakId = brk.Id, PlayedAt = Now.AddDays(-1), Votes = 7 });

        await context.SaveChangesAsync();

        var dto = await service.GetDashboardAsync();

        Assert.Equal(2, dto.TopArtists.Count);
        Assert.Equal(1, dto.TopArtists[0].ArtistId);
        Assert.Equal("Daft Punk", dto.TopArtists[0].ArtistName);
        Assert.Equal(3, dto.TopArtists[0].PlaysCount);
        Assert.Equal(6, dto.TopArtists[0].TotalVotes); // 3 + 2 + 1
        Assert.Equal(2, dto.TopArtists[1].ArtistId);
        Assert.Equal(1, dto.TopArtists[1].PlaysCount);
        Assert.Equal(7, dto.TopArtists[1].TotalVotes);
    }

    [Fact]
    public async Task GetDashboardAsync_OnCacheMiss_ComputesFromDatabaseAndWritesCache()
    {
        var (service, context, database) = CreateService(nameof(GetDashboardAsync_OnCacheMiss_ComputesFromDatabaseAndWritesCache));

        // TOP utworów liczone jest z tabeli history — potrzebny wpis o odtworzeniu.
        SeedSong(context, 1, "Cached Song", "Artist", "pop", 1);
        context.Breaks.Add(CreateBreak(1));
        context.Histories.Add(new History { Id = 1, SongId = 1, BreakId = 1, PlayedAt = Now.AddDays(-1), Votes = 4 });
        await context.SaveChangesAsync();

        var dto = await service.GetDashboardAsync();

        Assert.Equal("Cached Song", dto.TopSongs[0].Title);
        Assert.Equal(1, dto.TopSongs[0].PlaysCount);

        // Wynik musi zostać zapisany w Redis pod kluczem statistics:dashboard na 60 sekund.
        var write = Assert.Single(database.Invocations, i => i.Method.Name == "StringSetAsync");
        Assert.Equal("statistics:dashboard", Assert.IsType<RedisKey>(write.Arguments[0]).ToString());

        Assert.Equal(TimeSpan.FromSeconds(60), Assert.IsType<TimeSpan>(write.Arguments[2]));
    }

    [Fact]
    public async Task GetDashboardAsync_OnCacheHit_ReturnsCachedPayload_WithoutQueryingDatabase()
    {
        var cachedDashboard = new DashboardStatisticsDto(
            GeneratedAt: new DateTime(2020, 1, 1, 8, 0, 0),
            Votes: new VoteTotalsDto(Total: 999, ThisWeek: 111, Today: 22),
            Genres: new List<GenreStatDto> { new GenreStatDto("cached-genre", 5, 100d) },
            DailyActivity: new List<DailyActivityDto>(),
            TopSongs: new List<TopSongStatDto> { new TopSongStatDto(1, 1, "Stary utwór", "Stary wykonawca", 4, 3) },
            TopArtists: new List<TopArtistStatDto>());

        var cachedJson = JsonSerializer.Serialize(cachedDashboard);

        var (service, context, database) = CreateService(
            nameof(GetDashboardAsync_OnCacheHit_ReturnsCachedPayload_WithoutQueryingDatabase),
            new RedisValue(cachedJson));

        // Dane w bazie są inne niż w cache — odpowiedź musi pochodzić z Redisu.
        SeedSong(context, 1, "Inna piosenka", "Inny artysta", "rock", 1);
        await context.SaveChangesAsync();

        var dto = await service.GetDashboardAsync();

        Assert.Equal(999, dto.Votes.Total);
        Assert.Equal(111, dto.Votes.ThisWeek);
        Assert.Equal(22, dto.Votes.Today);
        Assert.Equal("Stary utwór", dto.TopSongs[0].Title);
        Assert.Equal("cached-genre", dto.Genres[0].Genre);
        Assert.Equal(new DateTime(2020, 1, 1, 8, 0, 0), dto.GeneratedAt);

        // Trafienie w cache nie może nadpisywać wpisu ani odpytywać bazy.
        Assert.DoesNotContain(database.Invocations, i => i.Method.Name == "StringSetAsync");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    public async Task GetDashboardAsync_WhenCacheIsInvalid_RemovesEntryAndFallsBackToDatabase(string cachedJson)
    {
        var (service, _, database) = CreateService(
            $"{nameof(GetDashboardAsync_WhenCacheIsInvalid_RemovesEntryAndFallsBackToDatabase)}_{cachedJson.Length}",
            new RedisValue(cachedJson));
        database.Setup(item => item.KeyDeleteAsync(
                It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        var dto = await service.GetDashboardAsync();

        Assert.True(dto.IsAvailable);
        database.Verify(item => item.KeyDeleteAsync("statistics:dashboard", CommandFlags.None), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetDashboardAsync_WhenManyRequestsMissCache_RefreshesCacheOnlyOnce()
    {
        var context = CreateInMemoryContext(nameof(GetDashboardAsync_WhenManyRequestsMissCache_RefreshesCacheOnlyOnce));
        RedisValue cache = RedisValue.Null;
        var database = new Mock<IDatabase>();
        database.Setup(item => item.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => cache);
        database.Setup(item => item.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()))
            .Callback<RedisKey, RedisValue, TimeSpan?, When>((_, value, _, _) => cache = value)
            .ReturnsAsync(true);
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(item => item.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        var service = new StatisticsService(
            context,
            redis.Object,
            new ManualTimeProvider(Now),
            NullLogger<StatisticsService>.Instance,
            new StatisticsRefreshCoordinator());

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => service.GetDashboardAsync()));

        Assert.All(results, result => Assert.True(result.IsAvailable));
        database.Verify(item => item.StringSetAsync(
            "statistics:dashboard",
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            When.Always), Times.Once);
    }

    [Fact]
    public async Task GetDashboardAsync_WhenRedisThrows_FallsBackToDatabase()
    {
        var context = CreateInMemoryContext(nameof(GetDashboardAsync_WhenRedisThrows_FallsBackToDatabase));

        var mockDatabase = new Mock<IDatabase>();
        mockDatabase
            .Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(
                ConnectionFailureType.UnableToConnect,
                CommandFlags.None,
                "brak połączenia z Redisem",
                innerException: null!,
                commandStatus: default));
        mockDatabase
            .Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<When>()))
            .ThrowsAsync(new RedisConnectionException(
                ConnectionFailureType.UnableToConnect,
                CommandFlags.None,
                "brak połączenia z Redisem",
                innerException: null!,
                commandStatus: default));

        var mockRedis = new Mock<IConnectionMultiplexer>();
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        var fallbackService = new StatisticsService(
            context,
            mockRedis.Object,
            new ManualTimeProvider(Now),
            NullLogger<StatisticsService>.Instance);

        SeedSong(context, 1, "Fallback Song", "Artist", "pop", 1);
        context.Breaks.Add(CreateBreak(1));
        context.Histories.Add(new History { Id = 1, SongId = 1, BreakId = 1, PlayedAt = Now.AddDays(-1), Votes = 4 });
        await context.SaveChangesAsync();

        var dto = await fallbackService.GetDashboardAsync();

        Assert.Equal("Fallback Song", dto.TopSongs[0].Title);
        Assert.Equal(1, dto.TopSongs[0].PlaysCount);
        Assert.Equal(7, dto.DailyActivity.Count);
        Assert.True(dto.IsAvailable);
    }

    [Fact]
    public async Task GetDashboardAsync_WhenProgrammingStateIsInvalid_DoesNotMaskErrorAsServiceUnavailable()
    {
        var context = CreateInMemoryContext(nameof(GetDashboardAsync_WhenProgrammingStateIsInvalid_DoesNotMaskErrorAsServiceUnavailable));
        // Użycie zwolnionego DbContext oznacza błąd cyklu życia aplikacji, a nie chwilową awarię bazy.
        await context.DisposeAsync();

        var mockRedis = new Mock<IConnectionMultiplexer>();
        var mockDb = new Mock<IDatabase>();
        mockDb.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(RedisValue.Null);
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDb.Object);

        var service = new StatisticsService(
            context,
            mockRedis.Object,
            new ManualTimeProvider(Now),
            NullLogger<StatisticsService>.Instance);

        var startTime = DateTime.UtcNow;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetDashboardAsync());
        var duration = DateTime.UtcNow - startTime;

        // Błąd powinien zostać zwrócony od razu, bez ukrywania go jako HTTP 503 i bez pętli retry.
        Assert.True(duration < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GetDashboardAsync_WhenDatabaseConnectionFails_ThrowsStatisticsUnavailableException()
    {
        var options = new DbContextOptionsBuilder<schoolmusicContext>()
            .UseMySql(
                "server=127.0.0.1;port=1;database=missing;user=missing;password=missing;Connection Timeout=1",
                new MariaDbServerVersion(new Version(10, 4, 0)))
            .Options;
        await using var context = new schoolmusicContext(options);
        var redis = new Mock<IConnectionMultiplexer>();
        var database = new Mock<IDatabase>();
        database.Setup(item => item.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        redis.Setup(item => item.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        var service = new StatisticsService(
            context,
            redis.Object,
            new ManualTimeProvider(Now),
            NullLogger<StatisticsService>.Instance,
            new StatisticsRefreshCoordinator());

        var exception = await Assert.ThrowsAsync<StatisticsUnavailableException>(() => service.GetDashboardAsync());

        Assert.Contains("chwilowo niedostępne", exception.Message);
    }

    [Fact]
    public async Task StatisticsController_WhenServiceThrowsStatisticsUnavailable_Returns503WithRetryAfterHeader()
    {
        var mockService = new Mock<IStatisticsService>();
        mockService.Setup(s => s.GetDashboardAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StatisticsUnavailableException("Statystyki są chwilowo niedostępne z powodu problemu z bazą danych."));

        var controller = new StatisticsController(mockService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.GetDashboard(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
        Assert.Equal("15", controller.Response.Headers.RetryAfter.ToString());

        var body = objectResult.Value;
        Assert.NotNull(body);
        var isAvailableProp = body.GetType().GetProperty("IsAvailable")?.GetValue(body);
        Assert.Equal(false, isAvailableProp);
    }
}
