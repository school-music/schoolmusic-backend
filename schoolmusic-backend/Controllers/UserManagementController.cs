using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers
{
    public record UpdateUserRoleDto(int RankId);

    public record UserListItemDto(
        int Id,
        string UserLogin,
        int RankId,
        string RankName,
        DateTime CreatedAt,
        DateTime? PasswordChangedAt,
        string? DisplayName = null,
        string? Email = null,
        bool EmailVerified = false,
        string AuthProvider = "local"
    );

    public sealed record UsersPagedResponseDto(
        int TotalCount,
        int Page,
        int PageSize,
        int TotalPages,
        IReadOnlyList<UserListItemDto> Users
    );

    public sealed record UpdateUserRoleResponseDto(
        string Message,
        int UserId,
        string UserLogin,
        int RankId,
        string RankName,
        int TokenVersion
    );

    /// <summary>
    /// Kontroler API do zarządzania kontami użytkowników i uprawnieniami (dostęp dla administratorów)
    /// </summary>
    [Route("api/users")]
    [ApiController]
    [Authorize]
    [Produces("application/json")]
    public class UserManagementController : ControllerBase
    {
        private readonly schoolmusicContext _context;
        private readonly ISessionTokenService? _sessionTokenService;
        private readonly IAuthSecurityStampService? _securityStampService;
        private readonly IPasswordRecoveryService? _passwordRecoveryService;

        public UserManagementController(
            schoolmusicContext context,
            ISessionTokenService? sessionTokenService = null,
            IAuthSecurityStampService? securityStampService = null,
            IPasswordRecoveryService? passwordRecoveryService = null)
        {
            _context = context;
            _sessionTokenService = sessionTokenService;
            _securityStampService = securityStampService;
            _passwordRecoveryService = passwordRecoveryService;
        }

        /// <summary>
        /// Zwraca stronicowaną listę użytkowników systemu (bez haseł)
        /// </summary>
        [HttpGet]
        [Authorize(Roles = "admin,head_admin")]
        [ProducesResponseType<UsersPagedResponseDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetUsers(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? search = null)
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 100) pageSize = 20;

            var query = _context.Users
                .Include(u => u.Rank)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(u =>
                    u.UserLogin.Contains(term) ||
                    (u.DisplayName != null && u.DisplayName.Contains(term)) ||
                    (u.Email != null && u.Email.Contains(term)));
            }

            int totalCount = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

            var users = await query
                .OrderBy(u => u.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(u => new UserListItemDto(
                    u.Id,
                    u.UserLogin,
                    u.RankId,
                    u.Rank.Rank1,
                    u.CreatedAt,
                    u.PasswordChangedAt,
                    u.DisplayName,
                    u.Email,
                    u.EmailVerifiedAt != null,
                    u.AuthProvider
                ))
                .ToListAsync();

            return Ok(new UsersPagedResponseDto(
                TotalCount: totalCount,
                Page: page,
                PageSize: pageSize,
                TotalPages: totalPages,
                Users: users
            ));
        }

        /// <summary>
        /// Zmienia rolę (rangę) wskazanego użytkownika i przy faktycznej zmianie unieważnia jego sesje.
        /// Dostępne wyłącznie dla rangi head_admin.
        /// </summary>
        [HttpPut("{id:int}/role")]
        [Authorize(Roles = "head_admin")]
        [ProducesResponseType<UpdateUserRoleResponseDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> UpdateUserRole(int id, [FromBody] UpdateUserRoleDto dto)
        {
            int? currentUserId = HttpContext.GetCurrentUserId();
            if (currentUserId == null)
            {
                return this.Create(StatusCodes.Status401Unauthorized, "Nieprawidłowy token uwierzytelniający.");
            }

            if (currentUserId.Value == id)
            {
                return this.Create(StatusCodes.Status400BadRequest, "Nie możesz zmienić własnej roli.");
            }

            if (dto.RankId < 1 || dto.RankId > 4)
            {
                return this.Create(StatusCodes.Status400BadRequest, "Nieprawidłowy identyfikator rangi (dozwolone wartości: 1 = user, 2 = mod, 3 = admin, 4 = head_admin).");
            }

            var user = await _context.Users
                .Include(u => u.Rank)
                .FirstOrDefaultAsync(u => u.Id == id);

            if (user == null)
            {
                return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono użytkownika o podanym identyfikatorze.");
            }

            var targetRank = await _context.Ranks.FindAsync(dto.RankId);
            if (targetRank == null)
            {
                return this.Create(StatusCodes.Status400BadRequest, "Podana ranga nie istnieje w bazie danych.");
            }

            bool roleActuallyChanged = user.RankId != dto.RankId;
            user.RankId = dto.RankId;

            // Synchronizacja tabeli moderators
            if (dto.RankId >= 2) // mod, admin, head_admin
            {
                bool isModeratorRecord = await _context.Moderators.AnyAsync(m => m.UserId == id);
                if (!isModeratorRecord)
                {
                    _context.Moderators.Add(new Moderator
                    {
                        UserId = id,
                        ArtistsBanned = 0,
                        SongsBanned = 0,
                        SongsApproved = 0
                    });
                }
            }
            else // degradacja do zwykłego użytkownika (1)
            {
                var modRecord = await _context.Moderators.FirstOrDefaultAsync(m => m.UserId == id);
                if (modRecord != null)
                {
                    bool hasDependencies = await _context.QueueItems.AnyAsync(q => q.ModeratorId == modRecord.Id)
                        || await _context.SongBlacklists.AnyAsync(b => b.ModId == modRecord.Id)
                        || await _context.SongGreenlists.AnyAsync(g => g.ModId == modRecord.Id)
                        || await _context.ArtistBlacklists.AnyAsync(a => a.ModeratorId == modRecord.Id)
                        || await _context.BannedUsers.AnyAsync(u => u.ModeratorId == modRecord.Id);

                    if (!hasDependencies)
                    {
                        _context.Moderators.Remove(modRecord);
                    }
                }
            }

            await _context.SaveChangesAsync();

            if (roleActuallyChanged)
            {
                if (_sessionTokenService != null)
                {
                    await _sessionTokenService.RevokeAllUserSessionsAsync(user);
                }
                else
                {
                    user.TokenVersion += 1;
                    await _context.SaveChangesAsync();
                    if (_securityStampService != null)
                    {
                        await _securityStampService.InvalidateUserAsync(user.Id);
                    }
                }
            }

            return Ok(new UpdateUserRoleResponseDto(
                Message: $"Zaktualizowano rolę użytkownika {user.UserLogin} na {targetRank.Rank1}.",
                UserId: user.Id,
                UserLogin: user.UserLogin,
                RankId: targetRank.Id,
                RankName: targetRank.Rank1,
                TokenVersion: user.TokenVersion
            ));
        }

        /// <summary>
        /// Wystawia jednorazowy, krótkotrwały link odzyskiwania hasła dla konta lokalnego bez zweryfikowanego adresu e-mail.
        /// </summary>
        [HttpPost("{id:int}/reset-password")]
        [Authorize(Roles = "admin,head_admin")]
        [ProducesResponseType<AdminIssuedRecoveryDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> IssueAdminPasswordRecovery(int id, CancellationToken cancellationToken = default)
        {
            if (_passwordRecoveryService == null)
            {
                return this.Create(StatusCodes.Status503ServiceUnavailable, "Usługa odzyskiwania hasła jest niedostępna.");
            }

            int? callerId = HttpContext.GetCurrentUserId();
            if (callerId == null)
            {
                return this.Create(StatusCodes.Status401Unauthorized, "Nieprawidłowy token uwierzytelniający.");
            }

            var caller = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == callerId.Value, cancellationToken);
            if (caller == null)
            {
                return this.Create(StatusCodes.Status401Unauthorized, "Nieprawidłowy token uwierzytelniający.");
            }

            var result = await _passwordRecoveryService.IssueAdminAssistedRecoveryAsync(id, caller.Id, caller.RankId, cancellationToken);
            if (!result.Success || result.Data == null)
            {
                return this.Create(result.StatusCode, result.Message ?? "Nie udało się wygenerować odzyskiwania hasła.");
            }

            return Ok(result.Data);
        }
    }
}
