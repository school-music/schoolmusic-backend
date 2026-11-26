using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Hubs;
using schoolmusic_backend.Models;
using StackExchange.Redis;

namespace schoolmusic_backend.Services
{
    /// <summary>
    /// Reprezentuje żądanie zaproponowania utworu zawierające identyfikatory utworu i przerwy
    /// </summary>
    public record ProposeSongDto(
        int? SongId = null,
        string? SpotifyId = null,
        int? BreakId = null,
        string? Title = null,
        string? Artist = null,
        string? Genre = null,
        string? Mp3Url = null
    );

    /// <summary>
    /// Reprezentuje żądanie oddania głosu dla elementu kolejki, opcjonalnie powiązane z utworem lub przerwą
    /// </summary>
    public record VoteRequestDto(int? QueueItemId, int? SongId = null, int? BreakId = null);

    public sealed record ProposedSongMetadataDto(
        int SongId,
        string Title,
        string Artist,
        string ArtistName,
        int ArtistId,
        string? Cover,
        int DurationMs,
        string? Genre,
        string Source,
        string? SongUrl,
        string? SpotifyId
    );

    public sealed record ProposeSongApprovedDto(
        string Status,
        string Message,
        int QueueItemId,
        int BreakId,
        int BreakNumber,
        ProposedSongMetadataDto Song,
        int VotesCount
    );

    public sealed record ProposeSongPendingDto(
        string Status,
        string Message,
        string ProposalId,
        int BreakId,
        int BreakNumber,
        ProposedSongMetadataDto Song
    );

    public sealed record QueueTrackItemDto(
        int Position,
        int QueueItemId,
        int SongId,
        string Title,
        int ArtistId,
        string ArtistName,
        string? Cover,
        int DurationMs,
        string? Genre,
        string Source,
        string? SongUrl,
        string? SpotifyId,
        string Status,
        string? ModerationStatus,
        int VotesCount,
        bool HasVoted,
        string SubmittedBy,
        int OrderIndex
    );

    public sealed record QueueBroadcastItemDto(
        int Position,
        int QueueItemId,
        int SongId,
        string Title,
        int ArtistId,
        string ArtistName,
        string? Cover,
        int DurationMs,
        string? Genre,
        string Source,
        string? SongUrl,
        string? SpotifyId,
        string Status,
        string? ModerationStatus,
        int VotesCount,
        string SubmittedBy,
        int OrderIndex
    );

    public sealed record QueueForBreakResponseDto(
        int BreakId,
        int BreakNumber,
        DateOnly BreakDate,
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt,
        DateTimeOffset CutoffAt,
        string TimeZone,
        bool IsVotingOpen,
        string VotingStatus,
        string VotingReasonCode,
        string VotingReason,
        int TotalSongs,
        IReadOnlyList<int> UserVotedQueueItemIds,
        IReadOnlyList<QueueTrackItemDto> Queue
    );

    public sealed record VoteResponseDto(
        string Message,
        int QueueItemId,
        int VotesCount,
        bool HasVoted
    );

    public sealed record ArchivedPlayedTrackDto(
        int SongId,
        string Title,
        string Artist,
        string ArtistName,
        string? SpotifyId,
        string Source,
        int Votes
    );

    public sealed record ArchiveBreakResponseDto(
        string Message,
        int BreakId,
        int BreakNumber,
        int ArchivedTracksCount,
        int TotalClearedTracks,
        IReadOnlyList<ArchivedPlayedTrackDto> ArchivedTracks
    );

    public interface IQueueService
    {
        Task<ServiceResult<object>> GetQueueForBreakAsync(int breakId, int? currentUserId);
        Task<ServiceResult<object>> ProposeSongAsync(ProposeSongDto dto, int currentUserId);
        Task<ServiceResult<object>> VoteAsync(VoteRequestDto dto, int currentUserId);
        Task<ServiceResult<object>> RemoveVoteAsync(int queueItemId, int currentUserId);
        Task<ServiceResult<object>> ArchiveBreakAsync(int breakId);
        Task BroadcastQueueAsync(int breakId);
    }

    /// <summary>
    /// Zarządza kolejką utworów dla przerw: obsługuje zgłaszanie piosenek, moderację, głosowanie, cofanie głosów,
    /// archiwizację przerw oraz emisję aktualizacji przez SignalR
    /// </summary>
    public class QueueService : IQueueService
    {
        private const int SongCooldownHours = 24;
        private static readonly IQueueMutationLock DefaultMutationLock = new QueueMutationLock();

