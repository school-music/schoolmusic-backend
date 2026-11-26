using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using schoolmusic_backend.Configuration;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace schoolmusic_backend.Controllers;

/// <summary>
/// Kontroler API odpowiedzialny za uwierzytelnianie użytkowników (logowanie lokalne oraz przez szkolne konto LDAP),
/// rejestrację uczniów, profil własny, weryfikację adresu e-mail, odzyskiwanie hasła oraz zarządzanie sesjami.
/// </summary>
[Route("api/auth")]
[ApiController]
[Authorize]
[Produces("application/json")]
public class UsersController : ControllerBase
{
    public const int MaxDisplayNameLength = 80;

    private readonly schoolmusicContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILdapService _ldapService;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ISessionTokenService? _sessionTokenService;
    private readonly IAuthSecurityStampService? _securityStampService;
    private readonly IEmailVerificationService? _emailVerificationService;
    private readonly IPasswordRecoveryService? _passwordRecoveryService;
    private readonly IOptions<AuthOptions>? _authOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UsersController>? _logger;

    public UsersController(
        schoolmusicContext context,
        IConfiguration configuration,
        ILdapService ldapService,
        IJwtTokenService jwtTokenService,
        ILogger<UsersController>? logger = null,
        ISessionTokenService? sessionTokenService = null,
        IAuthSecurityStampService? securityStampService = null,
        IEmailVerificationService? emailVerificationService = null,
        IPasswordRecoveryService? passwordRecoveryService = null,
        IOptions<AuthOptions>? authOptions = null,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _configuration = configuration;
        _ldapService = ldapService;
        _jwtTokenService = jwtTokenService;
        _logger = logger;
        _sessionTokenService = sessionTokenService;
        _securityStampService = securityStampService;
        _emailVerificationService = emailVerificationService;
        _passwordRecoveryService = passwordRecoveryService;
        _authOptions = authOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public class UserLoginDto
    {
        /// <summary>Nazwa użytkownika (login lokalny, zweryfikowany e-mail lub login szkolny imie.nazwisko).</summary>
        public string name { get; set; } = string.Empty;
        /// <summary>Hasło użytkownika.</summary>
        public string password { get; set; } = string.Empty;
    }

    public class SchoolLoginDto
    {
        /// <summary>Login szkolny (np. imie.nazwisko lub email szkolny).</summary>
        public string login { get; set; } = string.Empty;
        /// <summary>Hasło do szkolnego konta.</summary>
        public string password { get; set; } = string.Empty;
    }

    public class UserRegisterDto
    {
        /// <summary>Nazwa użytkownika (min. 3 znaki).</summary>
        public string name { get; set; } = string.Empty;
        /// <summary>Hasło użytkownika (min. 8 znaków).</summary>
        public string password { get; set; } = string.Empty;
        /// <summary>Opcjonalna nazwa wyświetlana w aplikacji (maks. 80 znaków).</summary>
        public string? displayName { get; set; }
        /// <summary>Opcjonalny adres e-mail konta lokalnego (wymaga weryfikacji przed logowaniem i resetem hasła).</summary>
        public string? email { get; set; }
    }

    public sealed record UpdateProfileDto(string? displayName);
    public sealed record AttachEmailDto(string email);
    public sealed record VerifyEmailDto(string token);
    public sealed record ResendEmailVerificationDto(string identifier);
    public sealed record RefreshTokenRequestDto(string refreshToken);
    public sealed record LogoutRequestDto(string? refreshToken = null);
    public sealed record PasswordRecoveryRequestDto(string identifier);
    public sealed record PasswordRecoveryConfirmDto(string token, string newPassword);
    public sealed record ChangePasswordDto(string currentPassword, string newPassword);

    public record LoginResponseDto(
        string Message,
        string jwtSecurityToken,
        string role,
        int userId,
        string username,
        bool isLdap,
        string? displayName = null,
        string? email = null,
        bool emailVerified = false,
        string authProvider = "local",
        string? refreshToken = null,
        DateTime? expiresAtUtc = null,
        DateTime? refreshTokenExpiresAtUtc = null
    );

    public sealed record RegisterResponseDto(
        int Id,
        string Username,
        string? DisplayName,
        string? Email,
        bool EmailVerificationRequired,
        string Message
    );

    public sealed record UserProfileDto(
        int UserId,
        string Username,
        string? DisplayName,
        string? Email,
        bool EmailVerified,
        string Role,
        int RankId,
        bool IsLdap,
        string AuthProvider,
        DateTime CreatedAt,
        DateTime? PasswordChangedAt
    );

    public sealed record AuthCapabilitiesDto(
        bool AllowLocalRegistration,
        bool EmailLoginEnabled,
        bool LdapEnabled,
        bool PasswordRecoveryEnabled,
        string? SchoolIdentityProviderUrl,
        string SchoolRecoveryGuidance
    );

    public sealed record AuthMessageResponseDto(string Message);

    private bool IsEmailLoginEnabled =>
        _authOptions?.Value.EmailLoginEnabled ?? _configuration.GetValue("Auth:EmailLoginEnabled", true);

    /// <summary>
    /// Zwraca publiczne możliwości uwierzytelniania skonfigurowane na tym serwerze.
    /// </summary>
    [HttpGet("capabilities")]
    [AllowAnonymous]
    [ProducesResponseType<AuthCapabilitiesDto>(StatusCodes.Status200OK)]
    public IActionResult GetCapabilities()
    {
        var schoolUrl = _configuration["Ldap:PasswordResetUrl"];
        var guidance = _configuration["Ldap:PasswordResetGuidance"]
            ?? "Hasło do konta szkolnego (LDAP / Active Directory) resetuje się u szkolnego dostawcy tożsamości lub u administratora IT szkoły.";

        return Ok(new AuthCapabilitiesDto(
            AllowLocalRegistration: _configuration.GetValue<bool>("Auth:AllowLocalRegistration"),
            EmailLoginEnabled: IsEmailLoginEnabled,
            LdapEnabled: _ldapService.IsEnabled,
            PasswordRecoveryEnabled: true,
            SchoolIdentityProviderUrl: string.IsNullOrWhiteSpace(schoolUrl) ? null : schoolUrl.Trim(),
            SchoolRecoveryGuidance: guidance));
    }

    /// <summary>
    /// Loguje użytkownika do systemu (login lokalny, zweryfikowany e-mail lub szkolny LDAP) i zwraca parę tokenów sesji.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [ProducesResponseType<LoginResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login([FromBody] UserLoginDto userLoginDto, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userLoginDto.name) || string.IsNullOrWhiteSpace(userLoginDto.password))
        {
            return this.Create(StatusCodes.Status400BadRequest, "Login i hasło są wymagane.");
        }

        var normalizedName = userLoginDto.name.Trim();
        var lowerName = normalizedName.ToLowerInvariant();

        var existingUser = await _context.Users.Include(u => u.Rank)
            .FirstOrDefaultAsync(u => u.UserLogin.ToLower() == lowerName, cancellationToken);

        if (existingUser == null && IsEmailLoginEnabled && normalizedName.Contains('@'))
        {
            existingUser = await _context.Users.Include(u => u.Rank)
                .FirstOrDefaultAsync(u =>
                    u.AuthProvider == "local" &&
                    u.Email != null &&
                    u.EmailVerifiedAt != null &&
                    u.Email.ToLower() == lowerName,
                    cancellationToken);
        }

        // 1. Użytkownik istnieje w bazie lokalnej
        if (existingUser != null)
        {
            bool isLdapAccount = string.Equals(existingUser.AuthProvider, "ldap", StringComparison.OrdinalIgnoreCase);
            bool isPasswordCorrect = !isLdapAccount && BCrypt.Net.BCrypt.Verify(userLoginDto.password, existingUser.UserPassword);
            bool authenticatedWithLdap = false;

            if (!isPasswordCorrect && _ldapService.IsEnabled)
            {
                var ldapFallback = await _ldapService.AuthenticateAsync(normalizedName, userLoginDto.password, cancellationToken);
                if (ldapFallback is { Success: true, User: not null })
                {
                    isPasswordCorrect = true;
                    authenticatedWithLdap = true;
                    await SyncLdapProfileAsync(existingUser, ldapFallback.User, cancellationToken);
                }
            }

            if (!isPasswordCorrect)
            {
                _logger?.LogWarning("Odrzucono próbę logowania z powodu nieprawidłowych danych uwierzytelniających.");
                return this.Create(StatusCodes.Status401Unauthorized, "Niepoprawny login lub hasło.");
            }

            if (await _context.BannedUsers.AnyAsync(b => b.UserId == existingUser.Id, cancellationToken))
            {
                _logger?.LogWarning("Odrzucono logowanie zablokowanego użytkownika {UserId}.", existingUser.Id);
                return this.Create(StatusCodes.Status403Forbidden, "Twoje konto zostało zablokowane przez moderatora.");
            }

            return await GenerateLoginResponseAsync(existingUser, "User zalogowany poprawnie", isLdap: authenticatedWithLdap || isLdapAccount, cancellationToken);
        }

        // 2. Użytkownika nie ma w bazie lokalnej -> Próba logowania przez szkolne konto LDAP (JIT Provisioning)
        if (_ldapService.IsEnabled)
        {
            var ldapResult = await _ldapService.AuthenticateAsync(normalizedName, userLoginDto.password, cancellationToken);
            if (ldapResult is { Success: true, User: not null })
            {
                return await ProvisionAndLoginLdapUserAsync(ldapResult.User, cancellationToken);
            }
        }

        _logger?.LogWarning("Odrzucono próbę logowania z powodu nieprawidłowych danych uwierzytelniających.");
        return this.Create(StatusCodes.Status401Unauthorized, "Błędne hasło i/lub login");
    }

