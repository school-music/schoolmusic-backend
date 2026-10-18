using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers;

public sealed record SetVolumeDto(int Volume);

[ApiController]
[Route("api/player")]
[Authorize]
public sealed class PlayerController(
    ISpotifyConnectPlaybackService spotify,
    IBreakSchedulerService scheduler) : ControllerBase
{
    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await spotify.GetStatusAsync(cancellationToken));
        }
        catch (SpotifyPlaybackException ex)
        {
            return StatusCode(ex.StatusCode, new { Message = ex.Message });
        }
    }

    [HttpPost("volume")]
    [Authorize(Roles = "mod,admin,head_admin")]
    public async Task<IActionResult> SetVolume([FromBody] SetVolumeDto dto, CancellationToken cancellationToken)
    {
        try
        {
            await spotify.SetVolumeAsync(dto.Volume, cancellationToken);
            return Ok(await spotify.GetStatusAsync(cancellationToken));
        }
        catch (SpotifyPlaybackException ex)
        {
            return StatusCode(ex.StatusCode, new { Message = ex.Message });
        }
    }

    [HttpPost("volume/up")]
    [Authorize(Roles = "mod,admin,head_admin")]
    public Task<IActionResult> VolumeUp([FromQuery] int step = 5, CancellationToken cancellationToken = default) =>
        AdjustVolumeAsync(Math.Clamp(step, 0, 100), cancellationToken);

    [HttpPost("volume/down")]
    [Authorize(Roles = "mod,admin,head_admin")]
    public Task<IActionResult> VolumeDown([FromQuery] int step = 5, CancellationToken cancellationToken = default) =>
        AdjustVolumeAsync(-Math.Clamp(step, 0, 100), cancellationToken);

    [HttpPost("pause")]
    [Authorize(Roles = "mod,admin,head_admin")]
    public Task<IActionResult> Pause(CancellationToken cancellationToken) =>
        PlaybackCommandAsync(spotify.PauseAsync(cancellationToken), cancellationToken);

    [HttpPost("resume")]
    [Authorize(Roles = "mod,admin,head_admin")]
    public Task<IActionResult> Resume(CancellationToken cancellationToken) =>
        PlaybackCommandAsync(spotify.ResumeAsync(cancellationToken), cancellationToken);

    [HttpPost("skip")]
    [Authorize(Roles = "mod,admin,head_admin")]
    public async Task<IActionResult> Skip(CancellationToken cancellationToken)
    {
        try
        {
            var skipped = await scheduler.SkipCurrentTrackAsync();
            return Ok(new { Success = skipped, PlayerStatus = await spotify.GetStatusAsync(cancellationToken) });
        }
        catch (SpotifyPlaybackException ex)
        {
            return StatusCode(ex.StatusCode, new { Message = ex.Message });
        }
    }

    private async Task<IActionResult> AdjustVolumeAsync(int delta, CancellationToken cancellationToken)
    {
        try
        {
            var status = await spotify.GetStatusAsync(cancellationToken);
            await spotify.SetVolumeAsync(status.Volume + delta, cancellationToken);
            return Ok(await spotify.GetStatusAsync(cancellationToken));
        }
        catch (SpotifyPlaybackException ex)
        {
            return StatusCode(ex.StatusCode, new { Message = ex.Message });
        }
    }

    private async Task<IActionResult> PlaybackCommandAsync(Task command, CancellationToken cancellationToken)
    {
        try
        {
            await command;
            return Ok(await spotify.GetStatusAsync(cancellationToken));
        }
        catch (SpotifyPlaybackException ex)
        {
            return StatusCode(ex.StatusCode, new { Message = ex.Message });
        }
    }
}
