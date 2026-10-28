using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using schoolmusic_backend.Hubs;
using schoolmusic_backend.Models;
using schoolmusic_backend.Extensions;
using StackExchange.Redis;
using System.Text.Json;

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
        private readonly ILogger<QueueService>? _logger;

        public QueueService(
            schoolmusicContext context,
            IConnectionMultiplexer redis,
            IHubContext<QueueHub> hubContext,
            ISongSourceService songSourceService,
            IConfiguration? configuration = null,
            IQueueMutationLock? mutationLock = null,
            TimeProvider? timeProvider = null,
            ILogger<QueueService>? logger = null)
        {
            _context = context;
            _redis = redis;
            _hubContext = hubContext;
            _songSourceService = songSourceService;
            _configuration = configuration;
            _mutationLock = mutationLock ?? DefaultMutationLock;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _logger = logger;
        }

        /// <summary>
        /// Sprawdza, czy głosowanie na daną przerwę jest aktualnie zamknięte.
        /// Gdy włączona jest opcja Voting:AllowVotingOutsideSchedule (np. w Development), głosowanie jest zawsze otwarte.
        /// W trybie produkcyjnym głosowanie jest zablokowane wyłącznie w oknie od VoteEnds (lub StartAt) do EndsAt bieżącego dnia.
        /// </summary>
        private bool IsVotingClosedForBreak(Break breakEntity, DateTime now)
        {
            bool allowOutside = _configuration?.GetValue<bool>("Voting:AllowVotingOutsideSchedule") ?? false;
            if (allowOutside)
            {
                return false;
            }

            var today = DateOnly.FromDateTime(now);
            var startAt = today.ToDateTime(TimeOnly.FromDateTime(breakEntity.StartAt));
            var endsAt = today.ToDateTime(TimeOnly.FromDateTime(breakEntity.EndsAt));
            if (endsAt <= startAt)
            {
                endsAt = endsAt.AddDays(1);
            }

            var voteEndTime = breakEntity.VoteEnds.HasValue
                ? today.ToDateTime(TimeOnly.FromDateTime(breakEntity.VoteEnds.Value))
                : startAt;

            // Okno zamkniętego głosowania to czas od voteEndTime do endsAt
            if (now >= voteEndTime && now < endsAt)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Wysyła zaktualizowaną listę utworów dla podanego breakId do klientów w grupie SignalR
        /// </summary>
        /// <remarks>Pobiera zatwierdzone (lub bez statusu moderacji) elementy kolejki wraz z powiązanymi
        /// danymi, sortuje po liczbie głosów i indeksie kolejności, a następnie emituje zdarzenie 'QueueUpdated' do
        /// grupy 'Break_{breakId}' z liczbą utworów i rankingiem</remarks>
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
        }

        /// <summary>
        /// Zwraca uporządkowaną kolejkę utworów dla podanej przerwy wraz z metadanymi utworów i informacją o głosach
        /// </summary>
        /// <remarks>Uwzględniane są tylko elementy z ModerationStatus równym 'approved' lub null.
        /// Kolekcja jest sortowana najpierw malejąco po liczbie głosów, następnie rosnąco po OrderIndex. Pole
        /// artistName zwraca 'Nieznany wykonawca' jeśli brak wykonawcy</remarks>
        /// <returns>Task z ServiceResult zawierającym obiekt ze szczegółami przerwy, łączną liczbą utworów i listą elementów
        /// kolejki; zwraca NotFound, jeśli przerwa nie istnieje.</returns>
        public async Task<ServiceResult<object>> GetQueueForBreakAsync(int breakId, int? currentUserId)
        {
            var breakEntity = await _context.Breaks.FindAsync(breakId);
            if (breakEntity == null)
            {
                // Fallback: jeśli podana przerwa nie istnieje, spróbuj użyć pierwszej dostępnej
                breakEntity = await _context.Breaks.OrderBy(b => b.BreakNumber).FirstOrDefaultAsync();
                if (breakEntity == null)
                {
                    return ServiceResult<object>.NotFound("Nie znaleziono przerwy o podanym identyfikatorze.");
                }
                breakId = breakEntity.Id;
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
                    submittedBy = q.User?.UserLogin ?? "Uczeń",
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

        /// <summary>
        /// Zgłasza utwór do przerwy radiowej; wykonuje walidacje (blokada użytkownika, istnienie utworu, czarna lista,
        /// cooldown, otwarte głosowanie) i albo automatycznie zatwierdza utwór z greenlisty, albo umieszcza propozycję
        /// w kolejce do weryfikacji moderatorów
        /// </summary>
        /// <returns>ServiceResult&lt;object&gt; z odpowiednim statusem i danymi: Forbidden gdy użytkownik zablokowany; NotFound gdy
        /// utwór lub przerwa nie istnieje; BadRequest dla nieprawidłowych warunków (czarna lista, cooldown, zamknięte
        /// głosowanie, brak przerw); Conflict gdy propozycja już istnieje; Created gdy utwór z greenlisty został
        /// zatwierdzony; Accepted gdy propozycja przesłana do moderacji</returns>
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

            // Fallback: wyszukanie po tytule i opcjonalnie autorze
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

                // Jeśli utwór nadal nie istnieje, a podano tytuł i wykonawcę (np. zgłoszenie z panelu WWW)
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

            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            if (lastPlayed != null && lastPlayed.PlayedAt > utcNow.AddHours(-SongCooldownHours))
            {
                var remaining = lastPlayed.PlayedAt.AddHours(SongCooldownHours) - utcNow;
                var hours = Math.Max(1, (int)Math.Ceiling(remaining.TotalHours));
                return ServiceResult<object>.BadRequest($"Ten utwór był niedawno odtwarzany na radiowęźle ({lastPlayed.PlayedAt:dd.MM HH:mm}). Będzie dostępny do ponownego zgłoszenia za ok. {hours} godz.");
            }

            // Ustalenie przerwy docelowej
            int targetBreakId;
            var now = _timeProvider.GetLocalNow().DateTime;
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
                var allBreaks = await _context.Breaks.OrderBy(b => b.BreakNumber).ToListAsync();
                var candidate = allBreaks.FirstOrDefault(b => !IsVotingClosedForBreak(b, now));

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
            if (dto.BreakId.HasValue && IsVotingClosedForBreak(targetBreakEntity, now))
            {
                return ServiceResult<object>.BadRequest("Głosowanie na tę przerwę zostało już zamknięte.");
            }

            await using var mutationLock = await _mutationLock.AcquireAsync(targetBreakId);

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
                    submittedAt = _timeProvider.GetUtcNow().UtcDateTime
                };

                var deduplicationKey = PendingProposalStore.GetDeduplicationKey(targetBreakId, song.Id);
                var reserved = await db.StringSetAsync(
                    deduplicationKey,
                    proposal.id,
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
                    proposal.id,
                    song.Id,
                    targetBreakId);

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
        /// <remarks>Może dodać pozycję do kolejki z zielonej listy (z zachowaniem cooldownu), weryfikuje
        /// blokady użytkownika i termin głosowania, zapobiega podwójnemu głosowaniu oraz emituje aktualizację
        /// kolejki</remarks>
        /// <returns>Task zawierający ServiceResult z odpowiednim statusem. Przy powodzeniu zwraca obiekt z polami: Message,
        /// queueItemId, votesCount i hasVoted; w innych przypadkach zwraca Forbidden, NotFound, BadRequest lub Conflict
        /// z opisem błędu</returns>
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

                    if (IsVotingClosedForBreak(breakEntity, _timeProvider.GetLocalNow().DateTime))
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
                            // Sprawdzenie cooldownu przed automatycznym dodaniem z Greenlisty
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

            return ServiceResult<object>.Ok(new
            {
                Message = "Głos został pomyślnie oddany!",
                queueItemId = targetQueueItemId,
                votesCount = totalVotes,
                hasVoted = true
            });
        }

        /// <summary>
        /// Usuwa głos użytkownika dla wskazanego elementu kolejki, zapisuje zmiany 
        /// oraz powiadamia klientów o aktualizacji kolejki
        /// </summary>
        /// <returns>ServiceResult zawierający wynik operacji: NotFound, jeśli głos nie istnieje, lub Ok z danymi (wiadomość,
        /// queueItemId, votesCount, hasVoted) po pomyślnym usunięciu</returns>
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
                    IsVotingClosedForBreak(vote.QueueItem.Break, _timeProvider.GetLocalNow().DateTime))
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

            return ServiceResult<object>.Ok(new
            {
                Message = "Głos został pomyślnie cofnięty.",
                queueItemId,
                votesCount = remainingVotes,
                hasVoted = false
            });
        }

        /// <summary>
        /// Archiwizuje przerwę o podanym identyfikatorze: zapisuje odtworzone utwory do historii, czyści kolejkę
        /// (usuwając pozycje i powiązane głosy) oraz wysyła powiadomienie SignalR
        /// </summary>
        /// <returns>ServiceResult zawierający obiekt z informacjami o wyniku operacji (komunikat, breakId, breakNumber, liczby
        /// zarchiwizowanych i wyczyszczonych utworów oraz lista zarchiwizowanych utworów) lub wynik błędu (np. NotFound
        /// gdy przerwa nie istnieje)</returns>
        public async Task<ServiceResult<object>> ArchiveBreakAsync(int breakId)
        {
            sbyte breakNumber;
            var playedTracksInfo = new List<object>();
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

                // Pobieramy wszystkie zatwierdzone pozycje z kolejki tej przerwy
                var approvedQueueItems = await _context.QueueItems
                    .Where(q => q.BreakId == breakId && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
                    .Include(q => q.Song)
                        .ThenInclude(s => s.Artist)
                    .Include(q => q.Votes)
                    .ToListAsync();
                var playedItems = approvedQueueItems
                    .Where(item => item.Status == QueueItemStatuses.Played || item.Status == QueueItemStatuses.Playing)
                    .ToList();

                // Status kolejki jest aktualizowany przez odpytywanie Spotify; archiwizujemy tylko rozpoczęte utwory.
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

                    playedTracksInfo.Add(new
                    {
                        songId = played.SongId,
                        title = played.Song.Title,
                        artist = played.Song.Artist?.Name ?? "Nieznany wykonawca",
                        votes = played.Votes.Count
                    });
                }

                // Usuwamy wszystkie QueueItems i powiązane głosy dla tej przerwy (czyścimy kolejkę)
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

            // Rozsyłamy powiadomienie SignalR (kolejka jest teraz wyczyszczona)
            await BroadcastQueueAsync(breakId);
            _logger?.LogInformation(
                "Zarchiwizowano przerwę {BreakId}: {ArchivedCount} odtworzonych i {ClearedCount} usuniętych pozycji.",
                breakId,
                playedTracksInfo.Count,
                totalClearedTracks);

            return ServiceResult<object>.Ok(new
            {
                Message = $"Przerwa #{breakNumber} została pomyślnie zarchiwizowana. Zapisano historię i wyczyszczono kolejkę.",
                breakId,
                breakNumber,
                archivedTracksCount = playedTracksInfo.Count,
                totalClearedTracks,
                archivedTracks = playedTracksInfo
            });
        }
    }
}
