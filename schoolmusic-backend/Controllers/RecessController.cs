using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Models;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

[Route("api/breaks")]
[ApiController]
[Authorize]
public class RecessController : ControllerBase
{
    private readonly schoolmusicContext _context;
    public RecessController(schoolmusicContext context)
    {
        _context = context;
    }

    [HttpGet("breaks")]
    public async Task<IActionResult> GetAllBreaks()
    {
        int userId = GetCurrentUserId() ?? 0;
        if (!await IsAdmin(userId))
        {
            return Unauthorized(new
            {
                Message = "Nie jesteœ zalogowany jako administrator",
                title = "Brak autoryzacji"
            });
        }

        var recess = await _context.Breaks.ToListAsync();
        return Ok(new
        {
            Message = "Wszystkie przerwy",
            Przerwy = recess
        });
    }

    [HttpGet("current-status")]
    public async Task<IActionResult> GetCurrentStatus()
    {
        int userId = GetCurrentUserId() ?? 0;
        if (!await IsAdmin(userId))
        {
            return Unauthorized(new
            {
                Message = "Nie jesteœ zalogowany jako administrator",
                title = "Brak autoryzacji"
            });
        }

        var currentSong = await _context.Breaks.Where(b => b.StartAt < DateTime.Now)
            .FirstOrDefaultAsync();
        return Ok(new
        {
            Message = "Aktualna najbli¿sza przerwa",
            Przerwa = currentSong
        });
    }


    private int? GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out int id) ? id : null;
    }

    private async Task<bool> IsAdmin(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return false;
        }
        if (user.RankId != 3)
        {
            return false;
        }
        return true;
    }
}