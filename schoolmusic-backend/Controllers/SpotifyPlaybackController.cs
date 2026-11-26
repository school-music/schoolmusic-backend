using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers;

/// <summary>
/// Kontroler autoryzacji i integracji z usługą Spotify Connect dla szkolnego radiowęzła.
/// </summary>
[ApiController]
[Route("api/spotify-playback")]
[Authorize(Roles = "admin,head_admin")]
[Produces("application/json")]
public sealed class SpotifyPlaybackController(
    ISpotifyConnectPlaybackService spotify,
    IBreakSchedulerService? scheduler = null) : ControllerBase
{
    /// <summary>
    /// Generuje adres autoryzacyjny Spotify OAuth 2.0 i przekierowuje do logowania konta radiowęzła.
    /// </summary>
    [HttpGet("connect")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Connect(CancellationToken cancellationToken)
    {
        try
        {
            return Redirect(await spotify.CreateAuthorizationUrlAsync(cancellationToken));
        }
        catch (SpotifyPlaybackException ex)
        {
            return this.Create(ex.StatusCode, ex.Message);
        }
    }

    /// <summary>
    /// Punkt końcowy wywołania zwrotnego (OAuth Callback) po autoryzacji w serwisie Spotify.
    /// </summary>
    [HttpGet("callback")]
    [AllowAnonymous]
    [Produces("text/plain", "application/problem+json")]
    [ProducesResponseType<string>(StatusCodes.Status200OK, "text/plain")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        try
        {
            await spotify.CompleteAuthorizationAsync(code, state, error, cancellationToken);
            scheduler?.NotifyPlaybackConfigurationChanged();
            return Content("Spotify playback account connected. You may close this page.", "text/plain");
        }
        catch (SpotifyPlaybackException ex)
        {
            return this.Create(ex.StatusCode, ex.Message);
        }
    }

    /// <summary>
    /// Pobiera status połączenia ze Spotify Connect (stan autoryzacji, aktywne urządzenie audio).
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType<SpotifyConnectionStatus>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Status(CancellationToken cancellationToken) =>
        Ok(await spotify.GetConnectionStatusAsync(cancellationToken));
}
