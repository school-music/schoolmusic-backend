using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers
{
    public sealed record ReorderQueueResponseDto(
        string Message,
        int BreakId,
        IReadOnlyList<int> NewOrder
    );

    /// <summary>
    /// Kontroler API obsługujący kolejkę utworów: proponowanie piosenek, głosowanie i cofanie głosów, pobieranie listy
    /// utworów dla przerwy, wyznaczanie docelowej przerwy do głosowania, generowanie harmonogramu przerwy,
    /// pobieranie aktualnie odtwarzanego utworu oraz archiwizację przerw.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    [Produces("application/json")]
    public class QueueController : ControllerBase
    {
        private readonly IQueueService _queueService;
        private readonly IRadioStatusService _radioStatusService;
        private readonly IBreakService _breakService;
        private readonly schoolmusicContext _context;
        private readonly IVotingTargetService _votingTargetService;
        private readonly ILogger<QueueController>? _logger;

        public QueueController(
            IQueueService queueService,
            IRadioStatusService radioStatusService,
            IBreakService breakService,
            schoolmusicContext context,
            ILogger<QueueController>? logger = null,
            IVotingTargetService? votingTargetService = null)
        {
            _queueService = queueService;
            _radioStatusService = radioStatusService;
            _breakService = breakService;
            _context = context;
            _logger = logger;
            _votingTargetService = votingTargetService ?? new VotingTargetService(context);
        }

        private IActionResult FromResult<T>(ServiceResult<T> result)
        {
            if (result.Success)
            {
                return StatusCode(result.StatusCode, result.Data);
            }

            return this.Create(result.StatusCode, result.Message ?? "Wystąpił błąd.");
        }

        /// <summary>
        /// Zwraca przerwę kwalifikującą się do głosowania, czas odcięcia ze strefą czasową oraz powód otwarcia/zamknięcia.
        /// </summary>
        [HttpGet("voting-target")]
        [AllowAnonymous]
        [ProducesResponseType<VotingTargetDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetVotingTarget(
            [FromQuery] int? breakId = null,
            CancellationToken cancellationToken = default)
        {
            var target = await _votingTargetService.GetVotingTargetAsync(breakId, cancellationToken);
            if (breakId.HasValue && target.ReasonCode == "break_not_found")
            {
                return this.Create(StatusCodes.Status404NotFound, target.Reason);
            }

            return Ok(target);
        }

        /// <summary>
        /// Pobiera listę utworów zgłoszonych na daną przerwę (Live ranking) wraz ze statusem kolejki,
        /// głosem bieżącego użytkownika i metadanymi Spotify.
        /// Obsługuje zarówno /api/queue/break/{breakId}, jak i /api/queue?breakId={breakId}.
        /// </summary>
        [HttpGet]
        [HttpGet("break/{breakId:int}")]
        [AllowAnonymous]
        [ProducesResponseType<QueueForBreakResponseDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetQueueForBreak(
            [FromRoute] int? breakId = null,
            [FromQuery(Name = "breakId")] int? queryBreakId = null,
            CancellationToken cancellationToken = default)
        {
            var effectiveBreakId = breakId ?? queryBreakId;
            if (!effectiveBreakId.HasValue)
            {
                var target = await _votingTargetService.GetVotingTargetAsync(null, cancellationToken);
                if (target.EligibleBreakId.HasValue)
                {
                    effectiveBreakId = target.EligibleBreakId.Value;
                }
                else if (target.BreakId.HasValue)
                {
                    effectiveBreakId = target.BreakId.Value;
                }
                else
                {
                    var allBreaks = await _context.Breaks.OrderBy(b => b.BreakNumber).ToListAsync(cancellationToken);
                    effectiveBreakId = allBreaks.FirstOrDefault()?.Id ?? 1;
                }
            }

            int? currentUserId = HttpContext.GetCurrentUserId();
            var result = await _queueService.GetQueueForBreakAsync(effectiveBreakId.Value, currentUserId);
            return FromResult(result);
        }

        /// <summary>
        /// Zwraca ułożony przez algorytm harmonogram utworów na daną przerwę (z uwzględnieniem czasu jej trwania)
        /// </summary>
        [HttpGet("break/{breakId:int}/plan")]
        [AllowAnonymous]
        [ProducesResponseType<ScheduledBreakPlan>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetBreakPlan(int breakId)
        {
            var breakEntity = await _context.Breaks.FindAsync(breakId);
            if (breakEntity == null)
            {
                return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono przerwy o podanym identyfikatorze.");
            }

            DateTime end = breakEntity.EndsAt;
            int durationSeconds = (int)(end - breakEntity.StartAt).TotalSeconds;

            if (durationSeconds <= 0)
            {
                durationSeconds = 600; // Domyślnie 10 minut
            }

            var items = await _context.QueueItems
                .Where(q => q.BreakId == breakId && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
                .Include(q => q.Song)
                    .ThenInclude(s => s.Artist)
                .Include(q => q.Votes)
                .ToListAsync();

            var plan = _breakService.CalculateBreak(breakId, durationSeconds, items);
            return Ok(plan);
        }

        /// <summary>
        /// Zwraca utwór aktualnie odtwarzany na radiowęźle (dla ekranu TV i paska odtwarzania w aplikacji)
        /// </summary>
        [HttpGet("now-playing")]
        [AllowAnonymous]
        [ProducesResponseType<RadioNowPlayingResponseDto>(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetNowPlaying()
        {
            var status = await _radioStatusService.GetNowPlayingDtoAsync();
            return Ok(status);
        }

        /// <summary>
        /// Zgłasza piosenkę przez zalogowanego ucznia na najbliższą (lub wybraną) przerwę.
        /// Obsługuje zarówno /api/queue/propose, jak i bezpośrednie /api/queue.
        /// </summary>
        [HttpPost]
        [HttpPost("propose")]
        [ProducesResponseType<ProposeSongApprovedDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProposeSongPendingDto>(StatusCodes.Status202Accepted)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> ProposeSong([FromBody] ProposeSongDto dto)
        {
            int? currentUserId = HttpContext.GetCurrentUserId();
            if (currentUserId == null)
            {
                return this.Create(StatusCodes.Status401Unauthorized, "Musisz być zalogowany, aby zgłosić piosenkę.");
            }

            var result = await _queueService.ProposeSongAsync(dto, currentUserId.Value);
            return FromResult(result);
        }

        /// <summary>
        /// Oddaje głos na utwór w kolejce (lub dodaje utwór z greenlisty i oddaje na niego głos)
        /// </summary>
        [HttpPost("vote")]
        [ProducesResponseType<VoteResponseDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Vote([FromBody] VoteRequestDto dto)
        {
            int? currentUserId = HttpContext.GetCurrentUserId();
            if (currentUserId == null)
            {
                return this.Create(StatusCodes.Status401Unauthorized, "Musisz być zalogowany, aby oddać głos.");
            }

            var result = await _queueService.VoteAsync(dto, currentUserId.Value);
            return FromResult(result);
        }

        /// <summary>
        /// Cofa oddany wcześniej głos na utwór w kolejce
        /// </summary>
        [HttpDelete("vote/{queueItemId:int}")]
        [ProducesResponseType<VoteResponseDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> RemoveVote(int queueItemId)
        {
            int? currentUserId = HttpContext.GetCurrentUserId();
            if (currentUserId == null)
            {
                return this.Create(StatusCodes.Status401Unauthorized, "Musisz być zalogowany, aby cofnąć głos.");
            }

            var result = await _queueService.RemoveVoteAsync(queueItemId, currentUserId.Value);
            return FromResult(result);
        }

        /// <summary>
        /// Zamyka przerwę, przenosi utwory, które zagrały, do tabeli historii i czyści kolejkę
        /// Dostęp tylko dla moderatorów i administratorów
        /// </summary>
        [HttpPost("break/{breakId:int}/archive")]
        [Authorize(Roles = "mod,admin,head_admin")]
        [ProducesResponseType<ArchiveBreakResponseDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> ArchiveBreak(int breakId)
        {
            var result = await _queueService.ArchiveBreakAsync(breakId);
            return FromResult(result);
        }

        /// <summary>
        /// Zmienia kolejność utworów w kolejce danej przerwy
        /// </summary>
        [HttpPut("break/{breakId:int}/order")]
        [Authorize(Roles = "mod,admin,head_admin")]
        [ProducesResponseType<ReorderQueueResponseDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> ReorderQueue(int breakId, [FromBody] ReorderQueueDto dto)
        {
            if (dto.QueueItemIds == null || dto.QueueItemIds.Count == 0)
            {
                return this.Create(StatusCodes.Status400BadRequest, "Lista identyfikatorów utworów nie może być pusta.");
            }

            var items = await _context.QueueItems
                .Where(q => q.BreakId == breakId)
                .ToListAsync();

            if (items.Count == 0)
            {
                return this.Create(StatusCodes.Status404NotFound, "Brak utworów w kolejce dla podanej przerwy.");
            }

            var itemDict = items.ToDictionary(q => q.Id);
            foreach (var id in dto.QueueItemIds)
            {
                if (!itemDict.ContainsKey(id))
                {
                    return this.Create(StatusCodes.Status400BadRequest, $"Utwór o identyfikatorze {id} nie należy do przerwy {breakId}.");
                }
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                for (int i = 0; i < dto.QueueItemIds.Count; i++)
                {
                    itemDict[dto.QueueItemIds[i]].OrderIndex = -(i + 1);
                }
                await _context.SaveChangesAsync();

                for (int i = 0; i < dto.QueueItemIds.Count; i++)
                {
                    itemDict[dto.QueueItemIds[i]].OrderIndex = i + 1;
                }
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger?.LogError(ex,
                    "Nie udało się zmienić kolejności utworów w kolejce przerwy {BreakId}.",
                    breakId);
                return this.Create(StatusCodes.Status500InternalServerError, "Nie udało się zmienić kolejności utworów. Spróbuj ponownie.");
            }

            await _queueService.BroadcastQueueAsync(breakId);

            return Ok(new ReorderQueueResponseDto(
                Message: "Zaktualizowano kolejność utworów w kolejce.",
                BreakId: breakId,
                NewOrder: dto.QueueItemIds
            ));
        }
    }

    public record ReorderQueueDto(List<int> QueueItemIds);
}
