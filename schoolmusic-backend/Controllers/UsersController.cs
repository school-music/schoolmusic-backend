using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers;

/// <summary>
/// Kontroler API odpowiedzialny za uwierzytelnianie użytkowników (logowanie lokalne oraz przez szkolne konto LDAP),
/// rejestrację uczniów oraz zarządzanie własnym kontem.
/// </summary>
[Route("api/auth")]
[ApiController]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly schoolmusicContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILdapService _ldapService;
    private readonly IJwtTokenService _jwtTokenService;

    public UsersController(
        schoolmusicContext context,
        IConfiguration configuration,
        ILdapService ldapService,
        IJwtTokenService jwtTokenService)
    {
        _context = context;
        _configuration = configuration;
        _ldapService = ldapService;
        _jwtTokenService = jwtTokenService;
    }

    /// <summary>
    /// Model danych logowania użytkownika.
    /// </summary>
    public class UserLoginDto
    {
        /// <summary>Nazwa użytkownika (login lokalny lub szkolny imie.nazwisko).</summary>
        public string name { get; set; } = string.Empty;
        /// <summary>Hasło użytkownika.</summary>
        public string password { get; set; } = string.Empty;
    }

    /// <summary>
    /// Model danych logowania dedykowanego dla kont szkolnych LDAP.
    /// </summary>
    public class SchoolLoginDto
    {
        /// <summary>Login szkolny (np. imie.nazwisko lub email szkolny).</summary>
        public string login { get; set; } = string.Empty;
        /// <summary>Hasło do szkolnego konta.</summary>
        public string password { get; set; } = string.Empty;
    }

    /// <summary>
    /// Model danych rejestracji nowego użytkownika.
    /// </summary>
    public class UserRegisterDto
    {
        /// <summary>Nazwa użytkownika (min. 3 znaki).</summary>
        public string name { get; set; } = string.Empty;
        /// <summary>Hasło użytkownika (min. 8 znaków).</summary>
        public string password { get; set; } = string.Empty;
    }

    /// <summary>
    /// Odpowiedź zwracana po pomyślnym zalogowaniu użytkownika.
    /// </summary>
    public record LoginResponseDto(
        string Message,
        string jwtSecurityToken,
        string role,
        int userId,
        string username,
        bool isLdap
    );

    /// <summary>
    /// Loguje użytkownika do systemu i zwraca token JWT z przypisaną rolą.
    /// Wspiera logowanie lokalne oraz automatyczny fallback do szkolnego katalogu LDAP.
    /// </summary>
    /// <param name="userLoginDto">Dane logowania (login i hasło).</param>
    /// <returns>Token JWT i status operacji.</returns>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] UserLoginDto userLoginDto)
    {
        if (string.IsNullOrWhiteSpace(userLoginDto.name) || string.IsNullOrWhiteSpace(userLoginDto.password))
        {
            return BadRequest(new { Message = "Login i hasło są wymagane." });
        }

        var normalizedName = userLoginDto.name.Trim();
        var existingUser = await _context.Users.Include(u => u.Rank)
            .FirstOrDefaultAsync(u => u.UserLogin.ToLower() == normalizedName.ToLower());

        // 1. Użytkownik istnieje w bazie lokalnej
        if (existingUser != null)
        {
            bool isPasswordCorrect = BCrypt.Net.BCrypt.Verify(userLoginDto.password, existingUser.UserPassword);
            bool authenticatedWithLdap = false;

            // Jeśli hasło lokalne nie pasuje, ale LDAP jest włączony, sprawdzamy w katalogu LDAP
            if (!isPasswordCorrect && _ldapService.IsEnabled)
            {
                var ldapFallback = await _ldapService.AuthenticateAsync(normalizedName, userLoginDto.password);
                if (ldapFallback.Success && ldapFallback.User != null)
                {
                    isPasswordCorrect = true;
                    authenticatedWithLdap = true;
                }
            }

            if (!isPasswordCorrect)
            {
                return BadRequest(new { Message = "Błędne hasło" });
            }

            var isBanned = await _context.BannedUsers.AnyAsync(b => b.UserId == existingUser.Id);
            if (isBanned)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { Message = "Twoje konto zostało zablokowane przez moderatora." });
            }

            return GenerateLoginResponse(existingUser, "User zalogowany poprawnie", isLdap: authenticatedWithLdap);
        }

        // 2. Użytkownika nie ma w bazie lokalnej -> Próba logowania przez szkolne konto LDAP (JIT Provisioning)
        if (_ldapService.IsEnabled)
        {
            var ldapResult = await _ldapService.AuthenticateAsync(normalizedName, userLoginDto.password);
            if (ldapResult.Success && ldapResult.User != null)
            {
                var username = ldapResult.User.Username;
                var newUser = new User
                {
                    UserLogin = username,
                    UserPassword = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
                    CreatedAt = DateTime.UtcNow,
                    RankId = 1, // 'user'
                    PasswordChangedAt = DateTime.UtcNow
                };

                try
                {
                    _context.Users.Add(newUser);
                    await _context.SaveChangesAsync();
                    await _context.Entry(newUser).Reference(u => u.Rank).LoadAsync();
                    return GenerateLoginResponse(newUser, "Zalogowano poprawnie przez konto szkolne (LDAP)", isLdap: true);
                }
                catch (DbUpdateException)
                {
                    _context.Entry(newUser).State = EntityState.Detached;
                    var parallelUser = await _context.Users.Include(u => u.Rank)
                        .FirstOrDefaultAsync(u => u.UserLogin.ToLower() == username.ToLower());
                    if (parallelUser != null)
                    {
                        return GenerateLoginResponse(parallelUser, "Zalogowano poprawnie przez konto szkolne (LDAP)", isLdap: true);
                    }
                    throw;
                }
            }
        }

        return Problem(
            detail: "Błędne hasło i/lub login",
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Unauthorized"
        );
    }

    /// <summary>
    /// Dedykowany endpoint logowania przez szkolne konto LDAP / Active Directory.
    /// Jeśli konto nie istnieje w systemie, następuje automatyczna rejestracja (JIT provisioning).
    /// </summary>
    /// <param name="dto">Login i hasło konta szkolnego.</param>
    /// <returns>Token JWT i metadane konta.</returns>
    [HttpPost("school/login")]
    [HttpPost("ldap/login")]
    [AllowAnonymous]
    public async Task<IActionResult> SchoolLogin([FromBody] SchoolLoginDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.login) || string.IsNullOrWhiteSpace(dto.password))
        {
            return BadRequest(new { Message = "Login i hasło konta szkolnego są wymagane." });
        }

        if (!_ldapService.IsEnabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                Message = "Logowanie przez szkolny katalog LDAP jest obecnie wyłączone."
            });
        }

        var ldapResult = await _ldapService.AuthenticateAsync(dto.login, dto.password);
        if (!ldapResult.Success || ldapResult.User == null)
        {
            return Unauthorized(new
            {
                Message = ldapResult.ErrorMessage ?? "Niepoprawny login lub hasło konta szkolnego."
            });
        }

        var username = ldapResult.User.Username;
        var existingUser = await _context.Users.Include(u => u.Rank)
            .FirstOrDefaultAsync(u => u.UserLogin.ToLower() == username.ToLower());

        if (existingUser != null)
        {
            var isBanned = await _context.BannedUsers.AnyAsync(b => b.UserId == existingUser.Id);
            if (isBanned)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { Message = "Twoje konto szkolne zostało zablokowane przez moderatora." });
            }

            return GenerateLoginResponse(existingUser, "Zalogowano poprawnie przez konto szkolne (LDAP)", isLdap: true);
        }

        // Auto-provisioning nowego ucznia
        var newUser = new User
        {
            UserLogin = username,
            UserPassword = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
            CreatedAt = DateTime.UtcNow,
            RankId = 1,
            PasswordChangedAt = DateTime.UtcNow
        };

        try
        {
            _context.Users.Add(newUser);
            await _context.SaveChangesAsync();
            await _context.Entry(newUser).Reference(u => u.Rank).LoadAsync();
            return GenerateLoginResponse(newUser, "Zalogowano poprawnie przez konto szkolne (LDAP)", isLdap: true);
        }
        catch (DbUpdateException)
        {
            _context.Entry(newUser).State = EntityState.Detached;
            var parallelUser = await _context.Users.Include(u => u.Rank)
                .FirstOrDefaultAsync(u => u.UserLogin.ToLower() == username.ToLower());
            if (parallelUser != null)
            {
                return GenerateLoginResponse(parallelUser, "Zalogowano poprawnie przez konto szkolne (LDAP)", isLdap: true);
            }
            throw;
        }
    }

    /// <summary>
    /// Rejestruje nowe konto ucznia w systemie.
    /// </summary>
    /// <param name="dto">Dane nowego konta (login i hasło).</param>
    /// <returns>Utworzone konto.</returns>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] UserRegisterDto dto)
    {
        if (!_configuration.GetValue<bool>("Auth:AllowLocalRegistration"))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                Message = "Publiczna rejestracja kont lokalnych jest wyłączona. Użyj szkolnego konta LDAP."
            });
        }

        if (string.IsNullOrWhiteSpace(dto.password) || string.IsNullOrWhiteSpace(dto.name))
        {
            return BadRequest(new
            {
                Message = "Login i hasło są niepoprawne"
            });
        }
        if (dto.password.Length < 8 || dto.name.Length < 3)
        {
            return BadRequest(new
            {
                Message = "Hasło lub login nie spełniają wymogów (login min. 3 znaki, hasło min. 8 znaków)"
            });
        }

        bool userExists = await _context.Users
            .AnyAsync(u => u.UserLogin.ToLower() == dto.name.ToLower());

        if (userExists)
        {
            return Conflict(new
            {
                Message = "Nazwa użytkownika zajęta"
            });
        }

        User newUser = new User
        {
            UserLogin = dto.name,
            UserPassword = BCrypt.Net.BCrypt.HashPassword(dto.password),
            CreatedAt = DateTime.UtcNow,
            RankId = 1,
            PasswordChangedAt = DateTime.UtcNow,
        };

        _context.Users.Add(newUser);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(Register), new
        {
            id = newUser.Id,
            username = newUser.UserLogin,
            Message = "Konto zostało pomyślnie utworzone"
        });
    }

    /// <summary>
    /// Usuwa konto aktualnie zalogowanego użytkownika.
    /// </summary>
    [HttpDelete("delete-acc")]
    [Authorize]
    public async Task<IActionResult> DeleteAcc()
    {
        var userIdClaims = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        if (!int.TryParse(userIdClaims, out int currentUserId))
        {
            return Unauthorized(new
            {
                Message = "Błędna wartość w tokenie"
            });
        }

        var user = await _context.Users.FindAsync(currentUserId);
        if (user != null)
        {
            _context.Users.Remove(user);
        }
        else
        {
            return NotFound(new
            {
                Message = "Nie znaleziono konta do usunięcia"
            });
        }
        await _context.SaveChangesAsync();

        return NoContent();
    }

    private IActionResult GenerateLoginResponse(User user, string message, bool isLdap = false)
    {
        string tokenString = _jwtTokenService.CreateToken(user, isLdap);

        return Ok(new LoginResponseDto(
            Message: message,
            jwtSecurityToken: tokenString,
            role: user.Rank?.Rank1 ?? "user",
            userId: user.Id,
            username: user.UserLogin,
            isLdap: isLdap
        ));
    }
}
