using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using schoolmusic_backend.Models;
using System.Security.Claims;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Azure.Identity;
using StackExchange.Redis;

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

    public class UserLoginDto
    {
        public string name { get; set; } = string.Empty;
        public string password { get; set; } = string.Empty;
    }

    public class UserRegisterDto
    {
        public string name { get; set; } = string.Empty;
        public string password { get; set; } = string.Empty;
    }
    public record RefreshTokenRequestDto (string refreshToken);
    public record AuthResponseDto(string accessToken, string refreshToken, string username);

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] UserLoginDto userLoginDto)
    {
        var exisitingUser = await _context.Users.Include(u => u.Rank)
            .FirstOrDefaultAsync(
            u => u.UserLogin == userLoginDto.name);
        if(!BCrypt.Net.BCrypt.Verify(userLoginDto.password, exisitingUser.UserPassword))
        {
            return BadRequest(new
            {
                Message = "Błędne hasło"
            });
        }

        if(exisitingUser == null)
        {
            return Problem(
                detail: "Błędne hasło i/lub login",
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized"
            );
        }

        var jwtSettings = _configuration.GetSection("Jwt");
        var key = Encoding.UTF8.GetBytes(jwtSettings["Key"]);
        var credentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256);

        var Claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, exisitingUser.Id.ToString()), // ! Subject 
            new Claim(JwtRegisteredClaimNames.UniqueName, exisitingUser.UserLogin), // ! nazwa użytkownika
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()), // ! JWT ID, czyli identyfikator samego tokenu
            new Claim(ClaimTypes.Role, exisitingUser.Rank?.RankName ?? "user")
        };

        var token = new JwtSecurityToken(
            issuer: jwtSettings["Issuer"],
            audience: jwtSettings["Audience"],
            claims: Claims,
            expires: DateTime.UtcNow.AddHours(5),
            signingCredentials: credentials
        );


        string tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return Ok(new
        {
            Message = "User zalogowany poprawnie",
            jwtSecurityToken = tokenString,
        });
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] UserRegisterDto dto)
    {
        if(string.IsNullOrWhiteSpace(dto.password) || string.IsNullOrWhiteSpace(dto.name))
        {
            return BadRequest(new
            {
                Message = "Login i hasło są niepoprawne"
            });
        }
        if(dto.password.Length < 8 || dto.name.Length < 3)
        {
            return BadRequest(new
            {
                Message = "Hasło lub login nie spełniają wymogów"
            });
        }

        bool userExists = await _context.Users
            .AnyAsync(u => u.UserLogin.ToLower() == dto.name.ToLower());

        if (userExists)
        {
            return Conflict(new
            {
                Message = "Nazwa użytkownika zajeta"
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

    [HttpDelete("delete-acc")]
    [Authorize]
    public async Task<IActionResult> DeleteAcc()
    {
        var userIdClaims = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        if(!int.TryParse(userIdClaims, out int currentUserId))
        {
            return Unauthorized(new
            {
                Message = "Błędna wartość w tokenie"
            });
        }

        var user = _context.Users.Find(userIdClaims);
        if(user != null)
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

    [HttpPost("testing")]
    [Authorize]
    public async Task<IActionResult> TestingAuth()
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

        var user = _context.Users.Find(currentUserId);

        if(user == null)
        {
            return BadRequest(new
            {
                Message = "User nie znaleziony"
            });
        }

        return Ok(new
        {
            Message = "User znaleziony",
            Ranga = user.Rank,
            Username = user.UserLogin,
            CreatedAt = user.CreatedAt
        });
    }

    /// Caching póxneij na razie trzeba zając sie bazą danych
    //[HttpPost("refresh-token")]
    //[Authorize]
    //public async Task<IActionResult> RefreshToken(
    //    [FromBody] RefreshTokenRequestDto dto,
    //    [FromServices] IConnectionMultiplexer redis
    //    )
    //{
    //    if (string.IsNullOrWhiteSpace(dto.refreshToken))
    //    {
    //        return BadRequest(new
    //        {
    //            Message = "Token odświeżający jest wymagany"
    //        });
    //    }   
    //}
}