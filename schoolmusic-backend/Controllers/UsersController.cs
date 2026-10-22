using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using schoolmusic_backend.Models;
using System.Security.Claims;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using StackExchange.Redis;

namespace schoolmusic_backend.Controllers;

/// <summary>
/// Kontroler uwierzytelniania i rejestracji użytkowników (JWT).
/// </summary>
[Route("api/auth")]
[ApiController]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly schoolmusicContext _context;
    private readonly IConfiguration _configuration;

    public UsersController(schoolmusicContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    /// <summary>
    /// Model danych logowania użytkownika.
    /// </summary>
    public class UserLoginDto
    {
        /// <summary>Nazwa użytkownika (login).</summary>
        public string name { get; set; } = string.Empty;
        /// <summary>Hasło użytkownika.</summary>
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
    /// Loguje użytkownika do systemu i zwraca token JWT z przypisaną rolą.
    /// </summary>
    /// <param name="userLoginDto">Dane logowania (login i hasło).</param>
    /// <returns>Token JWT i status operacji.</returns>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] UserLoginDto userLoginDto)
    {
        var exisitingUser = await _context.Users.Include(u => u.Rank)
            .FirstOrDefaultAsync(u => u.UserLogin == userLoginDto.name);
        
        if (exisitingUser == null)
        {
            return Problem(
                detail: "Błędne hasło i/lub login",
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized"
            );
        }

        if (!BCrypt.Net.BCrypt.Verify(userLoginDto.password, exisitingUser.UserPassword))
        {
            return BadRequest(new
            {
                Message = "Błędne hasło"
            });
        }

        var jwtSettings = _configuration.GetSection("Jwt");
        var key = Encoding.UTF8.GetBytes(jwtSettings["Key"] ?? "default_jwt_secret_key_development_only");
        var credentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, exisitingUser.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, exisitingUser.UserLogin),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, exisitingUser.Rank?.Rank1 ?? "user")
        };

        var token = new JwtSecurityToken(
            issuer: jwtSettings["Issuer"],
            audience: jwtSettings["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(12),
            signingCredentials: credentials
        );

        string tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return Ok(new
        {
            Message = "User zalogowany poprawnie",
            jwtSecurityToken = tokenString,
            role = exisitingUser.Rank?.Rank1 ?? "user",
            userId = exisitingUser.Id
        });
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
}
