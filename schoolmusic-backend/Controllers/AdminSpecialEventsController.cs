using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers
{
    public record SpecialEventDto(
        int Id,
        string? Title,
        string FileUrl,
        DateTime StartsAt,
        string EventStatus
    );

    public record CreateSpecialEventDto(
        string? Title,
        string FileUrl,
        DateTime StartsAt,
        string? EventStatus = "pending"
    );

    public record UpdateSpecialEventDto(
        string? Title,
        string FileUrl,
        DateTime StartsAt,
        string EventStatus
    );

    public sealed record DeleteSpecialEventResponseDto(
        string Message,
        int Id
    );

    /// <summary>
    /// Kontroler panelu administracyjnego do zarządzania wydarzeniami specjalnymi (dzwonki, apele, audycje specjalne).
    /// </summary>
    [Route("api/admin/special-events")]
    [ApiController]
    [Authorize(Roles = "admin,head_admin")]
    [Produces("application/json")]
    public class AdminSpecialEventsController : ControllerBase
    {
        private readonly schoolmusicContext _context;
        private readonly IBreakSchedulerService? _scheduler;
        private readonly TimeProvider _timeProvider;
        private static readonly string[] ValidStatuses = { "pending", "playing", "done" };

        public AdminSpecialEventsController(
            schoolmusicContext context,
            IBreakSchedulerService? scheduler = null,
            TimeProvider? timeProvider = null)
        {
            _context = context;
            _scheduler = scheduler;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>
        /// Pobiera listę wszystkich wydarzeń specjalnych z opcjonalnym filtrowaniem po statusie.
        /// </summary>
        [HttpGet]
        [ProducesResponseType<IReadOnlyList<SpecialEventDto>>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetSpecialEvents([FromQuery] string? status = null)
        {
            var query = _context.SpecialEvents.AsQueryable();

            if (!string.IsNullOrWhiteSpace(status))
            {
                var trimmedStatus = status.Trim().ToLowerInvariant();
                query = query.Where(e => e.EventStatus == trimmedStatus);
            }

            var events = await query
                .OrderBy(e => e.StartsAt)
                .Select(e => new SpecialEventDto(
                    e.Id,
                    e.Title,
                    e.FileUrl,
                    e.StartsAt,
                    e.EventStatus
                ))
                .ToListAsync();

            return Ok(events);
        }

        /// <summary>
        /// Pobiera szczegóły pojedynczego wydarzenia specjalnego.
        /// </summary>
        [HttpGet("{id:int}")]
        [ProducesResponseType<SpecialEventDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetSpecialEventById(int id)
        {
            var ev = await _context.SpecialEvents.FindAsync(id);
            if (ev == null)
            {
                return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono wydarzenia specjalnego o podanym ID.");
            }

            return Ok(new SpecialEventDto(
                ev.Id,
                ev.Title,
                ev.FileUrl,
                ev.StartsAt,
                ev.EventStatus
            ));
        }

        /// <summary>
        /// Tworzy nowe zaplanowane wydarzenie specjalne.
        /// </summary>
        [HttpPost]
        [ProducesResponseType<SpecialEventDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> CreateSpecialEvent([FromBody] CreateSpecialEventDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.FileUrl))
            {
                return this.Create(StatusCodes.Status400BadRequest, "Adres URL pliku dźwiękowego (FileUrl) jest wymagany.");
            }

            var status = string.IsNullOrWhiteSpace(dto.EventStatus)
                ? "pending"
                : dto.EventStatus.Trim().ToLowerInvariant();

            if (!ValidStatuses.Contains(status))
            {
                return this.Create(StatusCodes.Status400BadRequest, "Nieprawidłowy status wydarzenia. Dozwolone: 'pending', 'playing', 'done'.");
            }

            var ev = new SpecialEvent
            {
                Title = string.IsNullOrWhiteSpace(dto.Title) ? null : dto.Title.Trim(),
                FileUrl = dto.FileUrl.Trim(),
                StartsAt = ToSchoolLocalTime(dto.StartsAt),
                EventStatus = status
            };

            _context.SpecialEvents.Add(ev);
            await _context.SaveChangesAsync();
            _scheduler?.NotifyScheduleChanged();

            var resultDto = new SpecialEventDto(
                ev.Id,
                ev.Title,
                ev.FileUrl,
                ev.StartsAt,
                ev.EventStatus
            );

            return CreatedAtAction(nameof(GetSpecialEventById), new { id = ev.Id }, resultDto);
        }

        /// <summary>
        /// Aktualizuje istniejące wydarzenie specjalne.
        /// </summary>
        [HttpPut("{id:int}")]
        [ProducesResponseType<SpecialEventDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> UpdateSpecialEvent(int id, [FromBody] UpdateSpecialEventDto dto)
        {
            var ev = await _context.SpecialEvents.FindAsync(id);
            if (ev == null)
            {
                return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono wydarzenia specjalnego o podanym ID.");
            }

            if (string.IsNullOrWhiteSpace(dto.FileUrl))
            {
                return this.Create(StatusCodes.Status400BadRequest, "Adres URL pliku dźwiękowego (FileUrl) jest wymagany.");
            }

            var status = dto.EventStatus.Trim().ToLowerInvariant();
            if (!ValidStatuses.Contains(status))
            {
                return this.Create(StatusCodes.Status400BadRequest, "Nieprawidłowy status wydarzenia. Dozwolone: 'pending', 'playing', 'done'.");
            }

            ev.Title = string.IsNullOrWhiteSpace(dto.Title) ? null : dto.Title.Trim();
            ev.FileUrl = dto.FileUrl.Trim();
            ev.StartsAt = ToSchoolLocalTime(dto.StartsAt);
            ev.EventStatus = status;

            await _context.SaveChangesAsync();
            _scheduler?.NotifyScheduleChanged();

            return Ok(new SpecialEventDto(
                ev.Id,
                ev.Title,
                ev.FileUrl,
                ev.StartsAt,
                ev.EventStatus
            ));
        }

        /// <summary>
        /// Usuwa wydarzenie specjalne.
        /// </summary>
        [HttpDelete("{id:int}")]
        [ProducesResponseType<DeleteSpecialEventResponseDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> DeleteSpecialEvent(int id)
        {
            var ev = await _context.SpecialEvents.FindAsync(id);
            if (ev == null)
            {
                return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono wydarzenia specjalnego o podanym ID.");
            }

            _context.SpecialEvents.Remove(ev);
            await _context.SaveChangesAsync();
            _scheduler?.NotifyScheduleChanged();

            return Ok(new DeleteSpecialEventResponseDto("Pomyślnie usunięto wydarzenie specjalne.", id));
        }

        private DateTime ToSchoolLocalTime(DateTime value)
        {
            if (value.Kind == DateTimeKind.Unspecified)
                return value;
            if (value.Kind == DateTimeKind.Utc)
                return TimeZoneInfo.ConvertTimeFromUtc(value, _timeProvider.LocalTimeZone);

            return TimeZoneInfo.ConvertTime(new DateTimeOffset(value), _timeProvider.LocalTimeZone).DateTime;
        }
    }
}
