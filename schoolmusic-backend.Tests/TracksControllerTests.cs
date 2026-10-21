using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using schoolmusic_backend.Controllers;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Tests;

public class TracksControllerTests
{
    private schoolmusicContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<schoolmusicContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new schoolmusicContext(options);
    }

    private (TracksController controller, schoolmusicContext context) CreateController(
        string dbName,
        int? userId = null,
        Mock<ISongSourceService>? songSource = null,
        bool spotifyConfigured = true)
    {
        var context = CreateInMemoryContext(dbName);
        var mockBreakService = new Mock<IBreakService>();
        var mockAudioPlayer = new Mock<IAudioPlayerService>();
        songSource ??= new Mock<ISongSourceService>();
        songSource.SetupGet(source => source.IsConfigured).Returns(spotifyConfigured);

        var controller = new TracksController(
            context,
            mockBreakService.Object,
            mockAudioPlayer.Object,
            songSource.Object);

        var httpContext = new DefaultHttpContext();
        if (userId.HasValue)
        {
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            httpContext.User = new ClaimsPrincipal(identity);
        }
        else
        {
            httpContext.User = new ClaimsPrincipal();
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        return (controller, context);
    }

    [Fact]
    public async Task GetTracks_ReturnsPagedTracks_WithCorrectPagination()
    {
        var (controller, context) = CreateController(nameof(GetTracks_ReturnsPagedTracks_WithCorrectPagination));

        var artist = new Artist { Id = 1, Name = "Ludwig van Beethoven", Source = "other" };
        context.Artists.Add(artist);

        for (int i = 1; i <= 25; i++)
        {
            context.Songs.Add(new Song
            {
                Id = i,
                Title = $"Symphony No. {i}",
                ArtistId = 1,
                Artist = artist,
                DurationMs = 180000,
                Genre = "Classical",
                Source = "other",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync();

        var result = await controller.GetTracks(page: 2, pageSize: 10);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TracksPagedResponseDto>(okResult.Value);

        Assert.Equal(25, response.Total);
        Assert.Equal(2, response.Page);
        Assert.Equal(10, response.PageSize);
        Assert.Equal(3, response.TotalPages);
        Assert.Equal(10, response.Items.Count);
    }

    [Fact]
    public async Task GetTracks_FiltersBySearchTerm_InTitleAndArtist()
    {
        var (controller, context) = CreateController(nameof(GetTracks_FiltersBySearchTerm_InTitleAndArtist));

        var artist1 = new Artist { Id = 1, Name = "Scott Joplin", Source = "other" };
        var artist2 = new Artist { Id = 2, Name = "Frederic Chopin", Source = "other" };
        context.Artists.AddRange(artist1, artist2);

        context.Songs.AddRange(
            new Song { Id = 1, Title = "The Entertainer", ArtistId = 1, Artist = artist1, Genre = "Ragtime", Source = "other", DurationMs = 200000, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Song { Id = 2, Title = "Maple Leaf Rag", ArtistId = 1, Artist = artist1, Genre = "Ragtime", Source = "other", DurationMs = 190000, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Song { Id = 3, Title = "Nocturne in E-flat", ArtistId = 2, Artist = artist2, Genre = "Classical", Source = "other", DurationMs = 260000, IsActive = true, CreatedAt = DateTime.UtcNow }
        );
        await context.SaveChangesAsync();

        // Wyszukiwanie po tytule
        var searchTitleResult = await controller.GetTracks(search: "entertainer");
        var okTitle = Assert.IsType<OkObjectResult>(searchTitleResult);
        var resTitle = Assert.IsType<TracksPagedResponseDto>(okTitle.Value);
        Assert.Single(resTitle.Items);
        Assert.Equal("The Entertainer", resTitle.Items[0].Title);

        // Wyszukiwanie po autorze
        var searchArtistResult = await controller.GetTracks(search: "chopin");
        var okArtist = Assert.IsType<OkObjectResult>(searchArtistResult);
        var resArtist = Assert.IsType<TracksPagedResponseDto>(okArtist.Value);
        Assert.Single(resArtist.Items);
        Assert.Equal("Nocturne in E-flat", resArtist.Items[0].Title);
    }

    [Fact]
    public async Task GetTracks_FiltersByGenre()
    {
        var (controller, context) = CreateController(nameof(GetTracks_FiltersByGenre));

        var artist = new Artist { Id = 1, Name = "Various Artists", Source = "other" };
        context.Artists.Add(artist);

        context.Songs.AddRange(
            new Song { Id = 1, Title = "Track 1", ArtistId = 1, Artist = artist, Genre = "Jazz", Source = "other", DurationMs = 150000, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Song { Id = 2, Title = "Track 2", ArtistId = 1, Artist = artist, Genre = "Lo-Fi", Source = "other", DurationMs = 160000, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Song { Id = 3, Title = "Track 3", ArtistId = 1, Artist = artist, Genre = "Jazz", Source = "other", DurationMs = 170000, IsActive = true, CreatedAt = DateTime.UtcNow }
        );
        await context.SaveChangesAsync();

        var result = await controller.GetTracks(genre: "jazz");
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TracksPagedResponseDto>(okResult.Value);

        Assert.Equal(2, response.Total);
        Assert.All(response.Items, item => Assert.Equal("Jazz", item.Genre));
    }

    [Fact]
    public async Task GetTracks_IncludesVotesCount_And_QueueStatus_ForTargetBreak()
    {
        var (controller, context) = CreateController(nameof(GetTracks_IncludesVotesCount_And_QueueStatus_ForTargetBreak));

        var artist = new Artist { Id = 1, Name = "Artist 1", Source = "other" };
        var song1 = new Song { Id = 1, Title = "Song A", ArtistId = 1, Artist = artist, Genre = "Rock", Source = "other", DurationMs = 180000, IsActive = true, CreatedAt = DateTime.UtcNow };
        var song2 = new Song { Id = 2, Title = "Song B", ArtistId = 1, Artist = artist, Genre = "Rock", Source = "other", DurationMs = 180000, IsActive = true, CreatedAt = DateTime.UtcNow };
        var brk = new Break { Id = 10, BreakDate = DateOnly.FromDateTime(DateTime.Now), BreakNumber = 1, StartAt = DateTime.Now.AddHours(1), EndsAt = DateTime.Now.AddHours(2), MusicEnabled = true };

        var qItem = new QueueItem
        {
            Id = 50,
            BreakId = 10,
            SongId = 1,
            OrderIndex = 1,
            ModerationStatus = "approved",
            Status = "queued"
        };
        var vote1 = new Vote { Id = 1, QueueItemId = 50, UserId = 101 };
        var vote2 = new Vote { Id = 2, QueueItemId = 50, UserId = 102 };

        context.Artists.Add(artist);
        context.Songs.AddRange(song1, song2);
        context.Breaks.Add(brk);
        context.QueueItems.Add(qItem);
        context.Votes.AddRange(vote1, vote2);
        await context.SaveChangesAsync();

        var result = await controller.GetTracks(breakId: 10);
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TracksPagedResponseDto>(okResult.Value);

        var itemA = response.Items.First(i => i.Id == 1);
        var itemB = response.Items.First(i => i.Id == 2);

        Assert.Equal(2, itemA.VotesCount);
        Assert.Equal(50, itemA.QueueItemId);
        Assert.Equal("queued", itemA.QueueStatus);

        Assert.Equal(0, itemB.VotesCount);
        Assert.Null(itemB.QueueItemId);
    }

    [Fact]
    public async Task GetTracks_SetsIsFavorite_WhenUserIsLoggedIn()
    {
        var (controller, context) = CreateController(nameof(GetTracks_SetsIsFavorite_WhenUserIsLoggedIn), userId: 7);

        var artist = new Artist { Id = 1, Name = "Artist 1", Source = "other" };
        var song1 = new Song { Id = 1, Title = "Fav Song", ArtistId = 1, Artist = artist, Genre = "Pop", Source = "other", DurationMs = 180000, IsActive = true, CreatedAt = DateTime.UtcNow };
        var song2 = new Song { Id = 2, Title = "Normal Song", ArtistId = 1, Artist = artist, Genre = "Pop", Source = "other", DurationMs = 180000, IsActive = true, CreatedAt = DateTime.UtcNow };
        var user = new User { Id = 7, UserLogin = "testuser", UserPassword = "hash", RankId = 1, CreatedAt = DateTime.UtcNow };

        user.Songs.Add(song1);

        context.Artists.Add(artist);
        context.Songs.AddRange(song1, song2);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var result = await controller.GetTracks();
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TracksPagedResponseDto>(okResult.Value);

        var favItem = response.Items.First(i => i.Id == 1);
        var nonFavItem = response.Items.First(i => i.Id == 2);

        Assert.True(favItem.IsFavorite);
        Assert.False(nonFavItem.IsFavorite);
    }

    [Fact]
    public async Task GetGenres_ReturnsDistinctGenresList()
    {
        var (controller, context) = CreateController(nameof(GetGenres_ReturnsDistinctGenresList));

        var artist = new Artist { Id = 1, Name = "Artist", Source = "other" };
        context.Artists.Add(artist);

        context.Songs.AddRange(
            new Song { Id = 1, Title = "S1", ArtistId = 1, Artist = artist, Genre = "Jazz", Source = "other", DurationMs = 100, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Song { Id = 2, Title = "S2", ArtistId = 1, Artist = artist, Genre = "Classical", Source = "other", DurationMs = 100, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Song { Id = 3, Title = "S3", ArtistId = 1, Artist = artist, Genre = "Jazz", Source = "other", DurationMs = 100, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Song { Id = 4, Title = "S4", ArtistId = 1, Artist = artist, Genre = null, Source = "other", DurationMs = 100, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Song { Id = 5, Title = "S5", ArtistId = 1, Artist = artist, Genre = "Ambient", Source = "other", DurationMs = 100, IsActive = false, CreatedAt = DateTime.UtcNow } // nieaktywny
        );
        await context.SaveChangesAsync();

        var result = await controller.GetGenres();
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<GenresListResponseDto>(okResult.Value);

        Assert.Equal(2, response.Total);
        Assert.Contains("Classical", response.Genres);
        Assert.Contains("Jazz", response.Genres);
        Assert.DoesNotContain("Ambient", response.Genres); // bo nieaktywny
    }

    [Fact]
    public async Task GetTrackById_WhenSongExists_ReturnsTrackDetails()
    {
        var (controller, context) = CreateController(nameof(GetTrackById_WhenSongExists_ReturnsTrackDetails));

        var artist = new Artist { Id = 1, Name = "Frederic Chopin", Source = "other" };
        var song = new Song { Id = 42, Title = "Polonaise in A major", ArtistId = 1, Artist = artist, Genre = "Classical", Source = "other", DurationMs = 300000, IsActive = true, CreatedAt = DateTime.UtcNow };

        context.Artists.Add(artist);
        context.Songs.Add(song);
        await context.SaveChangesAsync();

        var result = await controller.GetTrackById(42);
        var okResult = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<TrackListItemDto>(okResult.Value);

        Assert.Equal(42, dto.Id);
        Assert.Equal("Polonaise in A major", dto.Title);
        Assert.Equal("Frederic Chopin", dto.ArtistName);
    }

    [Fact]
    public async Task GetTrackById_WhenSongDoesNotExist_ReturnsNotFound()
    {
        var (controller, context) = CreateController(nameof(GetTrackById_WhenSongDoesNotExist_ReturnsNotFound));

        var result = await controller.GetTrackById(999);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task SearchTracks_ReturnsSpotifySearchResults()
    {
        var songSource = new Mock<ISongSourceService>();
        songSource.SetupGet(source => source.IsConfigured).Returns(true);
        songSource.Setup(source => source.SearchTracksAsync("test", 20))
            .ReturnsAsync(new[]
            {
                new ExternalTrackDto("spotify-id", "Song", "Artist", "cover.jpg", 123000, null, "spotify")
            });
        var (controller, _) = CreateController(nameof(SearchTracks_ReturnsSpotifySearchResults), songSource: songSource);

        var result = Assert.IsType<OkObjectResult>(await controller.SearchTracks(" test "));
        var track = Assert.Single(Assert.IsAssignableFrom<IEnumerable<object>>(result.Value));
        Assert.Equal("spotify-id", track.GetType().GetProperty("externalId")?.GetValue(track));
        Assert.Equal("Song", track.GetType().GetProperty("title")?.GetValue(track));
        Assert.Equal("Artist", track.GetType().GetProperty("artist")?.GetValue(track));
    }

    [Fact]
    public async Task SearchTracks_WhenSpotifyIsNotConfigured_ReturnsServiceUnavailable()
    {
        var songSource = new Mock<ISongSourceService>();
        songSource.SetupGet(source => source.IsConfigured).Returns(false);
        var (controller, _) = CreateController(
            nameof(SearchTracks_WhenSpotifyIsNotConfigured_ReturnsServiceUnavailable),
            songSource: songSource,
            spotifyConfigured: false);

        var result = Assert.IsType<ObjectResult>(await controller.SearchTracks("test"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Contains("Spotify:ClientId", result.Value?.GetType().GetProperty("Message")?.GetValue(result.Value)?.ToString());
        songSource.Verify(source => source.SearchTracksAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task AddToFavouritesSpotifyId_ImportsMissingTrackAndArtist()
    {
        var spotifyTrack = new ExternalTrackDto("spotify-id", "New Song", "New Artist", "cover.jpg", 123000, null, "spotify");
        var songSource = new Mock<ISongSourceService>();
        songSource.SetupGet(source => source.IsConfigured).Returns(true);
        songSource.Setup(source => source.GetTrackByIdAsync("spotify-id")).ReturnsAsync(spotifyTrack);
        var (controller, context) = CreateController(nameof(AddToFavouritesSpotifyId_ImportsMissingTrackAndArtist), userId: 7, songSource: songSource);
        context.Users.Add(new User
        {
            Id = 7,
            UserLogin = "testuser",
            UserPassword = "hash",
            RankId = 1,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var result = await controller.AddToFavouritesSpotifyId("spotify-id");

        Assert.IsType<OkObjectResult>(result);
        var song = await context.Songs.Include(candidate => candidate.Artist).SingleAsync();
        Assert.Equal("spotify-id", song.SpotifyId);
        Assert.Equal("New Song", song.Title);
        Assert.Equal("New Artist", song.Artist.Name);
        Assert.Equal("cover.jpg", song.Cover);
        Assert.Equal(123000, song.DurationMs);
        Assert.Single(await context.Artists.ToListAsync());
        Assert.Contains(song, (await context.Users.SingleAsync()).Songs);
    }

    [Fact]
    public async Task AddToFavouritesSpotifyId_WhenSpotifyIsNotConfigured_ReturnsServiceUnavailable()
    {
        var songSource = new Mock<ISongSourceService>();
        songSource.SetupGet(source => source.IsConfigured).Returns(false);
        var (controller, context) = CreateController(
            nameof(AddToFavouritesSpotifyId_WhenSpotifyIsNotConfigured_ReturnsServiceUnavailable),
            userId: 7,
            songSource: songSource,
            spotifyConfigured: false);
        context.Users.Add(new User
        {
            Id = 7,
            UserLogin = "testuser",
            UserPassword = "hash",
            RankId = 1,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var result = Assert.IsType<ObjectResult>(await controller.AddToFavouritesSpotifyId("spotify-id"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }
}
