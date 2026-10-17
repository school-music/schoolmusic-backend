using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using schoolmusic_backend.Controllers;
using schoolmusic_backend.Hubs;
using schoolmusic_backend.Models;
using StackExchange.Redis;

namespace schoolmusic_backend.Tests;

public class ModerationAndUserManagementControllerTests
{
    private static schoolmusicContext CreateContext() => new(
        new DbContextOptionsBuilder<schoolmusicContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void SetUser(ControllerBase controller, int? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()), new Claim(ClaimTypes.Role, "head_admin") }
            : [];
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
            }
        };
    }

    private static (ModerationController Controller, Mock<IDatabase> Database, Mock<IClientProxy> Client) CreateModerationController(schoolmusicContext db, params string[] pending)
    {
        var database = new Mock<IDatabase>();
        database.Setup(redis => redis.ListRangeAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(pending.Select(value => (RedisValue)value).ToArray());
        database.Setup(redis => redis.ListRemoveAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(1);
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(connection => connection.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);

        var client = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(client.Object);
        var hub = new Mock<IHubContext<QueueHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        return (new ModerationController(db, redis.Object, hub.Object), database, client);
    }

    private static string Proposal(string id = "proposal-1", int songId = 1, int breakId = 2, int userId = 7) =>
        JsonSerializer.Serialize(new ModerationController.SongProposalDto(
            id, songId, "spotify-id", "Test song", "Test artist", "cover", 180000,
            breakId, 1, userId, "listener", DateTime.UtcNow));

    private static async Task<(Artist Artist, Song Song)> SeedSong(schoolmusicContext db, int songId = 1)
    {
        var artist = new Artist { Id = 1, Name = "Test artist", Source = "other" };
        var song = new Song
        {
            Id = songId, Title = "Test song", ArtistId = artist.Id, Artist = artist,
            Source = "other", DurationMs = 180000, IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Artists.Add(artist);
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        return (artist, song);
    }

    [Fact]
    public async Task GetAllPendingSongs_DeserializesPendingRedisEntries()
    {
        await using var db = CreateContext();
        var (controller, _, _) = CreateModerationController(db, Proposal());
        var result = Assert.IsType<OkObjectResult>(await controller.GetAllPendingSongs());
        var songs = (IEnumerable<ModerationController.SongProposalDto>)result.Value!.GetType().GetProperty("Songs")!.GetValue(result.Value)!;

        Assert.Single(songs);
        Assert.Equal("proposal-1", songs.Single().Id);
        Assert.Equal("Test song", songs.Single().Title);
    }

    [Fact]
    public async Task ApproveSong_MovesPendingProposalIntoQueueAndBroadcastsUpdate()
    {
        await using var db = CreateContext();
        var (_, song) = await SeedSong(db);
        db.Users.Add(new User { Id = 7, UserLogin = "listener", UserPassword = "hash", RankId = 1, CreatedAt = DateTime.UtcNow });
        var brk = new Break { Id = 2, BreakDate = DateOnly.FromDateTime(DateTime.Today), BreakNumber = 1, StartAt = DateTime.Today, EndsAt = DateTime.Today.AddHours(1) };
        db.Breaks.Add(brk);
        db.QueueItems.Add(new QueueItem
        {
            Id = 10, BreakId = brk.Id, Break = brk, SongId = song.Id, Song = song,
            UserId = 7, Status = "queued", ModerationStatus = null, OrderIndex = 1, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var (controller, database, client) = CreateModerationController(db, Proposal());
        SetUser(controller, 99);

        var result = Assert.IsType<OkObjectResult>(await controller.ApproveSong("proposal-1"));

        Assert.Equal(1, await db.SongGreenlists.CountAsync());
        var queued = Assert.Single(db.QueueItems);
        Assert.Equal(song.Id, queued.SongId);
        Assert.Equal("approved", queued.ModerationStatus);
        Assert.Single(db.Votes);
        Assert.Equal(1, (await db.Moderators.SingleAsync()).SongsApproved);
        database.Verify(redis => redis.ListRemoveAsync("proposals:pending", It.IsAny<RedisValue>(), 1, CommandFlags.None), Times.Once);
        client.Verify(proxy => proxy.SendCoreAsync("QueueUpdated", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task ApproveSong_ReturnsUnauthorizedWithoutModeratorIdentity()
    {
        await using var db = CreateContext();
        var (controller, _, _) = CreateModerationController(db, Proposal());
        SetUser(controller, null);

        Assert.IsType<UnauthorizedObjectResult>(await controller.ApproveSong("proposal-1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DenySong_RemovesProposal_AndOnlyBlacklistsWhenRequested(bool blacklist)
    {
        await using var db = CreateContext();
        var (artist, song) = await SeedSong(db);
        db.Moderators.Add(new Moderator { Id = 1, UserId = 99, SongsApproved = 0, SongsBanned = 0, ArtistsBanned = 0 });
        await db.SaveChangesAsync();
        var (controller, database, _) = CreateModerationController(db, Proposal());
        SetUser(controller, 99);

        Assert.IsType<OkObjectResult>(await controller.DenySong("proposal-1", blacklist));

        database.Verify(redis => redis.ListRemoveAsync("proposals:pending", It.IsAny<RedisValue>(), 1, CommandFlags.None), Times.Once);
        Assert.Equal(blacklist ? 1 : 0, await db.SongBlacklists.CountAsync());
        Assert.Equal(blacklist ? 1 : 0, (await db.Moderators.SingleAsync()).SongsBanned);
        if (blacklist)
        {
            var entry = await db.SongBlacklists.SingleAsync();
            Assert.Equal(song.Id, entry.SongId);
            Assert.Equal(artist.Id, entry.ArtistId);
        }
    }

    [Fact]
    public async Task BanUser_RejectsDuplicateAndUnbanUserRemovesExistingBan()
    {
        await using var db = CreateContext();
        var (controller, _, _) = CreateModerationController(db);
        SetUser(controller, 99);

        Assert.IsType<OkObjectResult>(await controller.BanUser(7));
        Assert.IsType<ConflictObjectResult>(await controller.BanUser(7));
        var ban = await db.BannedUsers.SingleAsync();

        Assert.IsType<OkObjectResult>(await controller.UnbanUser(ban.Id));
        Assert.Empty(db.BannedUsers);
        Assert.IsType<BadRequestObjectResult>(await controller.UnbanUser(ban.Id));
    }

    [Fact]
    public async Task BanAndUnbanUser_RequireModeratorIdentity()
    {
        await using var db = CreateContext();
        var (controller, _, _) = CreateModerationController(db);
        SetUser(controller, null);

        Assert.IsType<UnauthorizedObjectResult>(await controller.BanUser(7));
        Assert.IsType<UnauthorizedObjectResult>(await controller.UnbanUser(1));
    }

    [Fact]
    public async Task GetUsers_FiltersAndPaginatesWithoutExposingPasswords()
    {
        await using var db = CreateContext();
        var rank = new Rank { Id = 1, Rank1 = "user" };
        db.Ranks.Add(rank);
        db.Users.AddRange(Enumerable.Range(1, 5).Select(id => new User
        {
            Id = id, UserLogin = id % 2 == 0 ? $"match-{id}" : $"other-{id}",
            UserPassword = "secret", RankId = 1, Rank = rank, CreatedAt = DateTime.UtcNow
        }));
        await db.SaveChangesAsync();
        var controller = new UserManagementController(db);

        var result = Assert.IsType<OkObjectResult>(await controller.GetUsers(page: 2, pageSize: 1, search: "match"));
        var response = result.Value!;
        Assert.Equal(2, response.GetType().GetProperty("TotalCount")!.GetValue(response));
        Assert.Equal(2, response.GetType().GetProperty("TotalPages")!.GetValue(response));
        var users = Assert.IsAssignableFrom<IEnumerable<UserListItemDto>>(response.GetType().GetProperty("Users")!.GetValue(response));
        var user = Assert.Single(users);
        Assert.Equal("match-4", user.UserLogin);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(user));
    }

    [Fact]
    public async Task UpdateUserRole_UpdatesModeratorTableWhenPromotingAndDemoting()
    {
        await using var db = CreateContext();
        var userRank = new Rank { Id = 1, Rank1 = "user" };
        var modRank = new Rank { Id = 2, Rank1 = "mod" };
        db.Ranks.AddRange(userRank, modRank);
        db.Users.Add(new User { Id = 2, UserLogin = "target", UserPassword = "hash", RankId = 1, Rank = userRank, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var controller = new UserManagementController(db);
        SetUser(controller, 99);

        Assert.IsType<OkObjectResult>(await controller.UpdateUserRole(2, new UpdateUserRoleDto(2)));
        Assert.Equal(2, (await db.Users.SingleAsync()).RankId);
        Assert.Equal(2, (await db.Moderators.SingleAsync()).UserId);

        Assert.IsType<OkObjectResult>(await controller.UpdateUserRole(2, new UpdateUserRoleDto(1)));
        Assert.Equal(1, (await db.Users.SingleAsync()).RankId);
        Assert.Empty(db.Moderators);
    }

    [Fact]
    public async Task UpdateUserRole_RejectsSelfAndInvalidRank()
    {
        await using var db = CreateContext();
        var controller = new UserManagementController(db);
        SetUser(controller, 99);

        Assert.IsType<BadRequestObjectResult>(await controller.UpdateUserRole(99, new UpdateUserRoleDto(2)));
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateUserRole(2, new UpdateUserRoleDto(5)));
    }
}
