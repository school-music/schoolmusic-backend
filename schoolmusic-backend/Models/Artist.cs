using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class Artist
{
    public int Id { get; set; }

    public string SpotifyArtistId { get; set; } = null!;

    public string? Name { get; set; }

    public virtual ArtistBlacklist? ArtistBlacklist { get; set; }

    public virtual ICollection<SongBlacklist> SongBlacklists { get; set; } = new List<SongBlacklist>();

    public virtual ICollection<SongGreenlist> SongGreenlists { get; set; } = new List<SongGreenlist>();

    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();

    public override string ToString()
    {
        return this.Name ?? "Unknown";
    }
}
