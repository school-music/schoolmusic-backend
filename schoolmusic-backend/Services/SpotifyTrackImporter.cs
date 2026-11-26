using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Services;

internal static class SpotifyTrackImporter
{
    public static async Task<Song?> GetOrImportAsync(schoolmusicContext context, ISongSourceService songSource, string spotifyId)
    {
        var normalizedId = NormalizeSpotifyTrackId(spotifyId);
        var existingSong = await context.Songs
            .Include(song => song.Artist)
            .FirstOrDefaultAsync(song => song.SpotifyId == normalizedId || song.SpotifyId == spotifyId);
        if (existingSong != null)
        {
            return existingSong;
        }

        var externalTrack = await songSource.GetTrackByIdAsync(normalizedId);
        if (externalTrack == null)
        {
            return null;
        }

        var canonicalSpotifyId = NormalizeSpotifyTrackId(externalTrack.ExternalId);
        var existingByCanonical = await context.Songs
            .Include(song => song.Artist)
            .FirstOrDefaultAsync(song => song.SpotifyId == canonicalSpotifyId);
        if (existingByCanonical != null)
        {
            return existingByCanonical;
        }

        var artistName = externalTrack.ArtistName.Trim();
        var artist = await context.Artists
            .FirstOrDefaultAsync(candidate => candidate.Name.ToLower() == artistName.ToLower());

        if (artist == null)
        {
            artist = new Artist { Name = artistName, Source = "spotify" };
            context.Artists.Add(artist);
        }

        var song = new Song
        {
            SpotifyId = canonicalSpotifyId,
            Source = "spotify",
            Title = externalTrack.Title,
            Artist = artist,
            Cover = externalTrack.CoverUrl,
            SongUrl = externalTrack.PreviewUrl,
            DurationMs = externalTrack.DurationMs,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        context.Songs.Add(song);

        try
        {
            await context.SaveChangesAsync();
            return song;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation())
        {
            context.Entry(song).State = EntityState.Detached;
            if (artist.Id == 0)
            {
                context.Entry(artist).State = EntityState.Detached;
            }

            return await context.Songs
                .Include(candidate => candidate.Artist)
                .FirstOrDefaultAsync(candidate => candidate.SpotifyId == canonicalSpotifyId);
        }
    }

    internal static string NormalizeSpotifyTrackId(string raw)
    {
        var cleanId = raw.Trim();
        if (cleanId.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
        {
            cleanId = cleanId["spotify:track:".Length..];
        }
        else if (cleanId.Contains("/track/", StringComparison.OrdinalIgnoreCase))
        {
            var part = cleanId[(cleanId.IndexOf("/track/", StringComparison.OrdinalIgnoreCase) + 7)..];
            var qIndex = part.IndexOf('?');
            cleanId = qIndex >= 0 ? part[..qIndex] : part;
        }
        return cleanId;
    }
}