        private readonly schoolmusicContext _context;
        private readonly IConnectionMultiplexer _redis;
        private readonly IHubContext<QueueHub> _hubContext;
        private readonly ISongSourceService _songSourceService;
        private readonly IConfiguration? _configuration;
        private readonly IQueueMutationLock _mutationLock;
        private readonly TimeProvider _timeProvider;
        private readonly IVotingTargetService _votingTargetService;
        private readonly ILogger<QueueService>? _logger;

        public QueueService(
            schoolmusicContext context,
            IConnectionMultiplexer redis,
            IHubContext<QueueHub> hubContext,
            ISongSourceService songSourceService,
            IConfiguration? configuration = null,
            IQueueMutationLock? mutationLock = null,
            TimeProvider? timeProvider = null,
            ILogger<QueueService>? logger = null,
            IVotingTargetService? votingTargetService = null)
        {
            _context = context;
            _redis = redis;
            _hubContext = hubContext;
            _songSourceService = songSourceService;
            _configuration = configuration;
            _mutationLock = mutationLock ?? DefaultMutationLock;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _logger = logger;
            _votingTargetService = votingTargetService ?? new VotingTargetService(context, configuration, _timeProvider);
        }

        /// <summary>
        /// Wysyła zaktualizowaną listę utworów dla podanego breakId do klientów w grupie SignalR
        /// </summary>
        public Task BroadcastQueueAsync(int breakId) =>
            BroadcastQueueAsync(_context, _hubContext, breakId);

