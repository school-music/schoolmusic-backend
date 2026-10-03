using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class Song
{
    public int Id { get; set; }

    public string SpotifyId { get; set; } = null!;

    public string Title { get; set; } = null!;

    public int ArtistId { get; set; }

    public string Cover { get; set; } = null!;

    public int DurationMs { get; set; }

    public virtual Artist Artist { get; set; } = null!;

    public virtual ICollection<DefaultSong> DefaultSongs { get; set; } = new List<DefaultSong>();

    public virtual ICollection<History> Histories { get; set; } = new List<History>();

    public virtual ICollection<QueueItem> QueueItems { get; set; } = new List<QueueItem>();

    public virtual SongBlacklist? SongBlacklist { get; set; }

    public virtual SongGreenlist? SongGreenlist { get; set; }

    public virtual ICollection<User> Users { get; set; } = new List<User>();
}
