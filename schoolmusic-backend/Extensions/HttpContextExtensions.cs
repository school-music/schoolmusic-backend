using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace schoolmusic_backend.Extensions;

public static class HttpContextExtensions {
    /// <summary>
    /// Zwraca id zalogowanego użytkownika lub null gdy uzytkownik nie istnieje lub id nie jest liczbą
    /// </summary>
    public static int? GetCurrentUserId(this HttpContext httpContext) {
        var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out int id) ? id : null;
    }
}