        /// <summary>
        /// Buduje ranking kolejki dla podanego breakId i emituje pełne metadane przez SignalR.
        /// </summary>
        public static async Task BroadcastQueueAsync(
            schoolmusicContext context,
            IHubContext<QueueHub> hubContext,
            int breakId)
        {
            var items = await context.QueueItems
                .Where(q => q.BreakId == breakId && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
                .Include(q => q.Song)
                    .ThenInclude(s => s.Artist)
                .Include(q => q.User)
                .Include(q => q.Votes)
                .ToListAsync();

            var ranking = items
                .OrderByDescending(q => q.Votes.Count)
                .ThenBy(q => q.OrderIndex)
                .Select((q, index) => new QueueBroadcastItemDto(
                    Position: index + 1,
                    QueueItemId: q.Id,
                    SongId: q.SongId,
                    Title: q.Song.Title,
                    ArtistId: q.Song.ArtistId,
                    ArtistName: q.Song.Artist?.Name ?? "Nieznany wykonawca",
                    Cover: q.Song.Cover,
                    DurationMs: q.Song.DurationMs,
                    Genre: q.Song.Genre,
                    Source: q.Song.Source,
                    SongUrl: q.Song.SongUrl,
                    SpotifyId: q.Song.SpotifyId,
                    Status: q.Status,
                    ModerationStatus: q.ModerationStatus,
                    VotesCount: q.Votes.Count,
                    SubmittedBy: q.User?.UserLogin ?? "Uczeń",
                    OrderIndex: q.OrderIndex
                ))
                .ToList();

            await hubContext.Clients.Group($"Break_{breakId}").SendAsync("QueueUpdated", new
            {
                breakId,
                totalSongs = ranking.Count,
                queue = ranking
            });
        }

        /// <summary>
        /// Zwraca uporządkowaną kolejkę utworów dla podanej przerwy wraz ze statusem głosowania,
        /// głosem bieżącego użytkownika i metadanymi Spotify.
        /// </summary>
        public async Task<ServiceResult<object>> GetQueueForBreakAsync(int breakId, int? currentUserId)
        {
            var breakEntity = await _context.Breaks.FindAsync(breakId);
            if (breakEntity == null)
            {
                breakEntity = await _context.Breaks.OrderBy(b => b.BreakNumber).FirstOrDefaultAsync();
                if (breakEntity == null)
                {
                    return ServiceResult<object>.NotFound("Nie znaleziono przerwy o podanym identyfikatorze.");
                }
                breakId = breakEntity.Id;
            }

            var evaluation = await _votingTargetService.EvaluateBreakAsync(breakId);
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
                .Select((q, index) => new QueueTrackItemDto(
                    Position: index + 1,
                    QueueItemId: q.Id,
                    SongId: q.SongId,
                    Title: q.Song.Title,
                    ArtistId: q.Song.ArtistId,
                    ArtistName: q.Song.Artist?.Name ?? "Nieznany wykonawca",
                    Cover: q.Song.Cover,
                    DurationMs: q.Song.DurationMs,
                    Genre: q.Song.Genre,
                    Source: q.Song.Source,
                    SongUrl: q.Song.SongUrl,
                    SpotifyId: q.Song.SpotifyId,
                    Status: q.Status,
                    ModerationStatus: q.ModerationStatus,
                    VotesCount: q.Votes.Count,
                    HasVoted: currentUserId.HasValue && q.Votes.Any(v => v.UserId == currentUserId.Value),
                    SubmittedBy: q.User?.UserLogin ?? "Uczeń",
                    OrderIndex: q.OrderIndex
                ))
                .ToList();

            var userVotedIds = ranking
                .Where(item => item.HasVoted)
                .Select(item => item.QueueItemId)
                .ToList();

            var response = new QueueForBreakResponseDto(
                BreakId: breakEntity.Id,
                BreakNumber: breakEntity.BreakNumber,
                BreakDate: evaluation?.TargetDate ?? breakEntity.BreakDate,
                StartsAt: evaluation?.StartsAt ?? new DateTimeOffset(breakEntity.StartAt, _timeProvider.LocalTimeZone.GetUtcOffset(breakEntity.StartAt)),
                EndsAt: evaluation?.EndsAt ?? new DateTimeOffset(breakEntity.EndsAt, _timeProvider.LocalTimeZone.GetUtcOffset(breakEntity.EndsAt)),
                CutoffAt: evaluation?.CutoffAt ?? new DateTimeOffset(breakEntity.VoteEnds ?? breakEntity.StartAt, _timeProvider.LocalTimeZone.GetUtcOffset(breakEntity.VoteEnds ?? breakEntity.StartAt)),
                TimeZone: _timeProvider.LocalTimeZone.Id,
                IsVotingOpen: evaluation?.IsOpen ?? true,
                VotingStatus: evaluation?.Status ?? "open",
                VotingReasonCode: evaluation?.ReasonCode ?? "voting_open",
                VotingReason: evaluation?.Reason ?? "Głosowanie jest otwarte.",
                TotalSongs: ranking.Count,
                UserVotedQueueItemIds: userVotedIds,
                Queue: ranking
            );

            return ServiceResult<object>.Ok(response);
        }

        /// <summary>
        /// Zgłasza utwór do przerwy radiowej; wykonuje walidacje (blokada użytkownika, istnienie utworu, czarna lista,
        /// cooldown, otwarte głosowanie) i albo automatycznie zatwierdza utwór z greenlisty, albo umieszcza propozycję
        /// w kolejce do weryfikacji moderatorów
        /// </summary>
        public async Task<ServiceResult<object>> ProposeSongAsync(ProposeSongDto dto, int currentUserId)
        {
            bool isBanned = await _context.BannedUsers.AnyAsync(b => b.UserId == currentUserId);
            if (isBanned)
            {
                return ServiceResult<object>.Forbidden("Twoje konto zostało zablokowane i nie ma uprawnień do zgłaszania utworów.");
            }

            Song? song = null;
            if (dto.SongId.HasValue)
            {
                song = await _context.Songs.Include(s => s.Artist).FirstOrDefaultAsync(s => s.Id == dto.SongId.Value);
            }
            else if (!string.IsNullOrWhiteSpace(dto.SpotifyId))
            {
                var normalizedSpotifyId = SpotifyTrackImporter.NormalizeSpotifyTrackId(dto.SpotifyId);
                song = await _context.Songs.Include(s => s.Artist)
                    .FirstOrDefaultAsync(s => s.SpotifyId == normalizedSpotifyId || s.SpotifyId == dto.SpotifyId);
                if (song == null)
                {
                    if (!_songSourceService.IsConfigured)
                    {
                        return ServiceResult<object>.ServiceUnavailable(
                            "Propozycje utworów Spotify są niedostępne. Skonfiguruj Spotify:ClientId i Spotify:ClientSecret.");
                    }

                    song = await SpotifyTrackImporter.GetOrImportAsync(_context, _songSourceService, dto.SpotifyId);
                }
            }

            if (song == null && !string.IsNullOrWhiteSpace(dto.Title))
            {
                var normTitle = dto.Title.Trim().ToLower();
                var candidateQuery = _context.Songs.Include(s => s.Artist).Where(s => s.Title.ToLower() == normTitle);
                if (!string.IsNullOrWhiteSpace(dto.Artist))
                {
                    var normArtist = dto.Artist.Trim().ToLower();
                    song = await candidateQuery.FirstOrDefaultAsync(s => s.Artist != null && s.Artist.Name.ToLower() == normArtist);
                }
                song ??= await candidateQuery.FirstOrDefaultAsync();

                if (song == null && !string.IsNullOrWhiteSpace(dto.Artist))
                {
                    var artistName = dto.Artist.Trim();
                    var artist = await _context.Artists.FirstOrDefaultAsync(a => a.Name.ToLower() == artistName.ToLower());
                    if (artist == null)
                    {
                        artist = new Artist { Name = artistName, Source = "other" };
                        _context.Artists.Add(artist);
                        await _context.SaveChangesAsync();
                    }

                    song = new Song
                    {
                        Title = dto.Title.Trim(),
                        ArtistId = artist.Id,
                        Artist = artist,
                        Genre = dto.Genre ?? "Inny",
                        DurationMs = 180000,
                        Source = "other",
                        SongUrl = dto.Mp3Url ?? "https://archive.org/download/sample.mp3",
                        SpotifyId = !string.IsNullOrWhiteSpace(dto.SpotifyId) ? dto.SpotifyId : null,
                        IsActive = true,
                        CreatedAt = _timeProvider.GetUtcNow().UtcDateTime
                    };
                    _context.Songs.Add(song);
                    await _context.SaveChangesAsync();
                }
            }

            if (song == null)
            {
                return ServiceResult<object>.NotFound("Nie znaleziono utworu w bazie danych ani w Spotify.");
            }

            bool isSongBlacklisted = await _context.SongBlacklists.AnyAsync(sb => sb.SongId == song.Id);
            bool isArtistBlacklisted = await _context.ArtistBlacklists.AnyAsync(ab => ab.ArtistId == song.ArtistId);

            if (isSongBlacklisted || isArtistBlacklisted)
            {
                return ServiceResult<object>.BadRequest("Ten utwór lub wykonawca znajduje się na czarnej liście i nie może być odtwarzany.");
            }

            var lastPlayed = await _context.Histories
                .Where(h => h.SongId == song.Id)
                .OrderByDescending(h => h.PlayedAt)
                .FirstOrDefaultAsync();

            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            if (lastPlayed != null && lastPlayed.PlayedAt > utcNow.AddHours(-SongCooldownHours))
            {
                var remaining = lastPlayed.PlayedAt.AddHours(SongCooldownHours) - utcNow;
                var hours = Math.Max(1, (int)Math.Ceiling(remaining.TotalHours));
                return ServiceResult<object>.BadRequest($"Ten utwór był niedawno odtwarzany na radiowęźle ({lastPlayed.PlayedAt:dd.MM HH:mm}). Będzie dostępny do ponownego zgłoszenia za ok. {hours} godz.");
            }

            int targetBreakId;
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
                var votingTarget = await _votingTargetService.GetVotingTargetAsync();
                if (votingTarget.EligibleBreakId.HasValue)
                {
                    targetBreakId = votingTarget.EligibleBreakId.Value;
                }
                else
                {
                    var allBreaks = await _context.Breaks.OrderBy(b => b.BreakNumber).ToListAsync();
                    Break? candidate = null;
                    foreach (var brk in allBreaks)
                    {
                        if (!await _votingTargetService.IsVotingClosedForBreakAsync(brk))
                        {
                            candidate = brk;
                            break;
                        }
                    }

                    candidate ??= allBreaks.FirstOrDefault();
                    if (candidate == null)
                    {
                        return ServiceResult<object>.BadRequest("Brak zdefiniowanych przerw w harmonogramie.");
                    }

                    targetBreakId = candidate.Id;
                }
            }

            var targetBreakEntity = await _context.Breaks.FindAsync(targetBreakId);
            if (targetBreakEntity == null)
            {
                return ServiceResult<object>.BadRequest("Nieprawidłowa przerwa docelowa.");
            }

            if (dto.BreakId.HasValue && await _votingTargetService.IsVotingClosedForBreakAsync(targetBreakEntity))
            {
                return ServiceResult<object>.BadRequest("Głosowanie na tę przerwę zostało już zamknięte.");
            }

            var songMetadata = ToProposedSongMetadata(song);
            await using var mutationLock = await _mutationLock.AcquireAsync(targetBreakId);

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

                await using var transaction = _context.Database.IsRelational()
                    ? await _context.Database.BeginTransactionAsync()
                    : null;
                QueueItem queueItem;
                try
                {
                    queueItem = new QueueItem
                    {
                        BreakId = targetBreakId,
                        SongId = song.Id,
                        UserId = currentUserId,
                        GreenlistId = greenlistEntry.Id,
                        ModerationStatus = "approved",
                        Status = QueueItemStatuses.Queued,
                        OrderIndex = currentMaxOrder + 1
                    };

                    _context.QueueItems.Add(queueItem);
                    await _context.SaveChangesAsync();

                    _context.Votes.Add(new Vote
                    {
                        UserId = currentUserId,
                        QueueItemId = queueItem.Id,
                        VotedAt = _timeProvider.GetUtcNow().UtcDateTime
                    });
                    await _context.SaveChangesAsync();

                    if (transaction != null)
                        await transaction.CommitAsync();
                }
                catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation())
                {
                    if (transaction != null)
                        await transaction.RollbackAsync();
                    _logger?.LogWarning(ex,
                        "Odrzucono równoczesne dodanie utworu {SongId} do kolejki przerwy {BreakId}.",
                        song.Id,
                        targetBreakId);
                    return ServiceResult<object>.Conflict("Ten utwór został już zgłoszony lub kolejka zmieniła się równocześnie. Odśwież kolejkę i spróbuj ponownie.");
                }

