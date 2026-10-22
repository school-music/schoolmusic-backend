using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers;

public sealed record SetVolumeDto(int Volume);
public sealed record PlayFileDto(string PathOrUrl, string? Title, string? Artist);

/// <summary>
/// Kontroler sterowania odtwarzaczem radiowęzła szkolnego (Spotify Connect oraz lokalny silnik LibVLC).
/// </summary>
[ApiController]
[Route("api/player")]
[Authorize]
public sealed class PlayerController(
    ISpotifyConnectPlaybackService spotify,
    IAudioPlayerService audioPlayer,
    IBreakSchedulerService scheduler) : ControllerBase
{
    /// <summary>
    /// Pobiera aktualny stan odtwarzacza (tytuł, wykonawca, postęp, głośność, status odtwarzania).
    /// </summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var localStatus = audioPlayer.GetStatus();
        if (localStatus.Source != "None")
            return Ok(localStatus);

        try
        {
            return Ok(await spotify.GetStatusAsync(cancellationToken));
        }
        catch (SpotifyPlaybackException ex)
        {
            return StatusCode(ex.StatusCode, new { Message = ex.Message });
        }
    }

    /// <summary>
    /// Ustawia poziom głośności odtwarzacza (0-100%).
    /// </summary>
    [HttpPost("volume")]
    [Authorize(Roles = "mod,admin,head_admin")]
    public async Task<IActionResult> SetVolume([FromBody] SetVolumeDto dto, CancellationToken cancellationToken)
    {
        if (audioPlayer.GetStatus().Source != "None")
        {
            audioPlayer.SetVolume(dto.Volume);
            return Ok(audioPlayer.GetStatus());
        }

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

    /// <summary>
    /// Zwiększa głośność odtwarzacza o określoną liczbę procent (domyślnie 5%).
    /// </summary>
    [HttpPost("volume/up")]
    [Authorize(Roles = "mod,admin,head_admin")]
    public Task<IActionResult> VolumeUp([FromQuery] int step = 5, CancellationToken cancellationToken = default) =>
        AdjustVolumeAsync(Math.Clamp(step, 0, 100), cancellationToken);

    /// <summary>
    /// Zmniejsza głośność odtwarzacza o określoną liczbę procent (domyślnie 5%).
    /// </summary>
    [HttpPost("volume/down")]
    [Authorize(Roles = "mod,admin,head_admin")]
    public Task<IActionResult> VolumeDown([FromQuery] int step = 5, CancellationToken cancellationToken = default) =>
        AdjustVolumeAsync(-Math.Clamp(step, 0, 100), cancellationToken);

    /// <summary>
    /// Przesyła plik audio (MP3, WAV, OGG, FLAC) i natychmiast odtwarza go na radiowęźle.
    /// </summary>
    [HttpPost("upload-and-play")]
    [RequestSizeLimit(100_000_000)]
    [Authorize(Roles = "mod,admin,head_admin")]
    public async Task<IActionResult> UploadAndPlay(
        IFormFile file,
        [FromForm] string? title = null,
        [FromForm] string? artist = null)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { Message = "No file was uploaded." });

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a" or ".mp4"))
            return BadRequest(new { Message = "Unsupported format. Allowed: mp3, wav, ogg, flac, m4a, mp4." });

        var directory = Path.Combine(AppContext.BaseDirectory, "recordings");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid()}_{Path.GetFileName(file.FileName)}");
        await using (var stream = System.IO.File.Create(path))
            await file.CopyToAsync(stream);

        try
        {
            await spotify.PauseAsync();
        }
        catch (Exception ex) when (ex is SpotifyPlaybackException or HttpRequestException or InvalidOperationException)
        {
            // Local playback remains available when Spotify is disconnected.
        }

        return FromAudioResult(await audioPlayer.PlayFileOrUrlAsync(
            path,
            string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(file.FileName) : title,
            artist ?? "Unknown artist"));
    }

    /// <summary>
    /// Wstrzymuje aktualne odtwarzanie muzyki.
    /// </summary>
    [HttpPost("pause")]
    [Authorize(Roles = "mod,admin,head_admin")]
    public Task<IActionResult> Pause(CancellationToken cancellationToken) =>
        audioPlayer.GetStatus().Source != "None"
            ? AudioCommandAsync(audioPlayer.PauseAsync())
            : PlaybackCommandAsync(spotify.PauseAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// Wznawia wstrzymane odtwarzanie muzyki.
    /// </summary>
    [HttpPost("resume")]
    [Authorize(Roles = "mod,admin,head_admin")]
    public Task<IActionResult> Resume(CancellationToken cancellationToken) =>
        audioPlayer.GetStatus().Source != "None"
            ? AudioCommandAsync(audioPlayer.ResumeAsync())
            : PlaybackCommandAsync(spotify.ResumeAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// Odtwarza wskazany plik lokalny lub strumień z adresu URL.
    /// </summary>
    [HttpPost("play-file")]
    [Authorize(Roles = "mod,admin,head_admin")]
    public async Task<IActionResult> PlayFile([FromBody] PlayFileDto dto, CancellationToken cancellationToken)
    {
        try
        {
            await spotify.PauseAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is SpotifyPlaybackException or HttpRequestException or InvalidOperationException)
        {
            // Local playback must remain available when Spotify is disconnected.
        }

        return FromAudioResult(await audioPlayer.PlayFileOrUrlAsync(dto.PathOrUrl, dto.Title, dto.Artist));
    }

    /// <summary>
    /// Całkowicie zatrzymuje odtwarzanie dźwięku i resetuje stan.
    /// </summary>
    [HttpPost("stop")]
    [Authorize(Roles = "mod,admin,head_admin")]
    public async Task<IActionResult> Stop(CancellationToken cancellationToken)
    {
        var result = await audioPlayer.StopAsync();
        try
        {
            await spotify.PauseAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is SpotifyPlaybackException or HttpRequestException or InvalidOperationException)
        {
            // Stopping local playback does not depend on the Connect device being available.
        }

        return FromAudioResult(result);
    }

    /// <summary>
    /// Przełącza stan wyciszenia odtwarzacza (Mute/Unmute).
    /// </summary>
    [HttpPost("mute")]
    [Authorize(Roles = "mod,admin,head_admin")]
    public IActionResult ToggleMute()
    {
        audioPlayer.ToggleMute();
        return Ok(audioPlayer.GetStatus());
    }

    /// <summary>
    /// Pomija bieżący utwór w kolejce radiowęzła i przechodzi do kolejnego.
    /// </summary>
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
        if (audioPlayer.GetStatus().Source != "None")
        {
            audioPlayer.SetVolume(audioPlayer.GetVolume() + delta);
            return Ok(audioPlayer.GetStatus());
        }

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

    private async Task<IActionResult> AudioCommandAsync(Task<ServiceResult<PlayerStatusDto>> command) =>
        FromAudioResult(await command);

    private IActionResult FromAudioResult(ServiceResult<PlayerStatusDto> result) =>
        StatusCode(result.StatusCode, result.Success ? result.Data : new { Message = result.Message });
}
