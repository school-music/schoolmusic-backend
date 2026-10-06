using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using schoolmusic_backend.Models;
using StackExchange.Redis;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using schoolmusic_backend.Extensions;

namespace schoolmusic_backend.Controllers
{
    [Route("/api/[controller]")]
    [ApiController]
    [Authorize(Roles ="mod,admin,head_admin")]
    public class ModerationController : Controller
    {
        private readonly schoolmusicContext _context;
        private readonly IConnectionMultiplexer _redis;
        public ModerationController(schoolmusicContext context, IConnectionMultiplexer redis)
        {
            _context = context;
            _redis = redis;
        }
        public record SongProposalDto(
            string Id,
            int SongId,
            string SpotifyId,
            string Title,
            string Artist,
            string Cover,
            int DurationMs,
            int BreakId,
            int? BreakNumber,
            int UserId,
            string SubmittedBy,
            DateTime SubmittedAt
        );

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Zwraca wszystkie propozycje piosenek czekających na 
        /// zatwierdzenie przez moderacje
        /// Dostęp tylko dla administratorów
        /// </summary>
        [HttpGet("pending")]
        public async Task<IActionResult> GetAllPendingSongs()
        {
            var db = _redis.GetDatabase();
            string pendingKey = "proposals:pending";

            RedisValue[] pendingSongs = await db.ListRangeAsync(pendingKey);

            List<SongProposalDto> proposals = pendingSongs
                .Where(rv => rv.HasValue) // element is not null
                .Select(rv => JsonSerializer.Deserialize<SongProposalDto>(rv.ToString(), _jsonOptions))
                .Where(dto => dto != null) // ignore empty deserialization results
                .ToList()!;

            return Ok(new
            {
                Message = "Wszystkie propozycje piosenek oczekujące na zatwierdzenie",
                Songs = proposals,
            });
        }



        [HttpPost("approve/{proposalId}")]
        public async Task<IActionResult> ApproveSong(string proposalId)
        {
            var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(userIdClaim, out int modUserId))
            {
                return Unauthorized(new { Message = "Brak autoryzacji moderatora." });
            }

            var db = _redis.GetDatabase();
            string pendingKey = "proposals:pending";
            RedisValue[] pendingSongs = await db.ListRangeAsync(pendingKey);

            SongProposalDto? matchingProposal = null;
            RedisValue matchingRedisValue = RedisValue.Null;

