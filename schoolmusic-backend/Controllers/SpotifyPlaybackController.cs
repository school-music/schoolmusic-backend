using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers;

[ApiController]
[Route("api/spotify-playback")]
[Authorize(Roles = "admin,head_admin")]
public sealed class SpotifyPlaybackController(ISpotifyConnectPlaybackService spotify) : ControllerBase
{
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

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken) =>
        Ok(await spotify.GetConnectionStatusAsync(cancellationToken));
}