    /// <summary>
    /// Dedykowany endpoint logowania przez szkolne konto LDAP / Active Directory (z JIT provisioning).
    /// </summary>
    [HttpPost("school/login")]
    [HttpPost("ldap/login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [ProducesResponseType<LoginResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SchoolLogin([FromBody] SchoolLoginDto dto, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dto.login) || string.IsNullOrWhiteSpace(dto.password))
        {
            return this.Create(StatusCodes.Status400BadRequest, "Login i hasło konta szkolnego są wymagane.");
        }

        if (!_ldapService.IsEnabled)
        {
            return this.Create(StatusCodes.Status503ServiceUnavailable, "Logowanie przez szkolny katalog LDAP jest obecnie wyłączone.");
        }

        var ldapResult = await _ldapService.AuthenticateAsync(dto.login, dto.password, cancellationToken);
        if (ldapResult is not { Success: true, User: not null })
        {
            _logger?.LogWarning("Odrzucono próbę logowania przez szkolny katalog LDAP.");
            return this.Create(
                StatusCodes.Status401Unauthorized,
                ldapResult?.ErrorMessage ?? "Niepoprawny login lub hasło konta szkolnego.");
        }

        var username = ldapResult.User.Username;
        var existingUser = await _context.Users.Include(u => u.Rank)
            .FirstOrDefaultAsync(u => u.UserLogin.ToLower() == username.ToLower(), cancellationToken);

        if (existingUser != null)
        {
            if (await _context.BannedUsers.AnyAsync(b => b.UserId == existingUser.Id, cancellationToken))
            {
                _logger?.LogWarning("Odrzucono logowanie LDAP zablokowanego użytkownika {UserId}.", existingUser.Id);
                return this.Create(StatusCodes.Status403Forbidden, "Twoje konto szkolne zostało zablokowane przez moderatora.");
            }

            await SyncLdapProfileAsync(existingUser, ldapResult.User, cancellationToken);
            return await GenerateLoginResponseAsync(existingUser, "Zalogowano poprawnie przez konto szkolne (LDAP)", isLdap: true, cancellationToken);
        }

        return await ProvisionAndLoginLdapUserAsync(ldapResult.User, cancellationToken);
    }

    /// <summary>
    /// Rejestruje nowe konto lokalne ucznia w systemie z opcjonalną nazwą wyświetlaną i adresem e-mail.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [ProducesResponseType<RegisterResponseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Register([FromBody] UserRegisterDto dto, CancellationToken cancellationToken = default)
    {
        if (!_configuration.GetValue<bool>("Auth:AllowLocalRegistration"))
        {
            _logger?.LogWarning("Odrzucono próbę publicznej rejestracji, ponieważ funkcja jest wyłączona.");
            return this.Create(StatusCodes.Status403Forbidden, "Publiczna rejestracja kont lokalnych jest wyłączona. Użyj szkolnego konta LDAP.");
        }

        if (string.IsNullOrWhiteSpace(dto.password) || string.IsNullOrWhiteSpace(dto.name))
        {
            return this.Create(StatusCodes.Status400BadRequest, "Login i hasło są niepoprawne");
        }

        var trimmedLogin = dto.name.Trim();
        if (dto.password.Length < 8 || trimmedLogin.Length < 3 || trimmedLogin.Length > 255)
        {
            return this.Create(StatusCodes.Status400BadRequest, "Hasło lub login nie spełniają wymogów (login min. 3 znaki, hasło min. 8 znaków)");
        }

        if (!TryNormalizeDisplayName(dto.displayName, out var normalizedDisplayName, out var displayNameError))
        {
            return this.Create(StatusCodes.Status400BadRequest, displayNameError!);
        }

        string? normalizedEmail = null;
        if (!string.IsNullOrWhiteSpace(dto.email))
        {
            if (!IsEmailLoginEnabled)
            {
                return this.Create(StatusCodes.Status400BadRequest, "Rejestracja z adresem e-mail jest wyłączona w konfiguracji serwera.");
            }

            normalizedEmail = EmailVerificationService.NormalizeAndValidateEmail(dto.email, out var emailError);
            if (normalizedEmail == null)
            {
                return this.Create(StatusCodes.Status400BadRequest, emailError!);
            }

            bool verifiedEmailTaken = await _context.Users.AnyAsync(
                u => u.EmailVerifiedAt != null && u.Email != null && u.Email.ToLower() == normalizedEmail,
                cancellationToken);
            if (verifiedEmailTaken)
            {
                return this.Create(StatusCodes.Status409Conflict, "Podany adres e-mail jest już powiązany ze zweryfikowanym kontem.");
            }
        }

        bool userExists = await _context.Users
            .AnyAsync(u => u.UserLogin.ToLower() == trimmedLogin.ToLowerInvariant(), cancellationToken);

        if (userExists)
        {
            return this.Create(StatusCodes.Status409Conflict, "Nazwa użytkownika zajęta");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var newUser = new User
        {
            UserLogin = trimmedLogin,
            UserPassword = BCrypt.Net.BCrypt.HashPassword(dto.password),
            DisplayName = normalizedDisplayName,
            Email = normalizedEmail,
            EmailVerifiedAt = null,
            AuthProvider = "local",
            TokenVersion = 1,
            CreatedAt = utcNow,
            RankId = 1,
            PasswordChangedAt = utcNow
        };

        _context.Users.Add(newUser);
        await _context.SaveChangesAsync(cancellationToken);

        bool verificationRequired = false;
        if (normalizedEmail != null && _emailVerificationService != null)
        {
            var verifyIssue = await _emailVerificationService.IssueVerificationForUserAsync(newUser, normalizedEmail, cancellationToken);
            verificationRequired = verifyIssue.Success;
        }

        _logger?.LogInformation("Utworzono lokalne konto użytkownika {UserId}.", newUser.Id);

        var response = new RegisterResponseDto(
            Id: newUser.Id,
            Username: newUser.UserLogin,
            DisplayName: newUser.DisplayName,
            Email: newUser.Email,
            EmailVerificationRequired: verificationRequired,
            Message: verificationRequired
                ? "Konto zostało utworzone. Wysłaliśmy jednorazowy link weryfikacyjny na podany adres e-mail."
                : "Konto zostało pomyślnie utworzone");

        return CreatedAtAction(nameof(GetMe), null, response);
    }

    /// <summary>
    /// Zwraca aktualny profil zalogowanego użytkownika (w tym aktualną rolę i nazwę wyświetlaną).
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<UserProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMe(CancellationToken cancellationToken = default)
    {
        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return this.Create(StatusCodes.Status401Unauthorized, "Błędna wartość w tokenie");
        }

        var user = await _context.Users
            .AsNoTracking()
            .Include(u => u.Rank)
            .FirstOrDefaultAsync(u => u.Id == currentUserId.Value, cancellationToken);

        if (user == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono konta użytkownika.");
        }

        return Ok(ToProfileDto(user));
    }

    /// <summary>
    /// Aktualizuje nazwę wyświetlaną zalogowanego użytkownika.
    /// </summary>
    [HttpPatch("me")]
    [Authorize]
    [ProducesResponseType<UserProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateProfileDto dto, CancellationToken cancellationToken = default)
    {
        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return this.Create(StatusCodes.Status401Unauthorized, "Błędna wartość w tokenie");
        }

        if (!TryNormalizeDisplayName(dto.displayName, out var normalizedDisplayName, out var error))
        {
            return this.Create(StatusCodes.Status400BadRequest, error!);
        }

        var user = await _context.Users
            .Include(u => u.Rank)
            .FirstOrDefaultAsync(u => u.Id == currentUserId.Value, cancellationToken);
        if (user == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono konta użytkownika.");
        }

        user.DisplayName = normalizedDisplayName;
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(ToProfileDto(user));
    }

