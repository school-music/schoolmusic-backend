using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Hubs;
using schoolmusic_backend.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace schoolmusic_backend.Services
{
    public record ProposeSongDto(int? SongId, string? SpotifyId, int? BreakId);
    public record VoteRequestDto(int? QueueItemId, int? SongId = null, int? BreakId = null);

    public interface IQueueService
    {
        Task<ServiceResult<object>> GetQueueForBreakAsync(int breakId, int? currentUserId);
        Task<ServiceResult<object>> ProposeSongAsync(ProposeSongDto dto, int currentUserId);
        Task<ServiceResult<object>> VoteAsync(VoteRequestDto dto, int currentUserId);
        Task<ServiceResult<object>> RemoveVoteAsync(int queueItemId, int currentUserId);
        Task<ServiceResult<object>> ArchiveBreakAsync(int breakId);
        Task BroadcastQueueAsync(int breakId);
    }

    public class QueueService : IQueueService
    {
        private const int SongCooldownHours = 24;

        private readonly schoolmusicContext _context;
        private readonly IConnectionMultiplexer _redis;
        private readonly IHubContext<QueueHub> _hubContext;
        private readonly IBreakService _breakService;

        public QueueService(
            schoolmusicContext context,
            IConnectionMultiplexer redis,
            IHubContext<QueueHub> hubContext,
            IBreakService breakService)
        {
            _context = context;
            _redis = redis;
            _hubContext = hubContext;
            _breakService = breakService;
        }

        public async Task BroadcastQueueAsync(int breakId)
        {
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
        }

        public async Task<ServiceResult<object>> GetQueueForBreakAsync(int breakId, int? currentUserId)
        {
            var breakEntity = await _context.Breaks.FindAsync(breakId);
            if (breakEntity == null)
            {
                return ServiceResult<object>.NotFound("Nie znaleziono przerwy o podanym identyfikatorze.");
            }

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

            return ServiceResult<object>.Ok(new
            {
                breakId = breakEntity.Id,
                breakNumber = breakEntity.BreakNumber,
                totalSongs = ranking.Count,
                queue = ranking
            });
        }

        public async Task<ServiceResult<object>> ProposeSongAsync(ProposeSongDto dto, int currentUserId)
        {
            // Sprawdzenie blokady użytkownika
            bool isBanned = await _context.BannedUsers.AnyAsync(b => b.UserId == currentUserId);
            if (isBanned)
            {
                return ServiceResult<object>.Forbidden("Twoje konto zostało zablokowane i nie ma uprawnień do zgłaszania utworów.");
            }

            // Wyszukanie utworu w bazie
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
                return ServiceResult<object>.NotFound("Nie znaleziono utworu w bazie danych.");
            }

            // Sprawdzenie czarnej listy
            bool isSongBlacklisted = await _context.SongBlacklists.AnyAsync(sb => sb.SongId == song.Id);
            bool isArtistBlacklisted = await _context.ArtistBlacklists.AnyAsync(ab => ab.ArtistId == song.ArtistId);

            if (isSongBlacklisted || isArtistBlacklisted)
            {
                return ServiceResult<object>.BadRequest("Ten utwór lub wykonawca znajduje się na czarnej liście i nie może być odtwarzany.");
            }

            // Sprawdzenie cooldownu (czy utwór nie był odtwarzany w ciągu ostatnich 24 godzin)
            var lastPlayed = await _context.Histories
                .Where(h => h.SongId == song.Id)
                .OrderByDescending(h => h.PlayedAt)
                .FirstOrDefaultAsync();

            if (lastPlayed != null && lastPlayed.PlayedAt > DateTime.UtcNow.AddHours(-SongCooldownHours))
            {
                var remaining = lastPlayed.PlayedAt.AddHours(SongCooldownHours) - DateTime.UtcNow;
                var hours = Math.Max(1, (int)Math.Ceiling(remaining.TotalHours));
                return ServiceResult<object>.BadRequest($"Ten utwór był niedawno odtwarzany na radiowęźle ({lastPlayed.PlayedAt:dd.MM HH:mm}). Będzie dostępny do ponownego zgłoszenia za ok. {hours} godz.");
            }

            // Ustalenie przerwy docelowej
            int targetBreakId;
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);

            if (dto.BreakId.HasValue)
            {
                var selectedBreak = await _context.Breaks.FindAsync(dto.BreakId.Value);
                if (selectedBreak == null)
                {
                    return ServiceResult<object>.NotFound("Wybrana przerwa nie istnieje.");
                }
                targetBreakId = selectedBreak.Id;
            }
            else
            {
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
                    return ServiceResult<object>.BadRequest("Brak zdefiniowanych przerw w harmonogramie.");
                }

                targetBreakId = candidate.Id;
            }

            var targetBreakEntity = await _context.Breaks.FindAsync(targetBreakId);
            if (targetBreakEntity == null)
            {
                return ServiceResult<object>.BadRequest("Nieprawidłowa przerwa docelowa.");
            }

            // Sprawdzenie czy głosowanie jest jeszcze otwarte
            var targetVoteEndTime = targetBreakEntity.VoteEnds.HasValue
                ? today.ToDateTime(TimeOnly.FromDateTime(targetBreakEntity.VoteEnds.Value))
                : today.ToDateTime(TimeOnly.FromDateTime(targetBreakEntity.StartAt));

            if (dto.BreakId.HasValue && targetVoteEndTime < now)
            {
                return ServiceResult<object>.BadRequest("Głosowanie na tę przerwę zostało już zamknięte.");
            }

            // Sprawdzenie czy utwór jest na Greenliście
            var greenlistEntry = await _context.SongGreenlists.FirstOrDefaultAsync(g => g.SongId == song.Id);

            if (greenlistEntry != null)
            {
                bool alreadyQueued = await _context.QueueItems
                    .AnyAsync(q => q.BreakId == targetBreakId && q.SongId == song.Id);

                if (alreadyQueued)
                {
                    return ServiceResult<object>.Conflict("Ten utwór został już zgłoszony na tę przerwę!");
                }

                int currentMaxOrder = await _context.QueueItems
                    .Where(q => q.BreakId == targetBreakId)
                    .MaxAsync(q => (int?)q.OrderIndex) ?? 0;

                var queueItem = new QueueItem
                {
                    BreakId = targetBreakId,
                    SongId = song.Id,
                    UserId = currentUserId,
                    GreenlistId = greenlistEntry.Id,
                    ModerationStatus = "approved",
                    OrderIndex = currentMaxOrder + 1
                };

                _context.QueueItems.Add(queueItem);
                await _context.SaveChangesAsync();

                var initialVote = new Vote
                {
                    UserId = currentUserId,
                    QueueItemId = queueItem.Id
                };
                _context.Votes.Add(initialVote);
                await _context.SaveChangesAsync();

                await BroadcastQueueAsync(targetBreakId);

                return ServiceResult<object>.Created(new
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

            // Utwór spoza Greenlisty trafia do Redis do weryfikacji przez moderatorów
            var db = _redis.GetDatabase();
            var pendingKey = "proposals:pending";

            var existingPending = await db.ListRangeAsync(pendingKey);
            foreach (var item in existingPending)
            {
                if (item.HasValue && item.ToString().Contains($"\"songId\":{song.Id}") && item.ToString().Contains($"\"breakId\":{targetBreakId}"))
                {
                    return ServiceResult<object>.Conflict("Ten utwór już oczekuje na weryfikację przez moderatora na tę przerwę.");
                }
            }

            var userEntity = await _context.Users.FindAsync(currentUserId);

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
                userId = currentUserId,
                submittedBy = userEntity?.UserLogin ?? "Uczeń",
                submittedAt = DateTime.UtcNow
            };

            await db.ListRightPushAsync(pendingKey, JsonSerializer.Serialize(proposal));

            return ServiceResult<object>.Accepted(new
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

        public async Task<ServiceResult<object>> VoteAsync(VoteRequestDto dto, int currentUserId)
        {
            bool isBanned = await _context.BannedUsers.AnyAsync(b => b.UserId == currentUserId);
            if (isBanned)
            {
                return ServiceResult<object>.Forbidden("Twoje konto zostało zablokowane i nie ma uprawnień do głosowania.");
            }

            int targetQueueItemId;
            int targetBreakId;

            if (dto.QueueItemId.HasValue)
            {
                var existingItem = await _context.QueueItems
                    .Include(q => q.Break)
                    .FirstOrDefaultAsync(q => q.Id == dto.QueueItemId.Value);

                if (existingItem == null)
                {
                    return ServiceResult<object>.NotFound("Nie znaleziono pozycji w kolejce o podanym identyfikatorze.");
                }

                targetQueueItemId = existingItem.Id;
                targetBreakId = existingItem.BreakId;
            }
            else if (dto.SongId.HasValue && dto.BreakId.HasValue)
            {
                targetBreakId = dto.BreakId.Value;

                var greenlistEntry = await _context.SongGreenlists
                    .FirstOrDefaultAsync(g => g.SongId == dto.SongId.Value);

                if (greenlistEntry == null)
                {
                    return ServiceResult<object>.BadRequest("Ten utwór nie znajduje się na zielonej liście. Możesz go zgłosić do weryfikacji przez moderatora.");
                }

                var queuedItem = await _context.QueueItems
                    .FirstOrDefaultAsync(q => q.BreakId == targetBreakId && q.SongId == dto.SongId.Value);

                if (queuedItem != null)
                {
                    targetQueueItemId = queuedItem.Id;
                }
                else
                {
                    // Sprawdzenie cooldownu przed automatycznym dodaniem z Greenlisty
                    var lastPlayed = await _context.Histories
                        .Where(h => h.SongId == dto.SongId.Value)
                        .OrderByDescending(h => h.PlayedAt)
                        .FirstOrDefaultAsync();

                    if (lastPlayed != null && lastPlayed.PlayedAt > DateTime.UtcNow.AddHours(-SongCooldownHours))
                    {
                        var remaining = lastPlayed.PlayedAt.AddHours(SongCooldownHours) - DateTime.UtcNow;
                        var hours = Math.Max(1, (int)Math.Ceiling(remaining.TotalHours));
                        return ServiceResult<object>.BadRequest($"Ten utwór był niedawno odtwarzany na radiowęźle ({lastPlayed.PlayedAt:dd.MM HH:mm}). Będzie dostępny za ok. {hours} godz.");
                    }

                    int currentMaxOrder = await _context.QueueItems
                        .Where(q => q.BreakId == targetBreakId)
                        .MaxAsync(q => (int?)q.OrderIndex) ?? 0;

                    var newItem = new QueueItem
                    {
                        BreakId = targetBreakId,
                        SongId = dto.SongId.Value,
                        UserId = currentUserId,
                        GreenlistId = greenlistEntry.Id,
                        ModerationStatus = "approved",
                        OrderIndex = currentMaxOrder + 1
                    };

                    _context.QueueItems.Add(newItem);
                    await _context.SaveChangesAsync();
                    targetQueueItemId = newItem.Id;
                }
            }
            else
            {
                return ServiceResult<object>.BadRequest("Musisz podać QueueItemId lub parę (SongId, BreakId).");
            }

            var breakEntity = await _context.Breaks.FindAsync(targetBreakId);
            if (breakEntity == null)
            {
                return ServiceResult<object>.NotFound("Przerwa docelowa nie istnieje.");
            }

            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            var voteEndTime = breakEntity.VoteEnds.HasValue
                ? today.ToDateTime(TimeOnly.FromDateTime(breakEntity.VoteEnds.Value))
                : today.ToDateTime(TimeOnly.FromDateTime(breakEntity.StartAt));

            if (voteEndTime < now)
            {
                return ServiceResult<object>.BadRequest("Głosowanie na tę przerwę dobiegło już końca.");
            }

            bool alreadyVoted = await _context.Votes
                .AnyAsync(v => v.UserId == currentUserId && v.QueueItemId == targetQueueItemId);

            if (alreadyVoted)
            {
                return ServiceResult<object>.Conflict("Już oddałeś głos na ten utwór! Nie możesz zagłosować dwukrotnie.");
            }

            var vote = new Vote
            {
                UserId = currentUserId,
                QueueItemId = targetQueueItemId
            };

            _context.Votes.Add(vote);
            await _context.SaveChangesAsync();

            int totalVotes = await _context.Votes.CountAsync(v => v.QueueItemId == targetQueueItemId);

            await BroadcastQueueAsync(targetBreakId);

            return ServiceResult<object>.Ok(new
            {
                Message = "Głos został pomyślnie oddany!",
                queueItemId = targetQueueItemId,
                votesCount = totalVotes,
                hasVoted = true
            });
        }

        public async Task<ServiceResult<object>> RemoveVoteAsync(int queueItemId, int currentUserId)
        {
            var vote = await _context.Votes
                .Include(v => v.QueueItem)
                .FirstOrDefaultAsync(v => v.UserId == currentUserId && v.QueueItemId == queueItemId);

            if (vote == null)
            {
                return ServiceResult<object>.NotFound("Nie oddałeś głosu na ten utwór lub został on już cofnięty.");
            }

            int breakId = vote.QueueItem.BreakId;

            _context.Votes.Remove(vote);
            await _context.SaveChangesAsync();

            int remainingVotes = await _context.Votes.CountAsync(v => v.QueueItemId == queueItemId);

            await BroadcastQueueAsync(breakId);

            return ServiceResult<object>.Ok(new
            {
                Message = "Głos został pomyślnie cofnięty.",
                queueItemId,
                votesCount = remainingVotes,
                hasVoted = false
            });
        }

        public async Task<ServiceResult<object>> ArchiveBreakAsync(int breakId)
        {
            var breakEntity = await _context.Breaks.FindAsync(breakId);
            if (breakEntity == null)
            {
                return ServiceResult<object>.NotFound("Nie znaleziono przerwy o podanym identyfikatorze.");
            }

            // 1. Obliczamy czas trwania przerwy w sekundach
            DateTime end = breakEntity.EndsAt.HasValue ? breakEntity.EndsAt.Value : breakEntity.StartAt;
            int durationSeconds = (int)(end - breakEntity.StartAt).TotalSeconds;
            if (durationSeconds <= 0)
            {
                durationSeconds = 600;
            }

            // 2. Pobieramy wszystkie zatwierdzone pozycje z kolejki tej przerwy
            var approvedQueueItems = await _context.QueueItems
                .Where(q => q.BreakId == breakId && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
                .Include(q => q.Song)
                    .ThenInclude(s => s.Artist)
                .Include(q => q.Votes)
                .ToListAsync();

            var now = DateTime.UtcNow;
            var playedTracksInfo = new List<object>();

            if (approvedQueueItems.Count > 0)
            {
                // 3. Używamy algorytmu BreakService, aby ustalić, które dokładnie utwory zmieściły się w czasie przerwy
                var schedulePlan = _breakService.CalculateBreak(breakId, durationSeconds, approvedQueueItems);
                var playedQueueItemIds = schedulePlan.Tracks.Select(t => t.QueueItemId).ToHashSet();

                var playedItems = approvedQueueItems.Where(q => playedQueueItemIds.Contains(q.Id)).ToList();

                // 4. Zapisujemy odtworzone utwory do tabeli history
                foreach (var played in playedItems)
                {
                    var historyEntry = new History
                    {
                        SongId = played.SongId,
                        BreakId = breakId,
                        PlayedAt = now,
                        Votes = played.Votes.Count
                    };
                    _context.Histories.Add(historyEntry);

                    playedTracksInfo.Add(new
                    {
                        songId = played.SongId,
                        title = played.Song.Title,
                        artist = played.Song.Artist?.Name ?? "Nieznany wykonawca",
                        votes = played.Votes.Count
                    });
                }
            }

            // 5. Usuwamy wszystkie QueueItems i powiązane głosy dla tej przerwy (czyścimy kolejkę)
            var allBreakQueueItems = await _context.QueueItems
                .Where(q => q.BreakId == breakId)
                .Include(q => q.Votes)
                .ToListAsync();

            var allVotes = allBreakQueueItems.SelectMany(q => q.Votes).ToList();
            if (allVotes.Count > 0)
            {
                _context.Votes.RemoveRange(allVotes);
            }

            if (allBreakQueueItems.Count > 0)
            {
                _context.QueueItems.RemoveRange(allBreakQueueItems);
            }

            await _context.SaveChangesAsync();

            // 6. Rozsyłamy powiadomienie SignalR (kolejka jest teraz wyczyszczona)
            await BroadcastQueueAsync(breakId);

            return ServiceResult<object>.Ok(new
            {
                Message = $"Przerwa #{breakEntity.BreakNumber} została pomyślnie zarchiwizowana. Zapisano historię i wyczyszczono kolejkę.",
                breakId = breakEntity.Id,
                breakNumber = breakEntity.BreakNumber,
                archivedTracksCount = playedTracksInfo.Count,
                totalClearedTracks = allBreakQueueItems.Count,
                archivedTracks = playedTracksInfo
            });
        }
    }
}
