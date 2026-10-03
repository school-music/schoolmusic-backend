using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Models;
using schoolmusic_backend.Extensions;
using StackExchange.Redis;

namespace schoolmusic_backend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class QueueController : ControllerBase
    {
        private readonly schoolmusicContext _context;
        private readonly IConnectionMultiplexer _redis;

        public QueueController(schoolmusicContext context, IConnectionMultiplexer redis)
        {
            _context = context;
            _redis = redis;
        }

        public record ProposeSongDto(int? SongId, string? SpotifyId, int? BreakId);
        public record VoteRequestDto(int QueueItemId);

        /// <summary>
        /// Pobiera listę utworów zgłoszonych na daną przerwę, 
        /// posortowaną malejąco według liczby głosów (Live ranking).
        /// </summary>
        [HttpGet("break/{breakId:int}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetQueueForBreak(int breakId)
        {
            var breakEntity = await _context.Breaks.FindAsync(breakId);
            if (breakEntity == null)
            {
                return NotFound(new { Message = "Nie znaleziono przerwy o podanym identyfikatorze." });
            }

            int? currentUserId = HttpContext.GetCurrentUserId();

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
                    hasVoted = currentUserId.HasValue && q.Votes.Any(v => v.UserId == currentUserId.Value),
                    submittedBy = q.User.UserLogin,
                    orderIndex = q.OrderIndex
                })
                .ToList();

            return Ok(new
            {
                breakId = breakEntity.Id,
                breakNumber = breakEntity.BreakNumber,
                totalSongs = ranking.Count,
                queue = ranking
            });
        }

        /// <summary>
        /// Zwraca utwór aktualnie odtwarzany na radiowęźle (dla ekranu TV i paska odtwarzania w aplikacji).
        /// </summary>
        [HttpGet("now-playing")]
        [AllowAnonymous]
        public async Task<IActionResult> GetNowPlaying()
        {
            var now = DateTime.Now;

            // exception day
            var activeExceptionDay = await _context.ExceptionDays
                .Where(e => e.StartAt <= now && (e.EndsAt == null || e.EndsAt.Value >= now))
                .FirstOrDefaultAsync();

            if (activeExceptionDay != null)
            {
                return Ok(new
                {
                    isPlaying = false,
                    isMuted = true,
                    reason = activeExceptionDay.Description,
                    message = $"Radiowęzeł wyłączony: {activeExceptionDay.Description}.",
                    currentTrack = (object?)null,
                    breakInfo = (object?)null
                });
            }

            // exception break
            var activeExceptionBreak = await _context.ExceptionBreaks
                .Where(eb => eb.StartsAt <= now && (eb.EndsAt == null || eb.EndsAt.Value >= now))
                .FirstOrDefaultAsync();

            // all breaks for today
            var today = DateOnly.FromDateTime(now);
            var allBreaks = await _context.Breaks.OrderBy(b => b.BreakNumber ?? b.Id).ToListAsync();

            var todayBreaks = allBreaks.Select(b => new
            {
                Entity = b,
                StartAt = today.ToDateTime(TimeOnly.FromDateTime(b.StartAt)),
                EndsAt = b.EndsAt.HasValue
                    ? today.ToDateTime(TimeOnly.FromDateTime(b.EndsAt.Value))
                    : today.ToDateTime(TimeOnly.FromDateTime(b.StartAt))
            }).ToList();

            var currentBreak = todayBreaks.FirstOrDefault(b => now >= b.StartAt && now <= b.EndsAt);
            var nextBreak = todayBreaks.Where(b => b.StartAt > now).OrderBy(b => b.StartAt).FirstOrDefault();

            // break
            if (currentBreak != null)
            {
                if (activeExceptionBreak != null)
                {
                    return Ok(new
                    {
                        isPlaying = false,
                        isMuted = true,
                        reason = activeExceptionBreak.Description,
                        message = $"Trwa przerwa nr {currentBreak.Entity.BreakNumber}, ale muzyka jest wyciszona ({activeExceptionBreak.Description}).",
                        currentTrack = (object?)null,
                        breakInfo = new
                        {
                            breakId = currentBreak.Entity.Id,
                            breakNumber = currentBreak.Entity.BreakNumber,
                            endsAt = currentBreak.EndsAt,
                            secondsRemaining = (int)(currentBreak.EndsAt - now).TotalSeconds
                        }
                    });
                }

                var topTrack = await _context.QueueItems
                    .Where(q => q.BreakId == currentBreak.Entity.Id && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
                    .Include(q => q.Song)
                        .ThenInclude(s => s.Artist)
                    .Include(q => q.User)
                    .Include(q => q.Votes)
                    .OrderByDescending(q => q.Votes.Count)
                    .ThenBy(q => q.OrderIndex)
                    .FirstOrDefaultAsync();

                return Ok(new
                {
                    isPlaying = topTrack != null,
                    isMuted = false,
                    message = topTrack != null
                        ? $"Trwa odtwarzanie na przerwie nr {currentBreak.Entity.BreakNumber}"
                        : "Trwa przerwa, ale kolejka utworów jest pusta.",
                    currentTrack = topTrack == null ? null : new
                    {
                        queueItemId = topTrack.Id,
                        songId = topTrack.SongId,
                        title = topTrack.Song.Title,
                        artistName = topTrack.Song.Artist?.Name ?? "Nieznany wykonawca",
                        cover = topTrack.Song.Cover,
                        durationMs = topTrack.Song.DurationMs,
                        votesCount = topTrack.Votes.Count,
                        submittedBy = topTrack.User.UserLogin
                    },
                    breakInfo = new
                    {
                        breakId = currentBreak.Entity.Id,
                        breakNumber = currentBreak.Entity.BreakNumber,
                        endsAt = currentBreak.EndsAt,
                        secondsRemaining = (int)(currentBreak.EndsAt - now).TotalSeconds
                    }
                });
            }

            // lesson is active
            object? upcomingLeadTrack = null;
            if (nextBreak != null)
            {
                var leadingTrack = await _context.QueueItems
                    .Where(q => q.BreakId == nextBreak.Entity.Id && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
                    .Include(q => q.Song)
                        .ThenInclude(s => s.Artist)
                    .Include(q => q.User)
                    .Include(q => q.Votes)
                    .OrderByDescending(q => q.Votes.Count)
                    .ThenBy(q => q.OrderIndex)
                    .FirstOrDefaultAsync();

                if (leadingTrack != null)
                {
                    upcomingLeadTrack = new
                    {
                        queueItemId = leadingTrack.Id,
                        songId = leadingTrack.SongId,
                        title = leadingTrack.Song.Title,
                        artistName = leadingTrack.Song.Artist?.Name ?? "Nieznany wykonawca",
                        cover = leadingTrack.Song.Cover,
                        durationMs = leadingTrack.Song.DurationMs,
                        votesCount = leadingTrack.Votes.Count
                    };
                }
            }

            return Ok(new
            {
                isPlaying = false,
                isMuted = false,
                message = nextBreak != null
                    ? $"Trwają lekcje. Kolejna przerwa (nr {nextBreak.Entity.BreakNumber}) rozpocznie się o {nextBreak.StartAt:HH:mm}."
                    : "Wszystkie przerwy na dziś dobiegły końca.",
                currentTrack = (object?)null,
                breakInfo = nextBreak == null ? null : new
                {
                    nextBreakId = nextBreak.Entity.Id,
                    nextBreakNumber = nextBreak.Entity.BreakNumber,
                    startsAt = nextBreak.StartAt,
                    startsInMinutes = (int)Math.Max(0, (nextBreak.StartAt - now).TotalMinutes),
                    leadingSong = upcomingLeadTrack
                }
            });
        }

        /// <summary>
        /// Zgłasza piosenkę przez zalogowanego ucznia na najbliższą (lub wybraną) przerwę.
        /// </summary>
        [HttpPost("propose")]
        public async Task<IActionResult> ProposeSong([FromBody] ProposeSongDto dto)
        {
            int? currentUserId = HttpContext.GetCurrentUserId();
            if (currentUserId == null)
            {
                return Unauthorized(new { Message = "Musisz być zalogowany, aby zgłosić piosenkę." });
            }

            // is user banned
            bool isBanned = await _context.BannedUsers.AnyAsync(b => b.UserId == currentUserId.Value);
            if (isBanned)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    Message = "Twoje konto zostało zablokowane i nie ma uprawnień do zgłaszania utworów."
                });
            }

            // find the song by SongId or SpotifyId
            Song? song = null;
            if (dto.SongId.HasValue)
            {
                song = await _context.Songs.Include(s => s.Artist).FirstOrDefaultAsync(s => s.Id == dto.SongId.Value);
            }
            else if (!string.IsNullOrWhiteSpace(dto.SpotifyId))
            {
                song = await _context.Songs.Include(s => s.Artist).FirstOrDefaultAsync(s => s.SpotifyId == dto.SpotifyId);
            }

            if (song == null)
            {
                return NotFound(new { Message = "Nie znaleziono utworu w bazie danych." });
            }

            // blackilst
            bool isSongBlacklisted = await _context.SongBlacklists.AnyAsync(sb => sb.SongId == song.Id);
            bool isArtistBlacklisted = await _context.ArtistBlacklists.AnyAsync(ab => ab.ArtistId == song.ArtistId);

            if (isSongBlacklisted || isArtistBlacklisted)
            {
                return BadRequest(new
                {
                    Message = "Ten utwór lub wykonawca znajduje się na czarnej liście i nie może być odtwarzany."
                });
            }
            
            // target break
            int targetBreakId;
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);

            if (dto.BreakId.HasValue)
            {
                var selectedBreak = await _context.Breaks.FindAsync(dto.BreakId.Value);
                if (selectedBreak == null)
                {
                    return NotFound(new { Message = "Wybrana przerwa nie istnieje." });
                }
                targetBreakId = selectedBreak.Id;
            }
            else
            {
                // choose the next available break from now
                var allBreaks = await _context.Breaks.OrderBy(b => b.BreakNumber ?? b.Id).ToListAsync();
                var candidate = allBreaks.FirstOrDefault(b =>
                {
                    var voteEndTime = b.VoteEnds.HasValue
                        ? today.ToDateTime(TimeOnly.FromDateTime(b.VoteEnds.Value))
                        : today.ToDateTime(TimeOnly.FromDateTime(b.StartAt));
                    return voteEndTime > now;
                });

                if (candidate == null)
                {
                    candidate = allBreaks.FirstOrDefault();
                }

                if (candidate == null)
                {
                    return BadRequest(new { Message = "Brak zdefiniowanych przerw w harmonogramie." });
                }

                targetBreakId = candidate.Id;
            }

            var targetBreakEntity = await _context.Breaks.FindAsync(targetBreakId);
            if (targetBreakEntity == null)
            {
                return BadRequest(new { Message = "Nieprawidłowa przerwa docelowa." });
            }

            // check if voting has ended
            var targetVoteEndTime = targetBreakEntity.VoteEnds.HasValue
                ? today.ToDateTime(TimeOnly.FromDateTime(targetBreakEntity.VoteEnds.Value))
                : today.ToDateTime(TimeOnly.FromDateTime(targetBreakEntity.StartAt));

            if (dto.BreakId.HasValue && targetVoteEndTime < now)
            {
                return BadRequest(new { Message = "Głosowanie na tę przerwę zostało już zamknięte." });
            }

            // greenlist check
            var greenlistEntry = await _context.SongGreenlists.FirstOrDefaultAsync(g => g.SongId == song.Id);

            // if the song is on greenlist, automatic add
            if (greenlistEntry != null)
            {
                // checking if the song is alredy in gueue
                bool alreadyQueued = await _context.QueueItems
                    .AnyAsync(q => q.BreakId == targetBreakId && q.SongId == song.Id);

                if (alreadyQueued)
                {
                    return Conflict(new
                    {
                        Status = "conflict",
                        Message = "Ten utwór został już zgłoszony na tę przerwę!"
                    });
                }

                int currentMaxOrder = await _context.QueueItems
                    .Where(q => q.BreakId == targetBreakId)
                    .MaxAsync(q => (int?)q.OrderIndex) ?? 0;

                var queueItem = new QueueItem
                {
                    BreakId = targetBreakId,
                    SongId = song.Id,
                    UserId = currentUserId.Value,
                    GreenlistId = greenlistEntry.Id,
                    ModerationStatus = "approved",
                    OrderIndex = currentMaxOrder + 1
                };

                _context.QueueItems.Add(queueItem);
                await _context.SaveChangesAsync();

                // first vote by the proposing user
                var initialVote = new Vote
                {
                    UserId = currentUserId.Value,
                    QueueItemId = queueItem.Id
                };
                _context.Votes.Add(initialVote);
                await _context.SaveChangesAsync();

                return CreatedAtAction(nameof(GetQueueForBreak), new { breakId = targetBreakId }, new
                {
                    Status = "approved",
                    Message = "Utwór znajduje się na zielonej liście i został automatycznie zatwierdzony do kolejki!",
                    queueItemId = queueItem.Id,
                    breakId = targetBreakId,
                    breakNumber = targetBreakEntity.BreakNumber,
                    song = new
                    {
                        songId = song.Id,
                        title = song.Title,
                        artist = song.Artist?.Name ?? "Nieznany wykonawca",
                        cover = song.Cover,
                        durationMs = song.DurationMs
                    },
                    votesCount = 1
                });
            }

            // song is not on greenlist add to redis so mods can add it or remove it
            var db = _redis.GetDatabase();
            var pendingKey = "proposals:pending";

            // Sprawdzamy czy ten utwór już nie oczekuje w kolejce moderacji na tę samą przerwę
            var existingPending = await db.ListRangeAsync(pendingKey);
            foreach (var item in existingPending)
            {
                if (item.HasValue && item.ToString().Contains($"\"songId\":{song.Id}") && item.ToString().Contains($"\"breakId\":{targetBreakId}"))
                {
                    return Conflict(new
                    {
                        Status = "pending",
                        Message = "Ten utwór już oczekuje na weryfikację przez moderatora na tę przerwę."
                    });
                }
            }

            var userEntity = await _context.Users.FindAsync(currentUserId.Value);

            var proposal = new
            {
                id = Guid.NewGuid().ToString(),
                songId = song.Id,
                spotifyId = song.SpotifyId,
                title = song.Title,
                artist = song.Artist?.Name ?? "Nieznany wykonawca",
                cover = song.Cover,
                durationMs = song.DurationMs,
                breakId = targetBreakId,
                breakNumber = targetBreakEntity.BreakNumber,
                userId = currentUserId.Value,
                submittedBy = userEntity?.UserLogin ?? "Uczeń",
                submittedAt = DateTime.UtcNow
            };

            await db.ListRightPushAsync(pendingKey, System.Text.Json.JsonSerializer.Serialize(proposal));

            return Accepted(new
            {
                Status = "pending",
                Message = "Piosenka została przesłana do weryfikacji przez moderatora. Po zatwierdzeniu pojawi się w rankingu.",
                proposalId = proposal.id,
                breakId = targetBreakId,
                breakNumber = targetBreakEntity.BreakNumber,
                song = new
                {
                    songId = song.Id,
                    title = song.Title,
                    artist = song.Artist?.Name ?? "Nieznany wykonawca",
                    cover = song.Cover,
                    durationMs = song.DurationMs
                }
            });
        }

        /// <summary>
        /// Oddaje głos na utwór w kolejce. Waliduje blokadę 
        /// podwójnego głosu (jeden głos użytkownika na utwór).
        /// </summary>
        [HttpPost("vote")]
        public async Task<IActionResult> Vote([FromBody] VoteRequestDto dto)
        {
            int? currentUserId = HttpContext.GetCurrentUserId();
            if (currentUserId == null)
            {
                return Unauthorized(new { Message = "Musisz być zalogowany, aby oddać głos." });
            }

            // 1. Sprawdzenie blokady użytkownika
            bool isBanned = await _context.BannedUsers.AnyAsync(b => b.UserId == currentUserId.Value);
            if (isBanned)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    Message = "Twoje konto zostało zablokowane i nie ma uprawnień do głosowania."
                });
            }

            // checking if the queue item exists
            var queueItem = await _context.QueueItems
                .Include(q => q.Break)
                .FirstOrDefaultAsync(q => q.Id == dto.QueueItemId);

            if (queueItem == null)
            {
                return NotFound(new { Message = "Nie znaleziono pozycji w kolejce o podanym identyfikatorze." });
            }

            // checking if voting is still open for this break
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            var voteEndTime = queueItem.Break.VoteEnds.HasValue
                ? today.ToDateTime(TimeOnly.FromDateTime(queueItem.Break.VoteEnds.Value))
                : today.ToDateTime(TimeOnly.FromDateTime(queueItem.Break.StartAt));

            if (voteEndTime < now)
            {
                return BadRequest(new { Message = "Głosowanie na tę przerwę dobiegło już końca." });
            }

            // block double voite
            bool alreadyVoted = await _context.Votes
                .AnyAsync(v => v.UserId == currentUserId.Value && v.QueueItemId == dto.QueueItemId);

            if (alreadyVoted)
            {
                return Conflict(new
                {
                    Message = "Już oddałeś głos na ten utwór! Nie możesz zagłosować dwukrotnie."
                });
            }

            // save vote to mysql
            var vote = new Vote
            {
                UserId = currentUserId.Value,
                QueueItemId = dto.QueueItemId
            };

            _context.Votes.Add(vote);
            await _context.SaveChangesAsync();

            // counting total votes for this queue item
            int totalVotes = await _context.Votes.CountAsync(v => v.QueueItemId == dto.QueueItemId);

            return Ok(new
            {
                Message = "Głos został pomyślnie oddany!",
                queueItemId = dto.QueueItemId,
                votesCount = totalVotes,
                hasVoted = true
            });
        }

        /// <summary>
        /// Cofa oddany wcześniej głos na utwór w kolejce.
        /// </summary>
        [HttpDelete("vote/{queueItemId:int}")]
        public async Task<IActionResult> RemoveVote(int queueItemId)
        {
            int? currentUserId = HttpContext.GetCurrentUserId();
            if (currentUserId == null)
            {
                return Unauthorized(new { Message = "Musisz być zalogowany, aby cofnąć głos." });
            }

            // check if the vote actually exists
            var vote = await _context.Votes
                .FirstOrDefaultAsync(v => v.UserId == currentUserId.Value && v.QueueItemId == queueItemId);

            if (vote == null)
            {
                return NotFound(new
                {
                    Message = "Nie oddałeś głosu na ten utwór lub został on już cofnięty."
                });
            }

            // delete it
            _context.Votes.Remove(vote);
            await _context.SaveChangesAsync();

            // count remaining votes for this queue item
            int remainingVotes = await _context.Votes.CountAsync(v => v.QueueItemId == queueItemId);

            return Ok(new
            {
                Message = "Głos został pomyślnie cofnięty.",
                queueItemId,
                votesCount = remainingVotes,
                hasVoted = false
            });
        }
    }
}