    /// <summary>
    /// Przypisuje lub zmienia adres e-mail zalogowanego konta lokalnego i wysyła jednorazowy link weryfikacyjny.
    /// </summary>
    [HttpPost("me/email")]
    [Authorize]
    [EnableRateLimiting("auth-recovery")]
    [ProducesResponseType<AuthMessageResponseDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> AttachEmail([FromBody] AttachEmailDto dto, CancellationToken cancellationToken = default)
    {
        if (_emailVerificationService == null)
        {
            return this.Create(StatusCodes.Status503ServiceUnavailable, "Usługa weryfikacji e-maila jest niedostępna.");
        }

        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return this.Create(StatusCodes.Status401Unauthorized, "Błędna wartość w tokenie");
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId.Value, cancellationToken);
        if (user == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono konta użytkownika.");
        }

        var result = await _emailVerificationService.IssueVerificationForUserAsync(user, dto.email, cancellationToken);
        if (!result.Success)
        {
            return this.Create(result.StatusCode, result.Message ?? "Nie udało się wysłać weryfikacji adresu e-mail.");
        }

        return Accepted(new AuthMessageResponseDto(result.Message ?? "Wysłano link weryfikacyjny na podany adres e-mail."));
    }

    /// <summary>
    /// Potwierdza adres e-mail jednorazowym tokenem URL.
    /// </summary>
    [HttpPost("email/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-recovery")]
    [ProducesResponseType<EmailVerificationResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailDto dto, CancellationToken cancellationToken = default)
    {
        if (_emailVerificationService == null)
        {
            return this.Create(StatusCodes.Status503ServiceUnavailable, "Usługa weryfikacji e-maila jest niedostępna.");
        }

        var result = await _emailVerificationService.VerifyTokenAsync(dto.token, cancellationToken);
        if (!result.Success || result.Data == null)
        {
            return this.Create(result.StatusCode, result.Message ?? "Nie udało się zweryfikować adresu e-mail.");
        }

        return Ok(result.Data);
    }

