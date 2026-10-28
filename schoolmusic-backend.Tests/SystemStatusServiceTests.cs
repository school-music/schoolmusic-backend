using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;
using Xunit;

namespace schoolmusic_backend.Tests;

/// <summary>Deterministyczny <see cref="TimeProvider"/> dla testów.</summary>
public sealed class ManualTimeProvider(DateTime now) : TimeProvider
{
    private DateTimeOffset _now = new(DateTime.SpecifyKind(now, DateTimeKind.Utc));

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan value) => _now = _now.Add(value);
}

public class SystemStatusServiceTests
{
    // Środa 14.10.2026 — weekday ISO = 3.
    private static readonly DateOnly TestDate = new(2026, 10, 14);

    private static schoolmusicContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<schoolmusicContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new schoolmusicContext(options);
    }

    private static (SystemStatusService service, schoolmusicContext context) CreateService(string dbName, DateTime now)
    {
        var context = CreateInMemoryContext(dbName);
        var clock = new ManualTimeProvider(now);
        return (new SystemStatusService(context, clock), context);
    }

    private static Break CreateBreak(
        int id = 1,
        sbyte breakNumber = 1,
        DateTime? start = null,
        DateTime? end = null,
        bool musicEnabled = true)
    {
        return new Break
        {
            Id = id,
            BreakDate = TestDate,
            BreakNumber = breakNumber,
            StartAt = start ?? TestDate.ToDateTime(new TimeOnly(9, 40)),
            EndsAt = end ?? TestDate.ToDateTime(new TimeOnly(9, 50)),
            MusicEnabled = musicEnabled
        };
    }

    [Fact]
    public async Task GetStatusAsync_DuringLesson_ReturnsLessonState_CountingDownToNextBreak()
    {
        // 08:30 — lekcja, następna przerwa o 09:40.
        var now = TestDate.ToDateTime(new TimeOnly(8, 30));
        var (service, context) = CreateService(nameof(GetStatusAsync_DuringLesson_ReturnsLessonState_CountingDownToNextBreak), now);

        context.Breaks.Add(CreateBreak(breakNumber: 2, start: TestDate.ToDateTime(new TimeOnly(9, 40)), end: TestDate.ToDateTime(new TimeOnly(9, 50))));
        await context.SaveChangesAsync();

        var dto = await service.GetStatusAsync();

        Assert.Equal(SchoolState.Lesson, dto.CurrentState);
        Assert.Equal(TestDate.ToDateTime(new TimeOnly(9, 40)), dto.NextBreakStartsAt);
        Assert.Null(dto.BreakEndsAt);
        Assert.Equal(2, dto.NextBreakNumber);
        Assert.Null(dto.CurrentBreakNumber);
        Assert.False(dto.MusicEnabled);
        Assert.Equal(4200, dto.SecondsRemaining); // 70 minut
        Assert.Equal(now, dto.ServerTime);
    }

    [Fact]
    public async Task GetStatusAsync_DuringBreak_ReturnsBreakState_CountingDownToBreakEnd()
    {
        // 09:45 — przerwa 09:40–09:50, zostało 5 minut.
        var now = TestDate.ToDateTime(new TimeOnly(9, 45));
        var (service, context) = CreateService(nameof(GetStatusAsync_DuringBreak_ReturnsBreakState_CountingDownToBreakEnd), now);

        context.Breaks.Add(CreateBreak(breakNumber: 2, start: TestDate.ToDateTime(new TimeOnly(9, 40)), end: TestDate.ToDateTime(new TimeOnly(9, 50))));
        await context.SaveChangesAsync();

        var dto = await service.GetStatusAsync();

        Assert.Equal(SchoolState.Break, dto.CurrentState);
        Assert.Equal(TestDate.ToDateTime(new TimeOnly(9, 50)), dto.BreakEndsAt);
        Assert.Null(dto.NextBreakStartsAt);
        Assert.Equal(2, dto.CurrentBreakNumber);
        Assert.Equal(300, dto.SecondsRemaining);
        Assert.True(dto.MusicEnabled);
    }

    [Fact]
    public async Task GetStatusAsync_DuringBreak_IncludesNowPlaying_FromPlaybackState()
    {
        var now = TestDate.ToDateTime(new TimeOnly(9, 45));
        var (service, context) = CreateService(nameof(GetStatusAsync_DuringBreak_IncludesNowPlaying_FromPlaybackState), now);

        var artist = new Artist { Id = 1, Name = "Daft Punk", Source = "other" };
        var song = new Song { Id = 1, Title = "Get Lucky", ArtistId = 1, Artist = artist, DurationMs = 240000, Source = "other", CreatedAt = now };
        var user = new User { Id = 1, UserLogin = "zygmunt", UserPassword = "x", RankId = 1, CreatedAt = now };
        var brk = CreateBreak();

        var queueItem = new QueueItem
        {
            Id = 100,
            BreakId = brk.Id,
            SongId = song.Id,
            Song = song,
            UserId = user.Id,
            User = user,
            ModerationStatus = "approved",
            Status = "playing",
            OrderIndex = 1
        };

        context.Artists.Add(artist);
        context.Songs.Add(song);
        context.Users.Add(user);
        context.Breaks.Add(brk);
        context.QueueItems.Add(queueItem);
        context.PlaybackStates.Add(new PlaybackState
        {
            Id = 1,
            CurrentQueueItemId = queueItem.Id,
            StartedAt = TestDate.ToDateTime(new TimeOnly(9, 43)), // gra od 2 minut
            IsPaused = false,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        var dto = await service.GetStatusAsync();

        Assert.NotNull(dto.NowPlaying);
        Assert.Equal("Get Lucky", dto.NowPlaying!.Title);
        Assert.Equal("Daft Punk", dto.NowPlaying.ArtistName);
        Assert.Equal(120, dto.NowPlaying.ElapsedSeconds);
        Assert.Equal(120, dto.NowPlaying.RemainingSeconds); // 240s - 120s
        Assert.False(dto.NowPlaying.IsPaused);
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsTopFiveTracks_OrderedByVotes()
    {
        var now = TestDate.ToDateTime(new TimeOnly(9, 45));
        var (service, context) = CreateService(nameof(GetStatusAsync_ReturnsTopFiveTracks_OrderedByVotes), now);

        var artist = new Artist { Id = 1, Name = "Artist", Source = "other" };
        var brk = CreateBreak();
        context.Artists.Add(artist);
        context.Breaks.Add(brk);

        // 6 utworów z rosnącą liczbą głosów — endpoint zwraca tylko TOP 5.
        for (int i = 0; i < 6; i++)
        {
            var song = new Song { Id = i + 1, Title = $"Song {i + 1}", ArtistId = 1, Artist = artist, DurationMs = 180000, Source = "other", CreatedAt = now };
            var queueItem = new QueueItem
            {
                Id = 200 + i,
                BreakId = brk.Id,
                SongId = song.Id,
                Song = song,
                ModerationStatus = "approved",
                Status = "queued",
                OrderIndex = i + 1
            };
            context.Songs.Add(song);
            context.QueueItems.Add(queueItem);

            for (int v = 0; v <= i; v++)
            {
                context.Votes.Add(new Vote { Id = 1000 + (i * 10) + v, QueueItemId = queueItem.Id, UserId = 500 + (i * 10) + v, VotedAt = now });
            }
        }
        await context.SaveChangesAsync();

        var dto = await service.GetStatusAsync();

        Assert.Equal(5, dto.TopTracks.Count);
        Assert.Equal("Song 6", dto.TopTracks[0].Title);
        Assert.Equal(1, dto.TopTracks[0].Position);
        Assert.Equal(6, dto.TopTracks[0].VotesCount);
        Assert.Equal("Song 2", dto.TopTracks[4].Title);
        Assert.Equal(2, dto.TopTracks[4].VotesCount);
    }

    [Fact]
    public async Task GetStatusAsync_OnDayOffException_ReturnsHolidayState_WithoutMusic()
    {
        var now = TestDate.ToDateTime(new TimeOnly(9, 45));
        var (service, context) = CreateService(nameof(GetStatusAsync_OnDayOffException_ReturnsHolidayState_WithoutMusic), now);

        context.ExceptionDays.Add(new ExceptionDay
        {
            Id = 1,
            StartDate = TestDate,
            EndDate = TestDate,
            DayType = "day_off",
            Description = "Dzień Nauczyciela"
        });
        context.Breaks.Add(CreateBreak());
        await context.SaveChangesAsync();

        var dto = await service.GetStatusAsync();

        Assert.Equal(SchoolState.Holiday, dto.CurrentState);
        Assert.False(dto.MusicEnabled);
        Assert.Equal(0, dto.SecondsRemaining);
        Assert.Null(dto.NowPlaying);
        Assert.Empty(dto.TopTracks);
        Assert.Contains("Dzień Nauczyciela", dto.Message);
    }

    [Fact]
    public async Task GetStatusAsync_AfterAllBreaks_ReturnsAfterHoursState()
    {
        var now = TestDate.ToDateTime(new TimeOnly(15, 0));
        var (service, context) = CreateService(nameof(GetStatusAsync_AfterAllBreaks_ReturnsAfterHoursState), now);

        context.Breaks.Add(CreateBreak(breakNumber: 1, start: TestDate.ToDateTime(new TimeOnly(9, 40)), end: TestDate.ToDateTime(new TimeOnly(9, 50))));
        context.Breaks.Add(CreateBreak(id: 2, breakNumber: 2, start: TestDate.ToDateTime(new TimeOnly(11, 30)), end: TestDate.ToDateTime(new TimeOnly(11, 45))));
        await context.SaveChangesAsync();

        var dto = await service.GetStatusAsync();

        Assert.Equal(SchoolState.AfterHours, dto.CurrentState);
        Assert.Equal(0, dto.SecondsRemaining);
        Assert.Null(dto.BreakEndsAt);
        Assert.Null(dto.NextBreakStartsAt);
        Assert.False(dto.MusicEnabled);
    }

    [Fact]
    public async Task GetStatusAsync_DuringBreakMutedByExceptionBreak_ReportsMusicDisabledWithReason()
    {
        var now = TestDate.ToDateTime(new TimeOnly(9, 45));
        var (service, context) = CreateService(nameof(GetStatusAsync_DuringBreakMutedByExceptionBreak_ReportsMusicDisabledWithReason), now);

        context.Breaks.Add(CreateBreak(breakNumber: 2));
        context.ExceptionBreaks.Add(new ExceptionBreak
        {
            Id = 1,
            BreakDate = TestDate,
            BreakNumber = 2,
            MusicEnabled = false,
            Description = "Apel szkolny"
        });
        await context.SaveChangesAsync();

        var dto = await service.GetStatusAsync();

        Assert.Equal(SchoolState.Break, dto.CurrentState);
        Assert.False(dto.MusicEnabled);
        Assert.Equal("Apel szkolny", dto.MuteReason);
        Assert.Contains("Apel szkolny", dto.Message);
    }

    [Fact]
    public async Task GetStatusAsync_WithNoBreaksToday_UsesNewestRowPerBreakNumber()
    {
        // Brak wierszy na dziś — fallback bierze najnowszy wiersz na numer przerwy.
        // Starszy wiersz (do 10:00) nie może przykryć nowszego (do 09:50): o 09:55 ma być AfterHours.
        var now = TestDate.ToDateTime(new TimeOnly(9, 55));
        var (service, context) = CreateService(nameof(GetStatusAsync_WithNoBreaksToday_UsesNewestRowPerBreakNumber), now);

        context.Breaks.Add(new Break
        {
            Id = 1,
            BreakDate = new DateOnly(2026, 10, 7),
            BreakNumber = 1,
            StartAt = TestDate.ToDateTime(new TimeOnly(9, 40)),
            EndsAt = TestDate.ToDateTime(new TimeOnly(10, 0)),
            MusicEnabled = true
        });
        context.Breaks.Add(new Break
        {
            Id = 2,
            BreakDate = new DateOnly(2026, 10, 13),
            BreakNumber = 1,
            StartAt = TestDate.ToDateTime(new TimeOnly(9, 40)),
            EndsAt = TestDate.ToDateTime(new TimeOnly(9, 50)),
            MusicEnabled = true
        });
        await context.SaveChangesAsync();

        var dto = await service.GetStatusAsync();

        Assert.Equal(SchoolState.AfterHours, dto.CurrentState);
        Assert.Null(dto.BreakEndsAt);
        Assert.Equal(0, dto.SecondsRemaining);
    }

    [Fact]
    public async Task GetStatusAsync_WithMemoryCache_CachesBreakDataAndInvalidatesSuccessfully()
    {
        var now = TestDate.ToDateTime(new TimeOnly(9, 45));
        var context = CreateInMemoryContext(nameof(GetStatusAsync_WithMemoryCache_CachesBreakDataAndInvalidatesSuccessfully));
        var clock = new ManualTimeProvider(now);
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = new SystemStatusService(context, clock, memoryCache);

        var breakEntity = CreateBreak(breakNumber: 1, start: TestDate.ToDateTime(new TimeOnly(9, 40)), end: TestDate.ToDateTime(new TimeOnly(9, 50)));
        context.Breaks.Add(breakEntity);
        await context.SaveChangesAsync();

        var firstResult = await service.GetStatusAsync();
        Assert.Equal(SchoolState.Break, firstResult.CurrentState);

        // Remove the break from context
        context.Breaks.Remove(breakEntity);
        await context.SaveChangesAsync();

        // Second call should return cached break
        var secondResult = await service.GetStatusAsync();
        Assert.Equal(SchoolState.Break, secondResult.CurrentState);

        // Invalidate cache
        service.InvalidateScheduleCache();

        // Third call now reflects database change (no breaks left today -> Holiday or AfterHours)
        var thirdResult = await service.GetStatusAsync();
        Assert.NotEqual(SchoolState.Break, thirdResult.CurrentState);
    }
}
