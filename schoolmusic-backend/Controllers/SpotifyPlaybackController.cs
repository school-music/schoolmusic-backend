using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers;

/// <summary>
/// Kontroler autoryzacji i integracji z usługą Spotify Connect dla szkolnego radiowęzła.
/// </summary>
[ApiController]
[Route("api/spotify-playback")]
[Authorize(Roles = "admin,head_admin")]
public sealed class SpotifyPlaybackController(ISpotifyConnectPlaybackService spotify) : ControllerBase
{
    /// <summary>
    /// Generuje adres autoryzacyjny Spotify OAuth 2.0 i przekierowuje do logowania konta radiowęzła.
    /// </summary>
    [HttpGet("connect")]
    public async Task<IActionResult> Connect(CancellationToken cancellationToken)
    {
        try
        {
            return Redirect(await spotify.CreateAuthorizationUrlAsync(cancellationToken));
        }
        catch (SpotifyPlaybackException ex)
        {
            return StatusCode(ex.StatusCode, new { Message = ex.Message });
        }
    }

    /// <summary>
    /// Punkt końcowy wywołania zwrotnego (OAuth Callback) po autoryzacji w serwisie Spotify.
    /// </summary>
    /// <param name="code">Kod autoryzacyjny zwrócony przez Spotify.</param>
    /// <param name="state">Stan weryfikacji zapobiegający atakom CSRF.</param>
    /// <param name="error">Ewentualny kod błędu autoryzacji Spotify.</param>
    /// <param name="cancellationToken">Token anulowania operacji.</param>
    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        try
        {
            await spotify.CompleteAuthorizationAsync(code, state, error, cancellationToken);
            return Content("Spotify playback account connected. You may close this page.", "text/plain");
        }
        catch (SpotifyPlaybackException ex)
        {
            return StatusCode(ex.StatusCode, new { Message = ex.Message });
        }
    }

    /// <summary>
    /// Pobiera status połączenia ze Spotify Connect (stan autoryzacji, aktywne urządzenie audio).
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken) =>
        Ok(await spotify.GetConnectionStatusAsync(cancellationToken));
}
