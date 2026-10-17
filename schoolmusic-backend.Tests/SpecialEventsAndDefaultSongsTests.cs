using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using schoolmusic_backend.Controllers;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;
using Xunit;

namespace schoolmusic_backend.Tests;

public class SpecialEventsAndDefaultSongsTests
{
    private static schoolmusicContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<schoolmusicContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new schoolmusicContext(options);
    }

    private static (TController controller, schoolmusicContext context) CreateAdminController<TController>(
        string dbName,
        Func<schoolmusicContext, TController> factory) where TController : ControllerBase
    {
        var context = CreateInMemoryContext(dbName);
        var controller = factory(context);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Role, "admin")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        return (controller, context);
    }

    private static async Task<Song> SeedSongAsync(schoolmusicContext db, int id = 1, bool isActive = true)
    {
        var artist = new Artist
        {
            Id = id,
            Name = $"Artist {id}",
            Source = "other"
        };
        db.Artists.Add(artist);

        var song = new Song
        {
            Id = id,
            Title = $"Song {id}",
            ArtistId = artist.Id,
            Artist = artist,
            DurationMs = 180000,
            Genre = "Pop",
            Source = "other",
            SongUrl = "https://audio.schoolmusic.local/song.mp3",
            IsActive = isActive
        };
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        return song;
    }

    #region AdminSpecialEventsController Tests

    [Fact]
    public async Task SpecialEvents_Create_Get_Update_Delete_Lifecycle()
    {
        // Arrange
        var (controller, db) = CreateAdminController(Guid.NewGuid().ToString(), ctx => new AdminSpecialEventsController(ctx));

        // 1. Create Special Event
        var createDto = new CreateSpecialEventDto(
            Title: "Dzwonek na lekcję",
            FileUrl: "https://audio.schoolmusic.local/bell.mp3",
            StartsAt: new DateTime(2026, 10, 17, 8, 0, 0),
            EventStatus: "pending"
        );
        var createResult = await controller.CreateSpecialEvent(createDto);
        var created = Assert.IsType<CreatedAtActionResult>(createResult);
        var eventDto = Assert.IsType<SpecialEventDto>(created.Value);
        Assert.Equal("Dzwonek na lekcję", eventDto.Title);
        Assert.Equal("pending", eventDto.EventStatus);

        // 2. Get All & Filter by status
        var allEventsResult = await controller.GetSpecialEvents(status: "pending");
        var okAll = Assert.IsType<OkObjectResult>(allEventsResult);
        var list = Assert.IsAssignableFrom<IEnumerable<SpecialEventDto>>(okAll.Value);
        Assert.Single(list);

        // 3. Update
        var updateDto = new UpdateSpecialEventDto(
            Title: "Dzwonek na przerwę",
            FileUrl: "https://audio.schoolmusic.local/break_bell.mp3",
            StartsAt: new DateTime(2026, 10, 17, 8, 45, 0),
            EventStatus: "playing"
        );
        var updateResult = await controller.UpdateSpecialEvent(eventDto.Id, updateDto);
        var okUpdate = Assert.IsType<OkObjectResult>(updateResult);
        var updatedDto = Assert.IsType<SpecialEventDto>(okUpdate.Value);
        Assert.Equal("Dzwonek na przerwę", updatedDto.Title);
        Assert.Equal("playing", updatedDto.EventStatus);

        // 4. Delete
        var deleteResult = await controller.DeleteSpecialEvent(eventDto.Id);
        Assert.IsType<OkObjectResult>(deleteResult);
        bool existsAfterDelete = await db.SpecialEvents.AnyAsync(e => e.Id == eventDto.Id);
        Assert.False(existsAfterDelete);
    }

    [Fact]
    public async Task SpecialEvents_Create_ValidationErrors()
    {
        // Arrange
        var (controller, _) = CreateAdminController(Guid.NewGuid().ToString(), ctx => new AdminSpecialEventsController(ctx));

        // Empty FileUrl
        var emptyUrlResult = await controller.CreateSpecialEvent(new CreateSpecialEventDto(
            Title: "Test",
            FileUrl: "  ",
            StartsAt: DateTime.Now
        ));
        Assert.IsType<BadRequestObjectResult>(emptyUrlResult);

        // Invalid EventStatus
        var invalidStatusResult = await controller.CreateSpecialEvent(new CreateSpecialEventDto(
            Title: "Test",
            FileUrl: "https://test.local/sound.mp3",
            StartsAt: DateTime.Now,
            EventStatus: "invalid_status"
        ));
        Assert.IsType<BadRequestObjectResult>(invalidStatusResult);
    }

    [Fact]
    public async Task SpecialEvents_GetById_ReturnsNotFound_WhenDoesNotExist()
    {
        // Arrange
        var (controller, _) = CreateAdminController(Guid.NewGuid().ToString(), ctx => new AdminSpecialEventsController(ctx));

        // Act
        var result = await controller.GetSpecialEventById(9999);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result);
    }

    #endregion

    #region AdminDefaultSongsController Tests

    [Fact]
    public async Task DefaultSongs_Add_Get_Remove_Lifecycle()
    {
        // Arrange
        var (controller, db) = CreateAdminController(Guid.NewGuid().ToString(), ctx => new AdminDefaultSongsController(ctx));
        var song = await SeedSongAsync(db, 10, isActive: true);

        // 1. Add Default Song
        var addResult = await controller.AddDefaultSong(new AddDefaultSongDto(song.Id));
        var created = Assert.IsType<CreatedAtActionResult>(addResult);
        var defaultSongDto = Assert.IsType<DefaultSongItemDto>(created.Value);
        Assert.Equal(song.Id, defaultSongDto.SongId);

        // 2. Conflict on duplicate add
        var conflictResult = await controller.AddDefaultSong(new AddDefaultSongDto(song.Id));
        Assert.IsType<ConflictObjectResult>(conflictResult);

        // 3. Get all default songs
        var getResult = await controller.GetDefaultSongs();
        var okGet = Assert.IsType<OkObjectResult>(getResult);
        var list = Assert.IsAssignableFrom<IEnumerable<DefaultSongItemDto>>(okGet.Value);
        Assert.Single(list);

        // 4. Remove default song
        var removeResult = await controller.RemoveDefaultSong(song.Id);
        Assert.IsType<OkObjectResult>(removeResult);
        bool existsAfterRemove = await db.DefaultSongs.AnyAsync(d => d.SongId == song.Id);
        Assert.False(existsAfterRemove);
    }

    [Fact]
    public async Task DefaultSongs_Add_ReturnsNotFound_WhenSongDoesNotExist()
    {
        // Arrange
        var (controller, _) = CreateAdminController(Guid.NewGuid().ToString(), ctx => new AdminDefaultSongsController(ctx));

        // Act
        var result = await controller.AddDefaultSong(new AddDefaultSongDto(999));

        // Assert
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DefaultSongs_Add_ReturnsBadRequest_WhenSongIsInactive()
    {
        // Arrange
        var (controller, db) = CreateAdminController(Guid.NewGuid().ToString(), ctx => new AdminDefaultSongsController(ctx));
        var song = await SeedSongAsync(db, 11, isActive: false);

        // Act
        var result = await controller.AddDefaultSong(new AddDefaultSongDto(song.Id));

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    #endregion

    #region BreakSchedulerService Tests

    [Fact]
    public async Task BreakSchedulerService_ExecutesPendingSpecialEvent_AndMarksDone()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        var context = CreateInMemoryContext(dbName);

        // Dodanie zaplanowanego wydarzenia specjalnego z datą w przeszłości (już nadszedł czas)
        var specialEvent = new SpecialEvent
        {
            Id = 1,
            Title = "Dzwonek na rozpoczęcie lekcji",
            FileUrl = "https://audio.schoolmusic.local/bell.mp3",
            StartsAt = DateTime.Now.AddMinutes(-1),
            EventStatus = "pending"
        };
        context.SpecialEvents.Add(specialEvent);
        await context.SaveChangesAsync();

        var dummyStatus = new PlayerStatusDto(false, false, 100, false, "None", null, null, 0, 0);
        var mockPlayer = new Mock<IAudioPlayerService>();
        mockPlayer
            .Setup(p => p.PlayFileOrUrlAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(ServiceResult<PlayerStatusDto>.Ok(dummyStatus));

        var services = new ServiceCollection();
        services.AddScoped(_ => CreateInMemoryContext(dbName));
        services.AddScoped<IBreakService, BreakService>();
        services.AddScoped(_ => new Mock<IQueueService>().Object);
        var serviceProvider = services.BuildServiceProvider();

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(() => serviceProvider.CreateScope());

        var mockLogger = new Mock<ILogger<BreakSchedulerService>>();

        var scheduler = new BreakSchedulerService(mockScopeFactory.Object, mockPlayer.Object, mockLogger.Logger());

        // Act: wywołanie ProcessTickAsync
        await scheduler.ProcessTickAsync();

        // Assert
        using var verificationDb = CreateInMemoryContext(dbName);
        var updatedEvent = await verificationDb.SpecialEvents.FindAsync(1);
        Assert.NotNull(updatedEvent);
        Assert.Equal("done", updatedEvent.EventStatus);
        mockPlayer.Verify(p => p.PlayFileOrUrlAsync("https://audio.schoolmusic.local/bell.mp3", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task BreakSchedulerService_WhenQueueIsEmpty_UsesDefaultSongsAsFallback()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        var context = CreateInMemoryContext(dbName);

        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        // Utworzenie aktywnej przerwy w trakcie trwania
        var activeBreak = new Break
        {
            Id = 1,
            BreakDate = today,
            BreakNumber = 1,
            StartAt = now.AddMinutes(-5),
            EndsAt = now.AddMinutes(10),
            MusicEnabled = true
        };
        context.Breaks.Add(activeBreak);

        // Dodanie utworów i listy domyślnej
        var song1 = await SeedSongAsync(context, 101, isActive: true);
        var song2 = await SeedSongAsync(context, 102, isActive: true);

        context.DefaultSongs.Add(new DefaultSong { Id = 1, SongId = song1.Id, Song = song1 });
        context.DefaultSongs.Add(new DefaultSong { Id = 2, SongId = song2.Id, Song = song2 });
        await context.SaveChangesAsync();

        var dummyStatus = new PlayerStatusDto(false, false, 100, false, "None", null, null, 0, 0);
        var mockPlayer = new Mock<IAudioPlayerService>();
        mockPlayer
            .Setup(p => p.PlayFileOrUrlAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(ServiceResult<PlayerStatusDto>.Ok(dummyStatus));
        mockPlayer.Setup(p => p.GetStatus()).Returns(dummyStatus);

        var services = new ServiceCollection();
        services.AddScoped(_ => CreateInMemoryContext(dbName));
        services.AddScoped<IBreakService, BreakService>();
        var mockQueueService = new Mock<IQueueService>();
        services.AddScoped(_ => mockQueueService.Object);
        var serviceProvider = services.BuildServiceProvider();

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(() => serviceProvider.CreateScope());

        var mockLogger = new Mock<ILogger<BreakSchedulerService>>();

        var scheduler = new BreakSchedulerService(mockScopeFactory.Object, mockPlayer.Object, mockLogger.Logger());

        // Act
        await scheduler.ProcessTickAsync();

        // Assert: wygenerowano elementy w kolejce z listy DefaultSongs i zaplanowano ich odtworzenie
        using var verificationDb = CreateInMemoryContext(dbName);
        var queueItems = await verificationDb.QueueItems.Where(q => q.BreakId == activeBreak.Id).ToListAsync();
        Assert.NotEmpty(queueItems);
        Assert.Contains(queueItems, q => q.SongId == song1.Id);
        Assert.Contains(queueItems, q => q.SongId == song2.Id);

        // Plan odtwarzania w schedulerze został utworzony
        var plan = scheduler.GetCurrentPlan();
        Assert.NotNull(plan);
        Assert.NotEmpty(plan.Tracks);
    }

    #endregion
}

internal static class LoggerMockExtensions
{
    public static ILogger<T> Logger<T>(this Mock<ILogger<T>> mock) => mock.Object;
}