    /// <summary>
    /// Ponawia wysyłkę linku weryfikacyjnego dla niezweryfikowanego konta lokalnego (odpowiedź jednolita).
    /// </summary>
    [HttpPost("email/resend")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-recovery")]
    [ProducesResponseType<AuthMessageResponseDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ResendVerification([FromBody] ResendEmailVerificationDto dto, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dto.identifier))
        {
            return this.Create(StatusCodes.Status400BadRequest, "Identyfikator konta lub adres e-mail jest wymagany.");
        }

        if (_emailVerificationService != null)
        {
            await _emailVerificationService.ResendVerificationIfEligibleAsync(dto.identifier, cancellationToken);
        }

        return Accepted(new AuthMessageResponseDto("Jeśli konto lokalne oczekuje na weryfikację adresu e-mail, wysłano nowy link weryfikacyjny."));
    }

    /// <summary>
    /// Przyjmuje zgłoszenie odzyskiwania hasła z jednolitą odpowiedzią; dla zweryfikowanych kont lokalnych
    /// wysyła krótkotrwały jednorazowy link resetu, a konta szkolne kieruje do dostawcy tożsamości szkoły.
    /// </summary>
    [HttpPost("password-recovery/request")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-recovery")]
    [ProducesResponseType<PasswordRecoveryRequestResponseDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RequestPasswordRecovery([FromBody] PasswordRecoveryRequestDto dto, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dto.identifier))
        {
            return this.Create(StatusCodes.Status400BadRequest, "Login lub adres e-mail jest wymagany.");
        }

        if (_passwordRecoveryService == null)
        {
            return this.Create(StatusCodes.Status503ServiceUnavailable, "Usługa odzyskiwania hasła jest niedostępna.");
        }

        var response = await _passwordRecoveryService.RequestRecoveryAsync(dto.identifier, cancellationToken);
        return Accepted(response);
    }

