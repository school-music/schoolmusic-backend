using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using schoolmusic_backend.Controllers;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Tests;

public class RankingsTests
{
    private static schoolmusicContext CreateContext() => new(
        new DbContextOptionsBuilder<schoolmusicContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (Artist Artist, Song Song) AddSong(schoolmusicContext db, int id, string title, int artistId, string artistName)
    {
        var artist = db.Artists.Local.FirstOrDefault(a => a.Id == artistId)
            ?? new Artist { Id = artistId, Name = artistName, Source = "other" };
        if (db.Artists.Local.All(a => a.Id != artistId))
            db.Artists.Add(artist);
        var song = new Song
        {
            Id = id, Title = title, ArtistId = artistId, Artist = artist, Cover = $"cover-{id}",
            Source = "other", DurationMs = 180000, IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Songs.Add(song);
        return (artist, song);
    }

    [Fact]
    public async Task RankingsService_RanksSongsAndArtistsForPeriodAndAggregatesPlaysAndVotes()
    {
        await using var db = CreateContext();
        var brk = new Break { Id = 1, BreakDate = DateOnly.FromDateTime(DateTime.Today), BreakNumber = 1, StartAt = DateTime.Today, EndsAt = DateTime.Today.AddHours(1) };
        db.Breaks.Add(brk);
        var (_, songA) = AddSong(db, 1, "First", 1, "Shared artist");
        var (_, songB) = AddSong(db, 2, "Second", 1, "Shared artist");
        var (_, songC) = AddSong(db, 3, "Old", 2, "Old artist");
        var now = DateTime.UtcNow;
        db.Histories.AddRange(
            new History { Id = 1, SongId = songA.Id, Song = songA, BreakId = brk.Id, Break = brk, PlayedAt = now.AddHours(-1), Votes = 2 },
            new History { Id = 2, SongId = songA.Id, Song = songA, BreakId = brk.Id, Break = brk, PlayedAt = now.AddHours(-2), Votes = 1 },
            new History { Id = 3, SongId = songB.Id, Song = songB, BreakId = brk.Id, Break = brk, PlayedAt = now.AddDays(-2), Votes = 5 },
            new History { Id = 4, SongId = songC.Id, Song = songC, BreakId = brk.Id, Break = brk, PlayedAt = now.AddDays(-30), Votes = 50 });
        await db.SaveChangesAsync();
        var service = new RankingsService(db);

        var songs = await service.GetTopSongsAsync("week", 10);
        Assert.Equal(2, songs.Count);
        Assert.Equal("First", songs[0].Title);
        Assert.Equal(2, songs[0].PlaysCount);
        Assert.Equal(3, songs[0].TotalVotes);
        Assert.Equal(1, songs[0].Position);
        Assert.Equal("Second", songs[1].Title);

        var artists = await service.GetTopArtistsAsync("week", 10);
        var shared = Assert.Single(artists);
        Assert.Equal("Shared artist", shared.ArtistName);
        Assert.Equal(3, shared.PlaysCount);
        Assert.Equal(8, shared.TotalVotes);
    }

    [Theory]
    [InlineData("day", 0)]
    [InlineData("week", 6)]
    [InlineData("month", 29)]
    [InlineData("year", 364)]
    public async Task RankingsService_FiltersSongsAndArtistsByPeriod(string period, int recentAgeDays)
    {
        await using var db = CreateContext();
        var brk = new Break { Id = 1, BreakDate = DateOnly.FromDateTime(DateTime.Today), BreakNumber = 1, StartAt = DateTime.Today, EndsAt = DateTime.Today.AddHours(1) };
        db.Breaks.Add(brk);
        var (_, recentSong) = AddSong(db, 1, "Recent", 1, "Recent artist");
        var (_, oldSong) = AddSong(db, 2, "Old", 2, "Old artist");
        var now = DateTime.UtcNow;
        db.Histories.AddRange(
            new History { Id = 1, SongId = recentSong.Id, Song = recentSong, BreakId = brk.Id, Break = brk, PlayedAt = now.AddDays(-recentAgeDays), Votes = 1 },
            new History { Id = 2, SongId = oldSong.Id, Song = oldSong, BreakId = brk.Id, Break = brk, PlayedAt = now.AddDays(-800), Votes = 10 });
        await db.SaveChangesAsync();
        var service = new RankingsService(db);

        var songs = await service.GetTopSongsAsync(period);
        var artists = await service.GetTopArtistsAsync(period);

        Assert.Equal("Recent", Assert.Single(songs).Title);
        Assert.Equal("Recent artist", Assert.Single(artists).ArtistName);
    }

    [Fact]
    public async Task RankingsService_AllTimeIncludesOldHistoryAndClampsLimit()
    {
        await using var db = CreateContext();
        var brk = new Break { Id = 1, BreakDate = DateOnly.FromDateTime(DateTime.Today), BreakNumber = 1, StartAt = DateTime.Today, EndsAt = DateTime.Today.AddHours(1) };
        db.Breaks.Add(brk);
        var (_, song) = AddSong(db, 1, "All-time song", 1, "Artist");
        db.Histories.Add(new History
        {
            Id = 1, SongId = song.Id, Song = song, BreakId = brk.Id, Break = brk,
            PlayedAt = DateTime.UtcNow.AddYears(-2), Votes = 4
        });
        await db.SaveChangesAsync();

        var results = await new RankingsService(db).GetTopSongsAsync("all-time", 0);

        Assert.Single(results);
        Assert.Equal("All-time song", results[0].Title);
    }

    [Fact]
    public async Task RankingsService_RecentHistoryAppliesOffsetAndLimitInNewestFirstOrder()
    {
        await using var db = CreateContext();
        var brk = new Break { Id = 1, BreakDate = DateOnly.FromDateTime(DateTime.Today), BreakNumber = 1, StartAt = DateTime.Today, EndsAt = DateTime.Today.AddHours(1) };
        db.Breaks.Add(brk);
        var (_, song) = AddSong(db, 1, "Song", 1, "Artist");
        var baseTime = DateTime.UtcNow;
        for (int i = 0; i < 4; i++)
        {
            db.Histories.Add(new History
            {
                Id = i + 1, SongId = song.Id, Song = song, BreakId = brk.Id, Break = brk,
                PlayedAt = baseTime.AddMinutes(i), Votes = i
            });
        }
        await db.SaveChangesAsync();

        var results = await new RankingsService(db).GetRecentHistoryAsync(limit: 2, offset: 1);

        Assert.Equal(2, results.Count);
        Assert.Equal(baseTime.AddMinutes(2), results[0].PlayedAt);
        Assert.Equal(baseTime.AddMinutes(1), results[1].PlayedAt);
        Assert.Equal(1, results[1].VotesCount);
    }

    [Fact]
    public async Task RankingsController_DelegatesPeriodsLimitsAndHistoryPagination()
    {
        var service = new Mock<IRankingsService>();
        service.Setup(s => s.GetTopSongsAsync("month", 5)).ReturnsAsync([]);
        service.Setup(s => s.GetTopArtistsAsync("year", 3)).ReturnsAsync([]);
        service.Setup(s => s.GetRecentHistoryAsync(7, 9)).ReturnsAsync([]);
        var controller = new RankingsController(service.Object);

        Assert.IsType<OkObjectResult>(await controller.GetTopSongs("month", 5));
        Assert.IsType<OkObjectResult>(await controller.GetTopArtists("year", 3));
        Assert.IsType<OkObjectResult>(await controller.GetRecentHistory(7, 9));

        service.Verify(s => s.GetTopSongsAsync("month", 5), Times.Once);
        service.Verify(s => s.GetTopArtistsAsync("year", 3), Times.Once);
        service.Verify(s => s.GetRecentHistoryAsync(7, 9), Times.Once);
    }
}
