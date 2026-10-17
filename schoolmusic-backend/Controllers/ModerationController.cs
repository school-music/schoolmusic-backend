using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using schoolmusic_backend.Models;
using StackExchange.Redis;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Hubs;

namespace schoolmusic_backend.Controllers
{
    /// <summary>
    /// Kontroler API obsługujący zadania moderacyjne: przeglądanie i zarządzanie propozycjami piosenek, zatwierdzanie i
    /// odrzucanie propozycji oraz banowanie i odbanowywanie użytkowników
    /// </summary>
    /// <remarks>Dostęp tylko dla ról 'mod', 'admin' i 'head_admin'</remarks>
    [Route("/api/[controller]")]
    [ApiController]
    [Authorize(Roles ="mod,admin,head_admin")]
    public class ModerationController : Controller
    {
        private readonly schoolmusicContext _context;
        private readonly IConnectionMultiplexer _redis;
        private readonly IHubContext<QueueHub> _hubContext;

        public ModerationController(schoolmusicContext context, IConnectionMultiplexer redis, IHubContext<QueueHub> hubContext)
        {
            _context = context;
            _redis = redis;
            _hubContext = hubContext;
        }
        /// <summary>
        /// Reprezentuje propozycję utworu przesłaną przez użytkownika.
        /// </summary>
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


        /// <summary>
        /// Zatwierdza propozycję piosenki o podanym identyfikatorze, dodaje ją do zielonej listy i kolejki oraz
        /// powiadamia podłączonych klientów przez SignalR
        /// </summary>
        /// <returns>Zwraca IActionResult: 401 Unauthorized gdy brak autoryzacji moderatora; 404 NotFound gdy nie znaleziono
        /// propozycji lub powiązanej piosenki; 200 Ok zawierające informacje o zatwierdzeniu, identyfikatorze elementu
        /// kolejki i identyfikatorze przerwy</returns>
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
                    SongsBanned = 0,
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

            moderator.SongsApproved += 1;
            await _context.SaveChangesAsync();

            // Powiadamiamy wszystkich podłączonych do tej przerwy o nowej zatwierdzonej piosence
            // poprzez SignalR i aktualizujemy kolejkę w czasie rzeczywistym
            var items = await _context.QueueItems
                .Where(q => q.BreakId == breakId && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
                .Include(q => q.Song)
                    .ThenInclude(s => s.Artist)
                .Include(q => q.User)
                .Include(q => q.Votes)
                .ToListAsync();

            var ranking = items
                .OrderByDescending(q => q.Votes.Count)
                .ThenBy(q => q.OrderIndex)
                .Select((q, index) => new
                {
                    position = index + 1,
                    queueItemId = q.Id,
                    songId = q.SongId,
                    title = q.Song.Title,
                    artistName = q.Song.Artist?.Name ?? "Nieznany wykonawca",
                    cover = q.Song.Cover,
                    durationMs = q.Song.DurationMs,
                    votesCount = q.Votes.Count,
                    submittedBy = q.User.UserLogin,
                    orderIndex = q.OrderIndex
                })
                .ToList();

            await _hubContext.Clients.Group($"Break_{breakId}").SendAsync("QueueUpdated", new
            {
                breakId,
                totalSongs = ranking.Count,
                queue = ranking
            });

            return Ok(new
            {
                Message = "Piosenka została zatwierdzona, dodana do kolejki oraz wpisana na zieloną listę.",
                queueItemId = queueItem.Id,
                songTitle = song.Title,
                breakId = breakId
            });
        }

        /// <summary>
        /// Odrzuca propozycję utworu o podanym identyfikatorze; opcjonalnie dodaje utwór do czarnej listy
        /// </summary>
        /// <returns>Zwraca IActionResult z odpowiednim kodem HTTP i obiektem zawierającym komunikat, identyfikator propozycji,
        /// tytuł utworu oraz flagę wskazującą, czy utwór został dodany do czarnej listy.</returns>
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
                        SongsBanned = 0,
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
                        moderator.SongsBanned += 1;
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
        /// Znajduje wszystkich zbanowanych użytkowników
        /// </summary>
        /// <returns>Zwraca IActionResult z odpowiednią wiadomością oraz 
        /// listą zbanowanych użytkowników </returns>
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

        /// <summary>
        /// Banuje użytkownika o podanym identyfikatorze, tworząc wpis w tabeli BannedUsers i zapisując zmiany
        /// </summary>
        /// <returns>IActionResult: 200 OK gdy ban został dodany; 409 Conflict gdy użytkownik już jest zbanowany; 401
        /// Unauthorized gdy brak prawidłowego identyfikatora moderatora</returns>
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

        /// <summary>
        /// Odbanowuje użytkownika powiązanego z podanym identyfikatorem zakazu
        /// </summary>
        /// <returns>IActionResult zwracający 200 (Ok) z komunikatem po pomyślnym odbanowaniu; 400 (BadRequest) gdy użytkownik
        /// nie jest zbanowany; 401 (Unauthorized) gdy brak lub nieprawidłowy identyfikator moderatora</returns>
        [HttpPost("unaban/{banId:int}")]
        public async Task<IActionResult> UnbanUser(int banId)
        {
            var bannedUser = await _context.BannedUsers
                    .FirstOrDefaultAsync(u => u.Id == banId);
            var modId = HttpContext.GetCurrentUserId();
            if(modId == null)
            {
                return Unauthorized(new
                {
                    Message = "Nieprawidłowy lub brakujący identyfikator"
                });
            }
            if (bannedUser != null)
            {
                _context.BannedUsers.Remove(bannedUser);
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
