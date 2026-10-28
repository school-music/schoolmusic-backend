using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using schoolmusic_backend.Models;
using StackExchange.Redis;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Hubs;
using schoolmusic_backend.Services;

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
        private readonly IQueueMutationLock _mutationLock;
        private readonly ILogger<ModerationController>? _logger;
        private readonly TimeProvider _timeProvider;
        private static readonly IQueueMutationLock DefaultMutationLock = new QueueMutationLock();

        public ModerationController(
            schoolmusicContext context,
            IConnectionMultiplexer redis,
            IHubContext<QueueHub> hubContext,
            IQueueMutationLock? mutationLock = null,
            ILogger<ModerationController>? logger = null,
            TimeProvider? timeProvider = null)
        {
            _context = context;
            _redis = redis;
            _hubContext = hubContext;
            _mutationLock = mutationLock ?? DefaultMutationLock;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }
        /// <summary>
        /// Reprezentuje propozycję utworu przesłaną przez użytkownika.
        /// </summary>
        public record SongProposalDto(
            string Id,
            int SongId,
            string? SpotifyId = null,
            string? Title = null,
            string? Artist = null,
            string? Cover = null,
            int DurationMs = 0,
            int BreakId = 1,
            int? BreakNumber = null,
            int UserId = 0,
            string? SubmittedBy = null,
            DateTime? SubmittedAt = null
        );

        public record BlacklistedSongItemDto(
            int Id,
            int SongId,
            string Title,
            int ArtistId,
            string ArtistName,
            string? Cover,
            string? Genre,
            int DurationMs,
            int ModId,
            string? ModeratorLogin
        );

        public record BlacklistPagedResponseDto(
            int Total,
            int Page,
            int PageSize,
            int TotalPages,
            List<BlacklistedSongItemDto> Items
        );

        public record BlacklistedArtistItemDto(
            int Id,
            int ArtistId,
            string ArtistName,
            int ModeratorId,
            string? ModeratorLogin
        );

        public record BlacklistedArtistsPagedResponseDto(
            int Total,
            int Page,
            int PageSize,
            int TotalPages,
            List<BlacklistedArtistItemDto> Items
        );

        public record GreenlistedSongItemDto(
            int Id,
            int SongId,
            string Title,
            int ArtistId,
            string ArtistName,
            string? Cover,
            string? Genre,
            int DurationMs,
            int ModId,
            string? ModeratorLogin
        );

        public record GreenlistPagedResponseDto(
            int Total,
            int Page,
            int PageSize,
            int TotalPages,
            List<GreenlistedSongItemDto> Items
        );

        public record AddSongToBlacklistDto(int? SongId, int? ArtistId);
        public record AddSongToGreenlistDto(int SongId);
        private sealed record ApprovedProposalResult(QueueItem QueueItem, Song Song);

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
            string pendingKey = PendingProposalStore.ListKey;

            RedisValue[] pendingSongs = await db.ListRangeAsync(pendingKey);

            List<SongProposalDto> proposals = new();
            foreach (var rv in pendingSongs)
            {
                if (!rv.HasValue) continue;
                try
                {
                    var dto = JsonSerializer.Deserialize<SongProposalDto>(rv.ToString(), _jsonOptions);
                    if (dto != null)
                    {
                        proposals.Add(dto);
                    }
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    // Ignoruj wpisy o niezgodnym formacie
                    _logger?.LogWarning("Pominięto nieprawidłowy wpis w kolejce moderacji Redis.");
                }
            }

            return Ok(new
            {
                Message = "Wszystkie propozycje piosenek oczekujące na zatwierdzenie",
                Songs = proposals,
                Proposals = proposals
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
            var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                           ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!int.TryParse(userIdClaim, out int modUserId))
            {
                return Unauthorized(new { Message = "Brak autoryzacji moderatora." });
            }

            var db = _redis.GetDatabase();
            string pendingKey = PendingProposalStore.ListKey;
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
                    catch (Exception ex) when (ex is JsonException or NotSupportedException)
                    {
                        _logger?.LogWarning("Pominięto nieprawidłowy wpis w kolejce moderacji Redis.");
                    }
                }
            }

            if (matchingProposal == null)
            {
                return NotFound(new { Message = "Nie znaleziono propozycji o podanym identyfikatorze." });
            }

            await using var mutationLock = await _mutationLock.AcquireAsync(matchingProposal.BreakId);
            var stillPending = (await db.ListRangeAsync(pendingKey)).Any(value => value == matchingRedisValue);
            if (!stillPending)
                return NotFound(new { Message = "Propozycja została już obsłużona przez innego moderatora." });

            var approval = await ApproveProposalInDatabaseAsync(matchingProposal, modUserId);
            if (!approval.Success || approval.Data == null)
                return StatusCode(approval.StatusCode, new { Message = approval.Message });

            var queueItem = approval.Data.QueueItem;
            var song = approval.Data.Song;
            int breakId = matchingProposal.BreakId;

            await db.ListRemoveAsync(pendingKey, matchingRedisValue, 1);
            await db.KeyDeleteAsync(PendingProposalStore.GetDeduplicationKey(breakId, song.Id));
            _logger?.LogInformation(
                "Moderator {ModeratorUserId} zatwierdził propozycję {ProposalId} jako pozycję {QueueItemId} przerwy {BreakId}.",
                modUserId,
                proposalId,
                queueItem.Id,
                breakId);

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
                    submittedBy = q.User?.UserLogin ?? "Uczeń",
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
        [HttpPost("reject/{proposalId}")]
        public async Task<IActionResult> DenySong(string proposalId, [FromQuery] bool addToBlacklist = false)
        {
            var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                           ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!int.TryParse(userIdClaim, out int modUserId))
            {
                return Unauthorized(new { Message = "Brak autoryzacji moderatora." });
            }

            var db = _redis.GetDatabase();
            string pendingKey = PendingProposalStore.ListKey;
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
                    catch (Exception ex) when (ex is JsonException or NotSupportedException)
                    {
                        _logger?.LogWarning("Pominięto nieprawidłowy wpis w kolejce moderacji Redis.");
                    }
                }
            }

            if (matchingProposal == null)
            {
                return NotFound(new { Message = "Nie znaleziono propozycji o podanym identyfikatorze." });
            }

            await using var mutationLock = await _mutationLock.AcquireAsync(matchingProposal.BreakId);
            var stillPending = (await db.ListRangeAsync(pendingKey)).Any(value => value == matchingRedisValue);
            if (!stillPending)
                return NotFound(new { Message = "Propozycja została już obsłużona przez innego moderatora." });

            await db.ListRemoveAsync(pendingKey, matchingRedisValue, 1);
            await db.KeyDeleteAsync(PendingProposalStore.GetDeduplicationKey(
                matchingProposal.BreakId,
                matchingProposal.SongId));
            _logger?.LogInformation(
                "Moderator {ModeratorUserId} odrzucił propozycję {ProposalId} dla przerwy {BreakId}.",
                modUserId,
                proposalId,
                matchingProposal.BreakId);

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

        private async Task<Moderator?> GetOrCreateModeratorAsync(int userId)
        {
            var moderator = await _context.Moderators.FirstOrDefaultAsync(m => m.UserId == userId);
            if (moderator == null)
            {
                moderator = new Moderator
                {
                    UserId = userId,
                    SongsApproved = 0,
                    SongsBanned = 0,
                    ArtistsBanned = 0
                };
                _context.Moderators.Add(moderator);
                await _context.SaveChangesAsync();
            }
            return moderator;
        }

        /// <summary>
        /// Zwraca stronicowaną listę utworów znajdujących się na czarnej liście z opcjonalnym wyszukiwaniem po tytule lub wykonawcy.
        /// </summary>
        [HttpGet("blacklist")]
        public async Task<IActionResult> GetBlacklistedSongs(
            [FromQuery] string? search = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var query = _context.SongBlacklists
                .Include(b => b.Song)
                    .ThenInclude(s => s.Artist)
                .Include(b => b.Mod)
                    .ThenInclude(m => m.User)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var trimmed = search.Trim();
                query = query.Where(b => b.Song.Title.Contains(trimmed) || (b.Song.Artist != null && b.Song.Artist.Name.Contains(trimmed)));
            }

            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;
            if (pageSize > 100) pageSize = 100;

            int total = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(total / (double)pageSize);

            var items = await query
                .OrderByDescending(b => b.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(b => new BlacklistedSongItemDto(
                    b.Id,
                    b.SongId,
                    b.Song.Title,
                    b.ArtistId,
                    b.Song.Artist != null ? b.Song.Artist.Name : (b.Artist != null ? b.Artist.Name : "Nieznany wykonawca"),
                    b.Song.Cover,
                    b.Song.Genre,
                    b.Song.DurationMs,
                    b.ModId,
                    b.Mod != null && b.Mod.User != null ? b.Mod.User.UserLogin : null
                ))
                .ToListAsync();

            return Ok(new BlacklistPagedResponseDto(
                Total: total,
                Page: page,
                PageSize: pageSize,
                TotalPages: totalPages,
                Items: items
            ));
        }

        /// <summary>
        /// Dodaje utwór lub wykonawcę do czarnej listy. W przypadku dodania utworu, jest on automatycznie usuwany z zielonej listy.
        /// </summary>
        [HttpPost("blacklist")]
        public async Task<IActionResult> AddToBlacklist([FromBody] AddSongToBlacklistDto dto)
        {
            var modUserId = HttpContext.GetCurrentUserId();
            if (modUserId == null)
            {
                return Unauthorized(new { Message = "Brak autoryzacji moderatora." });
            }

            var moderator = await GetOrCreateModeratorAsync(modUserId.Value);
            if (moderator == null)
            {
                return Unauthorized(new { Message = "Nie znaleziono profilu moderatora." });
            }

            if (dto.SongId.HasValue)
            {
                int songId = dto.SongId.Value;
                var song = await _context.Songs.Include(s => s.Artist).FirstOrDefaultAsync(s => s.Id == songId);
                if (song == null)
                {
                    return NotFound(new { Message = "Utwór o podanym ID nie istnieje." });
                }

                bool alreadyBlacklisted = await _context.SongBlacklists.AnyAsync(b => b.SongId == song.Id);
                if (alreadyBlacklisted)
                {
                    return Conflict(new { Message = "Ten utwór znajduje się już na czarnej liście." });
                }

                var greenlistEntry = await _context.SongGreenlists.FirstOrDefaultAsync(g => g.SongId == song.Id);
                if (greenlistEntry != null)
                {
                    _context.SongGreenlists.Remove(greenlistEntry);
                }

                var blacklistEntry = new SongBlacklist
                {
                    SongId = song.Id,
                    ArtistId = song.ArtistId,
                    ModId = moderator.Id
                };
                _context.SongBlacklists.Add(blacklistEntry);
                moderator.SongsBanned += 1;
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    Message = $"Pomyślnie dodano utwór '{song.Title}' do czarnej listy.",
                    BlacklistId = blacklistEntry.Id,
                    SongId = song.Id,
                    RemovedFromGreenlist = greenlistEntry != null
                });
            }

            if (dto.ArtistId.HasValue)
            {
                int artistId = dto.ArtistId.Value;
                var artist = await _context.Artists.FindAsync(artistId);
                if (artist == null)
                {
                    return NotFound(new { Message = "Wykonawca o podanym ID nie istnieje." });
                }

                bool alreadyBlacklisted = await _context.ArtistBlacklists.AnyAsync(a => a.ArtistId == artistId);
                if (alreadyBlacklisted)
                {
                    return Conflict(new { Message = "Ten wykonawca znajduje się już na czarnej liście." });
                }

                var artistEntry = new ArtistBlacklist
                {
                    ArtistId = artistId,
                    ModeratorId = moderator.Id
                };
                _context.ArtistBlacklists.Add(artistEntry);
                moderator.ArtistsBanned += 1;
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    Message = $"Pomyślnie dodano wykonawcę '{artist.Name}' do czarnej listy.",
                    BlacklistId = artistEntry.Id,
                    ArtistId = artistId
                });
            }

            return BadRequest(new { Message = "Musisz podać SongId lub ArtistId." });
        }

        /// <summary>
        /// Usuwa utwór z czarnej listy po identyfikatorze wpisu lub identyfikatorze utworu.
        /// </summary>
        [HttpDelete("blacklist/{id:int}")]
        public async Task<IActionResult> RemoveFromBlacklist(int id)
        {
            var entry = await _context.SongBlacklists
                .Include(b => b.Song)
                .FirstOrDefaultAsync(b => b.Id == id || b.SongId == id);

            if (entry == null)
            {
                return NotFound(new { Message = "Wpis na czarnej liście o podanym identyfikatorze nie został znaleziony." });
            }

            _context.SongBlacklists.Remove(entry);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                Message = $"Pomyślnie usunięto utwór '{entry.Song?.Title ?? id.ToString()}' z czarnej listy.",
                BlacklistId = entry.Id,
                SongId = entry.SongId
            });
        }

        /// <summary>
        /// Zwraca stronicowaną listę zablokowanych wykonawców (ArtistBlacklist).
        /// </summary>
        [HttpGet("blacklist/artists")]
        public async Task<IActionResult> GetBlacklistedArtists(
            [FromQuery] string? search = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var query = _context.ArtistBlacklists
                .Include(a => a.Artist)
                .Include(a => a.Moderator)
                    .ThenInclude(m => m.User)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var trimmed = search.Trim();
                query = query.Where(a => a.Artist.Name.Contains(trimmed));
            }

            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;
            if (pageSize > 100) pageSize = 100;

            int total = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(total / (double)pageSize);

            var items = await query
                .OrderByDescending(a => a.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(a => new BlacklistedArtistItemDto(
                    a.Id,
                    a.ArtistId,
                    a.Artist.Name,
                    a.ModeratorId,
                    a.Moderator != null && a.Moderator.User != null ? a.Moderator.User.UserLogin : null
                ))
                .ToListAsync();

            return Ok(new BlacklistedArtistsPagedResponseDto(
                Total: total,
                Page: page,
                PageSize: pageSize,
                TotalPages: totalPages,
                Items: items
            ));
        }

        /// <summary>
        /// Dodaje wykonawcę o podanym ID do czarnej listy wykonawców.
        /// </summary>
        [HttpPost("blacklist/artist/{artistId:int}")]
        public async Task<IActionResult> AddArtistToBlacklist(int artistId)
        {
            var modUserId = HttpContext.GetCurrentUserId();
            if (modUserId == null)
            {
                return Unauthorized(new { Message = "Brak autoryzacji moderatora." });
            }

            var artist = await _context.Artists.FindAsync(artistId);
            if (artist == null)
            {
                return NotFound(new { Message = "Wykonawca o podanym ID nie istnieje." });
            }

            bool already = await _context.ArtistBlacklists.AnyAsync(a => a.ArtistId == artistId);
            if (already)
            {
                return Conflict(new { Message = "Ten wykonawca znajduje się już na czarnej liście." });
            }

            var moderator = await GetOrCreateModeratorAsync(modUserId.Value);
            if (moderator == null)
            {
                return Unauthorized(new { Message = "Nie znaleziono profilu moderatora." });
            }

            var entry = new ArtistBlacklist
            {
                ArtistId = artistId,
                ModeratorId = moderator.Id
            };
            _context.ArtistBlacklists.Add(entry);
            moderator.ArtistsBanned += 1;
            await _context.SaveChangesAsync();

            return Ok(new
            {
                Message = $"Pomyślnie dodano wykonawcę '{artist.Name}' do czarnej listy.",
                BlacklistId = entry.Id,
                ArtistId = artistId
            });
        }

        /// <summary>
        /// Usuwa wykonawcę z czarnej listy po identyfikatorze wykonawcy lub identyfikatorze wpisu.
        /// </summary>
        [HttpDelete("blacklist/artist/{artistId:int}")]
        public async Task<IActionResult> RemoveArtistFromBlacklist(int artistId)
        {
            var entry = await _context.ArtistBlacklists
                .Include(a => a.Artist)
                .FirstOrDefaultAsync(a => a.ArtistId == artistId || a.Id == artistId);

            if (entry == null)
            {
                return NotFound(new { Message = "Wykonawca o podanym ID nie znajduje się na czarnej liście." });
            }

            _context.ArtistBlacklists.Remove(entry);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                Message = $"Pomyślnie usunięto wykonawcę '{entry.Artist?.Name ?? artistId.ToString()}' z czarnej listy.",
                BlacklistId = entry.Id,
                ArtistId = entry.ArtistId
            });
        }

        /// <summary>
        /// Zwraca stronicowaną listę utworów zatwierdzonych (zielona lista) z opcjonalnym wyszukiwaniem po tytule lub wykonawcy.
        /// </summary>
        [HttpGet("greenlist")]
        public async Task<IActionResult> GetGreenlistedSongs(
            [FromQuery] string? search = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var query = _context.SongGreenlists
                .Include(g => g.Song)
                    .ThenInclude(s => s.Artist)
                .Include(g => g.Mod)
                    .ThenInclude(m => m.User)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var trimmed = search.Trim();
                query = query.Where(g => g.Song.Title.Contains(trimmed) || (g.Song.Artist != null && g.Song.Artist.Name.Contains(trimmed)));
            }

            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;
            if (pageSize > 100) pageSize = 100;

            int total = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(total / (double)pageSize);

            var items = await query
                .OrderByDescending(g => g.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(g => new GreenlistedSongItemDto(
                    g.Id,
                    g.SongId,
                    g.Song.Title,
                    g.ArtistId,
                    g.Song.Artist != null ? g.Song.Artist.Name : (g.Artist != null ? g.Artist.Name : "Nieznany wykonawca"),
                    g.Song.Cover,
                    g.Song.Genre,
                    g.Song.DurationMs,
                    g.ModId,
                    g.Mod != null && g.Mod.User != null ? g.Mod.User.UserLogin : null
                ))
                .ToListAsync();

            return Ok(new GreenlistPagedResponseDto(
                Total: total,
                Page: page,
                PageSize: pageSize,
                TotalPages: totalPages,
                Items: items
            ));
        }

        /// <summary>
        /// Bezpośrednio dodaje utwór do zielonej listy (wstępne zatwierdzenie przez moderatora/administratora).
        /// </summary>
        [HttpPost("greenlist")]
        public async Task<IActionResult> AddSongToGreenlist([FromBody] AddSongToGreenlistDto dto)
        {
            var modUserId = HttpContext.GetCurrentUserId();
            if (modUserId == null)
            {
                return Unauthorized(new { Message = "Brak autoryzacji moderatora." });
            }

            var song = await _context.Songs.Include(s => s.Artist).FirstOrDefaultAsync(s => s.Id == dto.SongId);
            if (song == null)
            {
                return NotFound(new { Message = "Utwór o podanym ID nie istnieje." });
            }

            bool isBlacklisted = await _context.SongBlacklists.AnyAsync(b => b.SongId == song.Id);
            if (isBlacklisted)
            {
                return BadRequest(new { Message = "Nie można dodać utworu do zielonej listy, ponieważ znajduje się na czarnej liście." });
            }

            bool alreadyGreenlisted = await _context.SongGreenlists.AnyAsync(g => g.SongId == song.Id);
            if (alreadyGreenlisted)
            {
                return Conflict(new { Message = "Ten utwór znajduje się już na zielonej liście." });
            }

            var moderator = await GetOrCreateModeratorAsync(modUserId.Value);
            if (moderator == null)
            {
                return Unauthorized(new { Message = "Nie znaleziono profilu moderatora." });
            }

            var entry = new SongGreenlist
            {
                SongId = song.Id,
                ArtistId = song.ArtistId,
                ModId = moderator.Id
            };
            _context.SongGreenlists.Add(entry);
            moderator.SongsApproved += 1;
            await _context.SaveChangesAsync();

            return Ok(new
            {
                Message = $"Pomyślnie dodano utwór '{song.Title}' do zielonej listy.",
                GreenlistId = entry.Id,
                SongId = song.Id
            });
        }

        /// <summary>
        /// Usuwa utwór z zielonej listy po identyfikatorze utworu lub identyfikatorze wpisu.
        /// </summary>
        [HttpDelete("greenlist/{songId:int}")]
        public async Task<IActionResult> RemoveFromGreenlist(int songId)
        {
            var entry = await _context.SongGreenlists
                .Include(g => g.Song)
                .FirstOrDefaultAsync(g => g.SongId == songId || g.Id == songId);

            if (entry == null)
            {
                return NotFound(new { Message = "Utwór o podanym identyfikatorze nie znajduje się na zielonej liście." });
            }

            _context.SongGreenlists.Remove(entry);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                Message = $"Pomyślnie usunięto utwór '{entry.Song?.Title ?? songId.ToString()}' z zielonej listy.",
                GreenlistId = entry.Id,
                SongId = entry.SongId
            });
        }

        private async Task<ServiceResult<ApprovedProposalResult>> ApproveProposalInDatabaseAsync(
            SongProposalDto proposal,
            int modUserId)
        {
            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync()
                : null;

            try
            {
                if (!await _context.Breaks.AnyAsync(item => item.Id == proposal.BreakId))
                    return ServiceResult<ApprovedProposalResult>.NotFound("Przerwa powiązana z propozycją nie istnieje.");

                var moderator = await _context.Moderators.FirstOrDefaultAsync(item => item.UserId == modUserId);
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

                var song = await _context.Songs
                    .Include(item => item.Artist)
                    .FirstOrDefaultAsync(item => item.Id == proposal.SongId);
                if (song == null)
                    return ServiceResult<ApprovedProposalResult>.NotFound("Piosenka powiązana z propozycją nie istnieje w bazie danych.");

                var greenlistEntry = await _context.SongGreenlists.FirstOrDefaultAsync(item => item.SongId == song.Id);
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

                var queueItem = await _context.QueueItems
                    .FirstOrDefaultAsync(item => item.BreakId == proposal.BreakId && item.SongId == song.Id);
                if (queueItem == null)
                {
                    int currentMaxOrder = await _context.QueueItems
                        .Where(item => item.BreakId == proposal.BreakId)
                        .MaxAsync(item => (int?)item.OrderIndex) ?? 0;
                    queueItem = new QueueItem
                    {
                        BreakId = proposal.BreakId,
                        SongId = song.Id,
                        UserId = proposal.UserId,
                        GreenlistId = greenlistEntry.Id,
                        ModeratorId = moderator.Id,
                        ModerationStatus = "approved",
                        Status = QueueItemStatuses.Queued,
                        CreatedAt = DateTime.UtcNow,
                        OrderIndex = currentMaxOrder + 1
                    };
                    _context.QueueItems.Add(queueItem);
                    await _context.SaveChangesAsync();
                }
                else
                {
                    if (queueItem.Status != QueueItemStatuses.Queued)
                        return ServiceResult<ApprovedProposalResult>.Conflict("Pozycja kolejki została już rozpoczęta lub zakończona.");
                    queueItem.ModerationStatus = "approved";
                    queueItem.ModeratorId = moderator.Id;
                    queueItem.GreenlistId = greenlistEntry.Id;
                }

                bool alreadyVoted = await _context.Votes
                    .AnyAsync(item => item.UserId == proposal.UserId && item.QueueItemId == queueItem.Id);
                if (!alreadyVoted)
                {
                    _context.Votes.Add(new Vote
                    {
                        UserId = proposal.UserId,
                        QueueItemId = queueItem.Id,
                        VotedAt = _timeProvider.GetUtcNow().UtcDateTime
                    });
                }

                moderator.SongsApproved += 1;
                await _context.SaveChangesAsync();
                if (transaction != null)
                    await transaction.CommitAsync();

                return ServiceResult<ApprovedProposalResult>.Ok(new ApprovedProposalResult(queueItem, song));
            }
            catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation())
            {
                if (transaction != null)
                    await transaction.RollbackAsync();
                return ServiceResult<ApprovedProposalResult>.Conflict(
                    "Propozycja została równocześnie obsłużona lub kolejka uległa zmianie. Odśwież dane i spróbuj ponownie.");
            }
        }
    }
}