    /// <summary>
    /// Kończy odzyskiwanie hasła za pomocą krótkotrwałego jednorazowego dowodu URL i unieważnia aktywne sesje.
    /// </summary>
    [HttpPost("password-recovery/confirm")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-recovery")]
    [ProducesResponseType<AuthMessageResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ConfirmPasswordRecovery([FromBody] PasswordRecoveryConfirmDto dto, CancellationToken cancellationToken = default)
    {
        if (_passwordRecoveryService == null)
        {
            return this.Create(StatusCodes.Status503ServiceUnavailable, "Usługa odzyskiwania hasła jest niedostępna.");
        }

        var result = await _passwordRecoveryService.ConfirmRecoveryAsync(dto.token, dto.newPassword, cancellationToken);
        if (!result.Success)
        {
            return this.Create(result.StatusCode, result.Message ?? "Nie udało się zresetować hasła.");
        }

        return Ok(new AuthMessageResponseDto(result.Data ?? "Hasło zostało zmienione."));
    }

    /// <summary>
    /// Zwraca wskazówki i adres szkolnego dostawcy tożsamości do resetu hasła dla kont szkolnych (LDAP).
    /// </summary>
    [HttpGet("password-recovery/school-provider")]
    [AllowAnonymous]
    [ProducesResponseType<PasswordRecoveryRequestResponseDto>(StatusCodes.Status200OK)]
    public IActionResult GetSchoolRecoveryProvider()
    {
        if (_passwordRecoveryService != null)
        {
            return Ok(_passwordRecoveryService.GetSchoolProviderGuidance());
        }

        var schoolUrl = _configuration["Ldap:PasswordResetUrl"];
        var guidance = _configuration["Ldap:PasswordResetGuidance"]
            ?? "Hasło do konta szkolnego (LDAP / Active Directory) resetuje się u szkolnego dostawcy tożsamości lub u administratora IT szkoły.";

        return Ok(new PasswordRecoveryRequestResponseDto(
            Message: guidance,
            SchoolIdentityProviderUrl: string.IsNullOrWhiteSpace(schoolUrl) ? null : schoolUrl.Trim(),
            SchoolGuidance: guidance));
    }

    /// <summary>
    /// Zmienia hasło zalogowanego konta lokalnego; konta szkolne LDAP są kierowane do szkolnego dostawcy tożsamości.
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting("auth-recovery")]
    [ProducesResponseType<AuthMessageResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto, CancellationToken cancellationToken = default)
    {
        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return this.Create(StatusCodes.Status401Unauthorized, "Błędna wartość w tokenie");
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId.Value, cancellationToken);
        if (user == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono konta użytkownika.");
        }

        if (string.Equals(user.AuthProvider, "ldap", StringComparison.OrdinalIgnoreCase))
        {
            var schoolUrl = _configuration["Ldap:PasswordResetUrl"];
            return this.Create(
                StatusCodes.Status403Forbidden,
                "Konta szkolne (LDAP) muszą zmieniać hasło u szkolnego dostawcy tożsamości.",
                extensions: string.IsNullOrWhiteSpace(schoolUrl)
                    ? null
                    : new Dictionary<string, object?> { ["schoolIdentityProviderUrl"] = schoolUrl });
        }

        if (string.IsNullOrWhiteSpace(dto.currentPassword) || !BCrypt.Net.BCrypt.Verify(dto.currentPassword, user.UserPassword))
        {
            return this.Create(StatusCodes.Status400BadRequest, "Obecne hasło jest nieprawidłowe.");
        }

        if (string.IsNullOrWhiteSpace(dto.newPassword) || dto.newPassword.Length < 8)
        {
            return this.Create(StatusCodes.Status400BadRequest, "Nowe hasło musi mieć co najmniej 8 znaków.");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        user.UserPassword = BCrypt.Net.BCrypt.HashPassword(dto.newPassword);
        user.PasswordChangedAt = utcNow;
        await _context.SaveChangesAsync(cancellationToken);

        if (_sessionTokenService != null)
        {
            await _sessionTokenService.RevokeAllUserSessionsAsync(user, cancellationToken);
        }
        else
        {
            user.TokenVersion += 1;
            await _context.SaveChangesAsync(cancellationToken);
            if (_securityStampService != null)
                await _securityStampService.InvalidateUserAsync(user.Id, cancellationToken);
        }

        return Ok(new AuthMessageResponseDto("Hasło zostało pomyślnie zmienione. Zaloguj się ponownie."));
    }

    /// <summary>
    /// Odświeża sesję użytkownika za pomocą ważnego refresh tokenu, stosując rotację i odświeżając aktualną rolę.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [ProducesResponseType<LoginResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequestDto dto, CancellationToken cancellationToken = default)
    {
        if (_sessionTokenService == null)
        {
            return this.Create(StatusCodes.Status503ServiceUnavailable, "Usługa odświeżania sesji jest niedostępna.");
        }

        var result = await _sessionTokenService.RefreshSessionAsync(dto.refreshToken, cancellationToken);
        if (!result.Success || result.Data == null)
        {
            return this.Create(result.StatusCode, result.Message ?? "Nie udało się odświeżyć sesji.");
        }

        var refreshed = result.Data;
        return Ok(BuildLoginDto(
            refreshed.User,
            "Sesja została pomyślnie odświeżona",
            refreshed.IsLdap,
            refreshed.Tokens.AccessToken,
            refreshed.Tokens.RefreshToken,
            refreshed.Tokens.AccessTokenExpiresAtUtc,
            refreshed.Tokens.RefreshTokenExpiresAtUtc));
    }

    /// <summary>
    /// Unieważnia bieżący access token (JTI) oraz opcjonalny refresh token.
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequestDto? dto = null, CancellationToken cancellationToken = default)
    {
        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return this.Create(StatusCodes.Status401Unauthorized, "Błędna wartość w tokenie");
        }