            foreach (var rv in pendingSongs)
            {
                if (rv.HasValue)
                {
                    try
                    {
                        var dto = JsonSerializer.Deserialize<SongProposalDto>(rv.ToString(), _jsonOptions);
                        if (dto != null && string.Equals(dto.Id, proposalId, StringComparison.OrdinalIgnoreCase))
                        {
                            matchingProposal = dto;
                            matchingRedisValue = rv;
                            break;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (matchingProposal == null)
            {
                return NotFound(new { Message = "Nie znaleziono propozycji o podanym identyfikatorze." });
            }

            var moderator = await _context.Moderators.FirstOrDefaultAsync(m => m.UserId == modUserId);
            if (moderator == null)
            {
                moderator = new Moderator
                {
                    UserId = modUserId,
                    SongsApproved = 0,
                    SongsBaned = 0,
                    ArtistsBanned = 0
                };
                _context.Moderators.Add(moderator);
                await _context.SaveChangesAsync();
            }

            int songId = matchingProposal.SongId;
            int breakId = matchingProposal.BreakId;
            int proposingUserId = matchingProposal.UserId;

            var song = await _context.Songs.Include(s => s.Artist).FirstOrDefaultAsync(s => s.Id == songId);
            if (song == null)
            {
                return NotFound(new { Message = "Piosenka powiązana z propozycją nie istnieje w bazie danych." });
            }

            var greenlistEntry = await _context.SongGreenlists.FirstOrDefaultAsync(g => g.SongId == song.Id);
            if (greenlistEntry == null)
            {
                greenlistEntry = new SongGreenlist
                {
                    SongId = song.Id,
                    ArtistId = song.ArtistId,
                    ModId = moderator.Id
                };
                _context.SongGreenlists.Add(greenlistEntry);
                await _context.SaveChangesAsync();
            }

            int currentMaxOrder = await _context.QueueItems
                .Where(q => q.BreakId == breakId)
                .MaxAsync(q => (int?)q.OrderIndex) ?? 0;

            var existingQueueItem = await _context.QueueItems
                .FirstOrDefaultAsync(q => q.BreakId == breakId && q.SongId == song.Id);

            QueueItem queueItem;
            if (existingQueueItem == null)
            {
                queueItem = new QueueItem
                {
                    BreakId = breakId,
                    SongId = song.Id,
                    UserId = proposingUserId,
                    GreenlistId = greenlistEntry.Id,
                    ModeratorId = moderator.Id,
                    ModerationStatus = "approved",
                    OrderIndex = currentMaxOrder + 1
                };
                _context.QueueItems.Add(queueItem);
                await _context.SaveChangesAsync();
            }
            else
            {
                queueItem = existingQueueItem;
                queueItem.ModerationStatus = "approved";
                queueItem.ModeratorId = moderator.Id;
                queueItem.GreenlistId = greenlistEntry.Id;
                await _context.SaveChangesAsync();
            }

            bool alreadyVoted = await _context.Votes
                .AnyAsync(v => v.UserId == proposingUserId && v.QueueItemId == queueItem.Id);

            if (!alreadyVoted)
            {
                _context.Votes.Add(new Vote
                {
                    UserId = proposingUserId,
                    QueueItemId = queueItem.Id
                });
                await _context.SaveChangesAsync();
            }

            await db.ListRemoveAsync(pendingKey, matchingRedisValue, 1);

            moderator.SongsApproved = (moderator.SongsApproved ?? 0) + 1;
            await _context.SaveChangesAsync();

            return Ok(new
            {
                Message = "Piosenka została zatwierdzona, dodana do kolejki oraz wpisana na zieloną listę.",
                queueItemId = queueItem.Id,
                songTitle = song.Title,
                breakId = breakId
            });
        }

        [HttpPost("deny/{proposalId}")]
        public async Task<IActionResult> DenySong(string proposalId, [FromQuery] bool addToBlacklist = false)
        {
            var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(userIdClaim, out int modUserId))
            {
                return Unauthorized(new { Message = "Brak autoryzacji moderatora." });
            }

            var db = _redis.GetDatabase();
            string pendingKey = "proposals:pending";
            RedisValue[] pendingSongs = await db.ListRangeAsync(pendingKey);

            SongProposalDto? matchingProposal = null;
            RedisValue matchingRedisValue = RedisValue.Null;

            foreach (var rv in pendingSongs)
            {
                if (rv.HasValue)
                {
                    try
                    {
                        var dto = JsonSerializer.Deserialize<SongProposalDto>(rv.ToString(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (dto != null && string.Equals(dto.Id, proposalId, StringComparison.OrdinalIgnoreCase))
                        {
                            matchingProposal = dto;
                            matchingRedisValue = rv;
                            break;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (matchingProposal == null)
            {
                return NotFound(new { Message = "Nie znaleziono propozycji o podanym identyfikatorze." });
            }

            await db.ListRemoveAsync(pendingKey, matchingRedisValue, 1);

            bool blacklisted = false;
            if (addToBlacklist)
            {
                var moderator = await _context.Moderators.FirstOrDefaultAsync(m => m.UserId == modUserId);
                if (moderator == null)
                {
                    moderator = new Moderator
                    {
                        UserId = modUserId,
                        SongsApproved = 0,
                        SongsBaned = 0,
                        ArtistsBanned = 0
                    };
                    _context.Moderators.Add(moderator);
                    await _context.SaveChangesAsync();
                }

                int songId = matchingProposal.SongId;
                var song = await _context.Songs.FindAsync(songId);
                if (song != null)
                {
                    bool isAlreadyBlacklisted = await _context.SongBlacklists.AnyAsync(sb => sb.SongId == song.Id);
                    if (!isAlreadyBlacklisted)
                    {
                        _context.SongBlacklists.Add(new SongBlacklist
                        {
                            SongId = song.Id,
                            ArtistId = song.ArtistId,
                            ModId = moderator.Id
                        });
                        moderator.SongsBaned = (moderator.SongsBaned ?? 0) + 1;
                        await _context.SaveChangesAsync();
                        blacklisted = true;
                    }
                }
            }

            return Ok(new
            {
                Message = blacklisted
                    ? "Propozycja została odrzucona i dodana do czarnej listy."
                    : "Propozycja została odrzucona i usunięta z kolejki oczekujących.",
                proposalId = proposalId,
                songTitle = matchingProposal.Title,
                addedToBlacklist = blacklisted
            });
        }

        /// <summary>
        /// Zwraca wszystkich uczniów z tabeli BannedUsers
        /// </summary>
        [HttpGet("banned")]
        public async Task<IActionResult> GetAllBannedUsers()
        {
            var bannedUsers = await _context.BannedUsers
                .Include(u => u.User)
                .Select(u => new
                {
                    u.Id,
                    u.UserId,
                    Username = u.User.UserLogin,
                    u.Reason,
                    u.BannedAt
                })
                .ToListAsync();

            return Ok(new
            {
                Message = "Wszyscy zbanowani użytkownicy",
                BannedUsers = bannedUsers
            });
        }

        [HttpPost("ban/{userId:int}")]
        public async Task<IActionResult> BanUser(int userId)
        {
            bool isBanned = await _context.BannedUsers
                  .AnyAsync(u => u.UserId == userId);
            var modId = HttpContext.GetCurrentUserId();
            if(modId == null)
            {
                return Unauthorized(new
                {
                    Message = "Nieprawidłowy lub brakujący identyfikator"
                });
            }
            if(!isBanned)
            {
                _context.BannedUsers.Add(new BannedUser
                {
                    UserId = userId,
                    ModeratorId = modId.Value,
                    BannedAt = DateTime.UtcNow

                });
                await _context.SaveChangesAsync();
                return Ok(new
                {
                    Message = $"Poprawnie zbanowano użytkownka o id {userId}",
                });
            }
            return Conflict(new
            {
                Message = "Ten użytkownik został już zbanowany"
            });
        }

        [HttpPost("unaban/{banId:int}")]
        public async Task<IActionResult> UnbanUser(int banId)
        {
            bool isBanned = await _context.BannedUsers
                    .AnyAsync(u => u.Id == banId);
            var modId = HttpContext.GetCurrentUserId();
            if(modId == null)
            {
                return Unauthorized(new
                {
                    Message = "Nieprawidłowy lub brakujący identyfikator"
                });
            }
            if (!isBanned)
            {
                await _context.BannedUsers.Where(u => u.Id == banId)
                    .ExecuteDeleteAsync();
                await _context.SaveChangesAsync();
                return Ok(new
                {
                    Message = "Pomyślnie odbanowano użytkownika"
                });
            }
            return BadRequest(new
            {
                Message = "Podany użytkownik nie jest zbanowany"
            });
        }
    }
}