                await BroadcastQueueAsync(targetBreakId);
                _logger?.LogInformation(
                    "Dodano zatwierdzony utwór {SongId} jako pozycję {QueueItemId} kolejki przerwy {BreakId}.",
                    song.Id,
                    queueItem.Id,
                    targetBreakId);

                return ServiceResult<object>.Created(new ProposeSongApprovedDto(
                    Status: "approved",
                    Message: "Utwór znajduje się na zielonej liście i został automatycznie zatwierdzony do kolejki!",
                    QueueItemId: queueItem.Id,
                    BreakId: targetBreakId,
                    BreakNumber: targetBreakEntity.BreakNumber,
                    Song: songMetadata,
                    VotesCount: 1
                ));
            }

            try
            {
                var db = _redis.GetDatabase();

                var existingPending = await db.ListRangeAsync(PendingProposalStore.ListKey);
                foreach (var item in existingPending)
                {
                    if (item.HasValue && item.ToString().Contains($"\"songId\":{song.Id}") && item.ToString().Contains($"\"breakId\":{targetBreakId}"))
                    {
                        return ServiceResult<object>.Conflict("Ten utwór już oczekuje na weryfikację przez moderatora na tę przerwę.");
                    }
                }

                var userEntity = await _context.Users.FindAsync(currentUserId);
                var proposalId = Guid.NewGuid().ToString();

                var proposal = new
                {
                    id = proposalId,
                    songId = song.Id,
                    spotifyId = song.SpotifyId,
                    source = song.Source,
                    songUrl = song.SongUrl,
                    title = song.Title,
                    artist = song.Artist?.Name ?? "Nieznany wykonawca",
                    cover = song.Cover,
                    durationMs = song.DurationMs,
                    breakId = targetBreakId,
                    breakNumber = targetBreakEntity.BreakNumber,
                    userId = currentUserId,
                    submittedBy = userEntity?.UserLogin ?? "Uczeń",
                    submittedAt = _timeProvider.GetUtcNow().UtcDateTime
                };

                var deduplicationKey = PendingProposalStore.GetDeduplicationKey(targetBreakId, song.Id);
                var reserved = await db.StringSetAsync(
                    deduplicationKey,
                    proposalId,
                    expiry: (TimeSpan?)null,
                    when: When.NotExists);
                if (!reserved)
                {
                    return ServiceResult<object>.Conflict("Ten utwór już oczekuje na weryfikację przez moderatora na tę przerwę.");
                }

                try
                {
                    await db.ListRightPushAsync(PendingProposalStore.ListKey, JsonSerializer.Serialize(proposal));
                }
                catch (RedisException ex)
                {
                    _logger?.LogWarning(ex,
                        "Nie udało się dopisać propozycji utworu {SongId} do kolejki moderacji; rezerwacja zostanie zwolniona.",
                        song.Id);
                    try
                    {
                        await db.KeyDeleteAsync(deduplicationKey);
                    }
                    catch (RedisException cleanupException)
                    {
                        _logger?.LogWarning(cleanupException,
                            "Nie udało się usunąć rezerwacji propozycji dla utworu {SongId} i przerwy {BreakId}.",
                            song.Id,
                            targetBreakId);
                    }
                    throw;
                }

                _logger?.LogInformation(
                    "Zapisano propozycję {ProposalId} utworu {SongId} dla przerwy {BreakId} w kolejce moderacji.",
                    proposalId,
                    song.Id,
                    targetBreakId);

                return ServiceResult<object>.Accepted(new ProposeSongPendingDto(
                    Status: "pending",
                    Message: "Piosenka została przesłana do weryfikacji przez moderatora. Po zatwierdzeniu pojawi się w rankingu.",
                    ProposalId: proposalId,
                    BreakId: targetBreakId,
                    BreakNumber: targetBreakEntity.BreakNumber,
                    Song: songMetadata
                ));
            }
            catch (RedisException ex)
            {
                _logger?.LogError(ex,
                    "Redis jest niedostępny podczas zapisu propozycji utworu {SongId} dla przerwy {BreakId}.",
                    song.Id,
                    targetBreakId);
                return ServiceResult<object>.ServiceUnavailable(
                    "Kolejka moderacji jest chwilowo niedostępna. Spróbuj ponownie za chwilę.");
            }
        }

        /// <summary>
        /// Oddaje głos dla pozycji w kolejce przerwy; w razie potrzeby może automatycznie dodać utwór z zielonej listy
        /// </summary>
        public async Task<ServiceResult<object>> VoteAsync(VoteRequestDto dto, int currentUserId)
        {
            bool isBanned = await _context.BannedUsers.AnyAsync(b => b.UserId == currentUserId);
            if (isBanned)
            {
                return ServiceResult<object>.Forbidden("Twoje konto zostało zablokowane i nie ma uprawnień do głosowania.");
            }

            int targetBreakId;
            if (dto.QueueItemId.HasValue)
            {
                var existingBreakId = await _context.QueueItems
                    .Where(q => q.Id == dto.QueueItemId.Value)
                    .Select(q => (int?)q.BreakId)
                    .FirstOrDefaultAsync();
                if (!existingBreakId.HasValue)
                    return ServiceResult<object>.NotFound("Nie znaleziono pozycji w kolejce o podanym identyfikatorze.");
                targetBreakId = existingBreakId.Value;
            }
            else if (dto.SongId.HasValue && dto.BreakId.HasValue)
            {
                targetBreakId = dto.BreakId.Value;
            }
            else
            {
                return ServiceResult<object>.BadRequest("Musisz podać QueueItemId lub parę (SongId, BreakId).");
            }

            int targetQueueItemId;
            int totalVotes;
            await using (var mutationLock = await _mutationLock.AcquireAsync(targetBreakId))
            {
                await using var transaction = _context.Database.IsRelational()
                    ? await _context.Database.BeginTransactionAsync()
                    : null;

                try
                {
                    var breakEntity = await _context.Breaks.FindAsync(targetBreakId);
                    if (breakEntity == null)
                        return ServiceResult<object>.NotFound("Przerwa docelowa nie istnieje.");

                    if (await _votingTargetService.IsVotingClosedForBreakAsync(breakEntity))
                        return ServiceResult<object>.BadRequest("Głosowanie na tę przerwę dobiegło już końca.");

                    QueueItem? targetQueueItem;
                    if (dto.QueueItemId.HasValue)
                    {
                        targetQueueItem = await _context.QueueItems
                            .FirstOrDefaultAsync(q => q.Id == dto.QueueItemId.Value && q.BreakId == targetBreakId);
                    }
                    else
                    {
                        var songId = dto.SongId!.Value;
                        var greenlistEntry = await _context.SongGreenlists
                            .FirstOrDefaultAsync(g => g.SongId == songId);
                        if (greenlistEntry == null)
                        {
                            return ServiceResult<object>.BadRequest("Ten utwór nie znajduje się na zielonej liście. Możesz go zgłosić do weryfikacji przez moderatora.");
                        }

                        targetQueueItem = await _context.QueueItems
                            .FirstOrDefaultAsync(q => q.BreakId == targetBreakId && q.SongId == songId);
                        if (targetQueueItem == null)
                        {
                            var lastPlayed = await _context.Histories
                                .Where(h => h.SongId == songId)
                                .OrderByDescending(h => h.PlayedAt)
                                .FirstOrDefaultAsync();

                            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
                            if (lastPlayed != null && lastPlayed.PlayedAt > utcNow.AddHours(-SongCooldownHours))
                            {
                                var remaining = lastPlayed.PlayedAt.AddHours(SongCooldownHours) - utcNow;
                                var hours = Math.Max(1, (int)Math.Ceiling(remaining.TotalHours));
                                return ServiceResult<object>.BadRequest($"Ten utwór był niedawno odtwarzany na radiowęźle ({lastPlayed.PlayedAt:dd.MM HH:mm}). Będzie dostępny za ok. {hours} godz.");
                            }

                            int currentMaxOrder = await _context.QueueItems
                                .Where(q => q.BreakId == targetBreakId)
                                .MaxAsync(q => (int?)q.OrderIndex) ?? 0;
                            targetQueueItem = new QueueItem
                            {
                                BreakId = targetBreakId,
                                SongId = songId,
                                UserId = currentUserId,
                                GreenlistId = greenlistEntry.Id,
                                ModerationStatus = "approved",
                                Status = QueueItemStatuses.Queued,
                                OrderIndex = currentMaxOrder + 1
                            };
                            _context.QueueItems.Add(targetQueueItem);
                            await _context.SaveChangesAsync();
                        }
                    }

                    if (targetQueueItem == null)
                        return ServiceResult<object>.NotFound("Nie znaleziono pozycji w kolejce o podanym identyfikatorze.");
                    if (targetQueueItem.Status != QueueItemStatuses.Queued ||
                        (targetQueueItem.ModerationStatus != "approved" && targetQueueItem.ModerationStatus != null))
                    {
                        return ServiceResult<object>.BadRequest("Głos można oddać wyłącznie na zatwierdzony utwór oczekujący w kolejce.");
                    }

                    targetQueueItemId = targetQueueItem.Id;
                    bool alreadyVoted = await _context.Votes
                        .AnyAsync(v => v.UserId == currentUserId && v.QueueItemId == targetQueueItemId);
                    if (alreadyVoted)
                        return ServiceResult<object>.Conflict("Już oddałeś głos na ten utwór! Nie możesz zagłosować dwukrotnie.");

                    _context.Votes.Add(new Vote
                    {
                        UserId = currentUserId,
                        QueueItemId = targetQueueItemId,
                        VotedAt = _timeProvider.GetUtcNow().UtcDateTime
                    });
                    await _context.SaveChangesAsync();

                    if (transaction != null)
                        await transaction.CommitAsync();
                    totalVotes = await _context.Votes.CountAsync(v => v.QueueItemId == targetQueueItemId);
                }
                catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation())
                {
                    if (transaction != null)
                        await transaction.RollbackAsync();
                    _logger?.LogWarning(ex,
                        "Odrzucono równoczesny głos użytkownika {UserId} w kolejce przerwy {BreakId}.",
                        currentUserId,
                        targetBreakId);
                    return ServiceResult<object>.Conflict("Głos został już oddany lub kolejka zmieniła się równocześnie. Odśwież dane i spróbuj ponownie.");
                }
            }

            await BroadcastQueueAsync(targetBreakId);
            _logger?.LogInformation(
                "Użytkownik {UserId} oddał głos na pozycję {QueueItemId} kolejki przerwy {BreakId}.",
                currentUserId,
                targetQueueItemId,
                targetBreakId);

            return ServiceResult<object>.Ok(new VoteResponseDto(
                Message: "Głos został pomyślnie oddany!",
                QueueItemId: targetQueueItemId,
                VotesCount: totalVotes,
                HasVoted: true
            ));
        }

        /// <summary>
        /// Usuwa głos użytkownika dla wskazanego elementu kolejki, zapisuje zmiany
        /// oraz powiadamia klientów o aktualizacji kolejki
        /// </summary>
        public async Task<ServiceResult<object>> RemoveVoteAsync(int queueItemId, int currentUserId)
        {
            var breakId = await _context.QueueItems
                .Where(item => item.Id == queueItemId)
                .Select(item => (int?)item.BreakId)
                .FirstOrDefaultAsync();
            if (!breakId.HasValue)
                return ServiceResult<object>.NotFound("Nie oddałeś głosu na ten utwór lub został on już cofnięty.");

            int remainingVotes;
            await using (var mutationLock = await _mutationLock.AcquireAsync(breakId.Value))
            {
                var vote = await _context.Votes
                    .Include(v => v.QueueItem)
                    .ThenInclude(item => item.Break)
                    .FirstOrDefaultAsync(v => v.UserId == currentUserId && v.QueueItemId == queueItemId);
                if (vote == null)
                    return ServiceResult<object>.NotFound("Nie oddałeś głosu na ten utwór lub został on już cofnięty.");
                if (vote.QueueItem.Status != QueueItemStatuses.Queued ||
                    await _votingTargetService.IsVotingClosedForBreakAsync(vote.QueueItem.Break))
                    return ServiceResult<object>.BadRequest("Nie można cofnąć głosu po zamknięciu głosowania lub rozpoczęciu odtwarzania utworu.");

                _context.Votes.Remove(vote);
                await _context.SaveChangesAsync();
                remainingVotes = await _context.Votes.CountAsync(v => v.QueueItemId == queueItemId);
            }

            await BroadcastQueueAsync(breakId.Value);
            _logger?.LogInformation(
                "Użytkownik {UserId} cofnął głos na pozycję {QueueItemId} kolejki przerwy {BreakId}.",
                currentUserId,
                queueItemId,
                breakId.Value);

            return ServiceResult<object>.Ok(new VoteResponseDto(
                Message: "Głos został pomyślnie cofnięty.",
                QueueItemId: queueItemId,
                VotesCount: remainingVotes,
                HasVoted: false
            ));
        }

        /// <summary>
        /// Archiwizuje przerwę o podanym identyfikatorze: zapisuje odtworzone utwory do historii, czyści kolejkę
        /// (usuwając pozycje i powiązane głosy) oraz wysyła powiadomienie SignalR
        /// </summary>
        public async Task<ServiceResult<object>> ArchiveBreakAsync(int breakId)
        {
            sbyte breakNumber;
            var playedTracksInfo = new List<ArchivedPlayedTrackDto>();
            var totalClearedTracks = 0;

            await using (var mutationLock = await _mutationLock.AcquireAsync(breakId))
            {
                var breakEntity = await _context.Breaks.FindAsync(breakId);
                if (breakEntity == null)
                    return ServiceResult<object>.NotFound("Nie znaleziono przerwy o podanym identyfikatorze.");
                breakNumber = breakEntity.BreakNumber;

                await using var transaction = _context.Database.IsRelational()
                    ? await _context.Database.BeginTransactionAsync()
                    : null;

                var approvedQueueItems = await _context.QueueItems
                    .Where(q => q.BreakId == breakId && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
                    .Include(q => q.Song)
                        .ThenInclude(s => s.Artist)
                    .Include(q => q.Votes)
                    .ToListAsync();
                var playedItems = approvedQueueItems
                    .Where(item => item.Status == QueueItemStatuses.Played || item.Status == QueueItemStatuses.Playing)
                    .ToList();

                var now = _timeProvider.GetUtcNow().UtcDateTime;
                foreach (var played in playedItems)
                {
                    _context.Histories.Add(new History
                    {
                        SongId = played.SongId,
                        BreakId = breakId,
                        PlayedAt = now,
                        Votes = played.Votes.Count
                    });

                    var artistName = played.Song.Artist?.Name ?? "Nieznany wykonawca";
                    playedTracksInfo.Add(new ArchivedPlayedTrackDto(
                        SongId: played.SongId,
                        Title: played.Song.Title,
                        Artist: artistName,
                        ArtistName: artistName,
                        SpotifyId: played.Song.SpotifyId,
                        Source: played.Song.Source,
                        Votes: played.Votes.Count
                    ));
                }

                var allBreakQueueItems = await _context.QueueItems
                    .Where(q => q.BreakId == breakId)
                    .Include(q => q.Votes)
                    .ToListAsync();
                totalClearedTracks = allBreakQueueItems.Count;

                var allVotes = allBreakQueueItems.SelectMany(q => q.Votes).ToList();
                if (allVotes.Count > 0)
                    _context.Votes.RemoveRange(allVotes);
                if (allBreakQueueItems.Count > 0)
                    _context.QueueItems.RemoveRange(allBreakQueueItems);

                await _context.SaveChangesAsync();
                if (transaction != null)
                    await transaction.CommitAsync();
            }

            await BroadcastQueueAsync(breakId);
            _logger?.LogInformation(
                "Zarchiwizowano przerwę {BreakId}: {ArchivedCount} odtworzonych i {ClearedCount} usuniętych pozycji.",
                breakId,
                playedTracksInfo.Count,
                totalClearedTracks);

            return ServiceResult<object>.Ok(new ArchiveBreakResponseDto(
                Message: $"Przerwa #{breakNumber} została pomyślnie zarchiwizowana. Zapisano historię i wyczyszczono kolejkę.",
                BreakId: breakId,
                BreakNumber: breakNumber,
                ArchivedTracksCount: playedTracksInfo.Count,
                TotalClearedTracks: totalClearedTracks,
                ArchivedTracks: playedTracksInfo
            ));
        }

        private static ProposedSongMetadataDto ToProposedSongMetadata(Song song)
        {
            var artistName = song.Artist?.Name ?? "Nieznany wykonawca";
            return new ProposedSongMetadataDto(
                SongId: song.Id,
                Title: song.Title,
                Artist: artistName,
                ArtistName: artistName,
                ArtistId: song.ArtistId,
                Cover: song.Cover,
                DurationMs: song.DurationMs,
                Genre: song.Genre,
                Source: song.Source,
                SongUrl: song.SongUrl,
                SpotifyId: song.SpotifyId);
        }
    }
}