        await RevokeCurrentJwtIfPresentAsync(cancellationToken);

        if (_sessionTokenService != null && !string.IsNullOrWhiteSpace(dto?.refreshToken))
        {
            await _sessionTokenService.RevokeRefreshTokenAsync(dto.refreshToken, currentUserId.Value, cancellationToken);
        }

        return NoContent();
    }

    /// <summary>
    /// Unieważnia wszystkie aktywne sesje (access i refresh tokeny) zalogowanego użytkownika na wszystkich urządzeniach.
    /// </summary>
    [HttpPost("logout-all")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> LogoutAll(CancellationToken cancellationToken = default)
    {
        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return this.Create(StatusCodes.Status401Unauthorized, "Błędna wartość w tokenie");
        }

        await RevokeCurrentJwtIfPresentAsync(cancellationToken);

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId.Value, cancellationToken);
        if (user != null)
        {
            if (_sessionTokenService != null)
            {
                await _sessionTokenService.RevokeAllUserSessionsAsync(user, cancellationToken);
            }
            else
            {
                user.TokenVersion += 1;
                await _context.SaveChangesAsync(cancellationToken);
                if (_securityStampService != null)
                    await _securityStampService.InvalidateUserAsync(user.Id, cancellationToken);
            }
        }

        return NoContent();
    }

    /// <summary>
    /// Usuwa konto aktualnie zalogowanego użytkownika oraz unieważnia jego sesje.
    /// </summary>
    [HttpDelete("delete-acc")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAcc(CancellationToken cancellationToken = default)
    {
        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return this.Create(StatusCodes.Status401Unauthorized, "Błędna wartość w tokenie");
        }

        var user = await _context.Users
            .Include(u => u.Songs)
            .FirstOrDefaultAsync(u => u.Id == currentUserId.Value, cancellationToken);

        if (user == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono konta do usunięcia");
        }

        await RevokeCurrentJwtIfPresentAsync(cancellationToken);

        user.Songs.Clear();

        var userVotes = await _context.Votes.Where(v => v.UserId == currentUserId.Value).ToListAsync(cancellationToken);
        if (userVotes.Count > 0)
            _context.Votes.RemoveRange(userVotes);

        var userQueueItems = await _context.QueueItems.Where(q => q.UserId == currentUserId.Value).ToListAsync(cancellationToken);
        foreach (var item in userQueueItems)
        {
            item.UserId = null;
        }

        var refreshTokens = await _context.RefreshTokens.Where(t => t.UserId == currentUserId.Value).ToListAsync(cancellationToken);
        if (refreshTokens.Count > 0)
            _context.RefreshTokens.RemoveRange(refreshTokens);

        var resetTokens = await _context.PasswordResetTokens.Where(t => t.UserId == currentUserId.Value).ToListAsync(cancellationToken);
        if (resetTokens.Count > 0)
            _context.PasswordResetTokens.RemoveRange(resetTokens);

        var verifyTokens = await _context.EmailVerificationTokens.Where(t => t.UserId == currentUserId.Value).ToListAsync(cancellationToken);
        if (verifyTokens.Count > 0)
            _context.EmailVerificationTokens.RemoveRange(verifyTokens);

        _context.Users.Remove(user);
        await _context.SaveChangesAsync(cancellationToken);

        if (_securityStampService != null)
        {
            await _securityStampService.InvalidateUserAsync(currentUserId.Value, cancellationToken);
        }

        _logger?.LogInformation("Usunięto konto użytkownika {UserId}.", currentUserId.Value);

        return NoContent();
    }

    private async Task<IActionResult> ProvisionAndLoginLdapUserAsync(LdapUser ldapUser, CancellationToken cancellationToken)
    {
        var username = ldapUser.Username;
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var newUser = new User
        {
            UserLogin = username,
            UserPassword = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
            DisplayName = TruncateDisplayName(ldapUser.DisplayName),
            Email = await ResolveNonConflictingLdapEmailAsync(null, ldapUser.Email, cancellationToken),
            EmailVerifiedAt = null,
            AuthProvider = "ldap",
            TokenVersion = 1,
            CreatedAt = utcNow,
            RankId = 1,
            PasswordChangedAt = utcNow
        };
        if (!string.IsNullOrWhiteSpace(newUser.Email))
        {
            newUser.EmailVerifiedAt = utcNow;
        }

        try
        {
            _context.Users.Add(newUser);
            await _context.SaveChangesAsync(cancellationToken);
            await _context.Entry(newUser).Reference(u => u.Rank).LoadAsync(cancellationToken);
            return await GenerateLoginResponseAsync(newUser, "Zalogowano poprawnie przez konto szkolne (LDAP)", isLdap: true, cancellationToken);
        }
        catch (DbUpdateException)
        {
            _context.Entry(newUser).State = EntityState.Detached;
            var parallelUser = await _context.Users.Include(u => u.Rank)
                .FirstOrDefaultAsync(u => u.UserLogin.ToLower() == username.ToLower(), cancellationToken);
            if (parallelUser != null)
            {
                return await GenerateLoginResponseAsync(parallelUser, "Zalogowano poprawnie przez konto szkolne (LDAP)", isLdap: true, cancellationToken);
            }
            throw;
        }
    }

    private async Task SyncLdapProfileAsync(User user, LdapUser ldapUser, CancellationToken cancellationToken)
    {
        bool changed = false;
        if (!string.Equals(user.AuthProvider, "ldap", StringComparison.OrdinalIgnoreCase))
        {
            user.AuthProvider = "ldap";
            changed = true;
        }

        var syncedDisplayName = TruncateDisplayName(ldapUser.DisplayName);
        if (!string.IsNullOrWhiteSpace(syncedDisplayName) && string.IsNullOrWhiteSpace(user.DisplayName))
        {
            user.DisplayName = syncedDisplayName;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(ldapUser.Email) && string.IsNullOrWhiteSpace(user.Email))
        {
            var resolvedEmail = await ResolveNonConflictingLdapEmailAsync(user.Id, ldapUser.Email, cancellationToken);
            if (resolvedEmail != null)
            {
                user.Email = resolvedEmail;
                user.EmailVerifiedAt = _timeProvider.GetUtcNow().UtcDateTime;
                changed = true;
            }
        }

        if (changed)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<string?> ResolveNonConflictingLdapEmailAsync(int? currentUserId, string? rawEmail, CancellationToken cancellationToken)
    {
        var normalized = EmailVerificationService.NormalizeAndValidateEmail(rawEmail, out _);
        if (normalized == null)
            return null;

        bool conflict = await _context.Users.AnyAsync(
            u => (!currentUserId.HasValue || u.Id != currentUserId.Value) &&
                 u.EmailVerifiedAt != null &&
                 u.Email != null &&
                 u.Email.ToLower() == normalized,
            cancellationToken);

        return conflict ? null : normalized;
    }

    private async Task<IActionResult> GenerateLoginResponseAsync(
        User user,
        string message,
        bool isLdap,
        CancellationToken cancellationToken)
    {
        string accessToken;
        DateTime? accessExpiresAtUtc;
        string? refreshToken = null;
        DateTime? refreshExpiresAtUtc = null;

        if (_sessionTokenService != null)
        {
            var sessionTokens = await _sessionTokenService.CreateSessionAsync(user, isLdap, cancellationToken);
            accessToken = sessionTokens.AccessToken;
            accessExpiresAtUtc = sessionTokens.AccessTokenExpiresAtUtc;
            refreshToken = sessionTokens.RefreshToken;
            refreshExpiresAtUtc = sessionTokens.RefreshTokenExpiresAtUtc;
        }
        else
        {
            var issued = _jwtTokenService.IssueAccessToken(user, isLdap);
            accessToken = issued.Token;
            accessExpiresAtUtc = issued.ExpiresAtUtc;
        }

        _logger?.LogInformation(
            "Użytkownik {UserId} zalogował się z rolą {Role} metodą {AuthenticationMethod}.",
            user.Id,
            user.Rank?.Rank1 ?? "user",
            isLdap ? "ldap" : "password");

        return Ok(BuildLoginDto(
            user,
            message,
            isLdap,
            accessToken,
            refreshToken,
            accessExpiresAtUtc,
            refreshExpiresAtUtc));
    }

    private static LoginResponseDto BuildLoginDto(
        User user,
        string message,
        bool isLdap,
        string accessToken,
        string? refreshToken,
        DateTime? accessExpiresAtUtc,
        DateTime? refreshExpiresAtUtc) =>
        new(
            Message: message,
            jwtSecurityToken: accessToken,
            role: user.Rank?.Rank1 ?? "user",
            userId: user.Id,
            username: user.UserLogin,
            isLdap: isLdap,
            displayName: user.DisplayName,
            email: user.Email,
            emailVerified: user.EmailVerifiedAt.HasValue,
            authProvider: user.AuthProvider,
            refreshToken: refreshToken,
            expiresAtUtc: accessExpiresAtUtc,
            refreshTokenExpiresAtUtc: refreshExpiresAtUtc);

    private static UserProfileDto ToProfileDto(User user) =>
        new(
            UserId: user.Id,
            Username: user.UserLogin,
            DisplayName: user.DisplayName,
            Email: user.Email,
            EmailVerified: user.EmailVerifiedAt.HasValue,
            Role: user.Rank?.Rank1 ?? "user",
            RankId: user.RankId,
            IsLdap: string.Equals(user.AuthProvider, "ldap", StringComparison.OrdinalIgnoreCase),
            AuthProvider: user.AuthProvider,
            CreatedAt: user.CreatedAt,
            PasswordChangedAt: user.PasswordChangedAt);

    private async Task RevokeCurrentJwtIfPresentAsync(CancellationToken cancellationToken)
    {
        if (_securityStampService == null)
            return;

        var jti = User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        if (string.IsNullOrWhiteSpace(jti))
            return;

        var expClaim = User.FindFirst(JwtRegisteredClaimNames.Exp)?.Value;
        var remaining = TimeSpan.FromHours(12);
        if (long.TryParse(expClaim, out var expUnix))
        {
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expUnix);
            remaining = expiresAt - _timeProvider.GetUtcNow();
        }

        await _securityStampService.RevokeAccessTokenAsync(jti, remaining, cancellationToken);
    }

    private static bool TryNormalizeDisplayName(string? raw, out string? normalized, out string? errorMessage)
    {
        errorMessage = null;
        if (raw == null)
        {
            normalized = null;
            return true;
        }

        if (raw.Length == 0)
        {
            normalized = null;
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            errorMessage = "Nazwa wyświetlana nie może składać się wyłącznie z białych znaków.";
            normalized = null;
            return false;
        }

        if (trimmed.Length > MaxDisplayNameLength)
        {
            errorMessage = $"Nazwa wyświetlana może mieć maksymalnie {MaxDisplayNameLength} znaków.";
            normalized = null;
            return false;
        }

        if (trimmed.Any(char.IsControl))
        {
            errorMessage = "Nazwa wyświetlana zawiera niedozwolone znaki sterujące.";
            normalized = null;
            return false;
        }

        normalized = trimmed;
        return true;
    }

    private static string? TruncateDisplayName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var cleaned = new string(raw.Trim().Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (cleaned.Length == 0)
            return null;

        return cleaned.Length <= MaxDisplayNameLength ? cleaned : cleaned[..MaxDisplayNameLength];
    }
}
