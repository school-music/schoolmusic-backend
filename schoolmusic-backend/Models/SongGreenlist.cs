using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class SongGreenlist
{
    public int Id { get; set; }

    public int SongId { get; set; }

    public int ArtistId { get; set; }

    public int ModId { get; set; }

    public virtual Artist Artist { get; set; } = null!;

    public virtual Moderator Mod { get; set; } = null!;

    public virtual ICollection<QueueItem> QueueItems { get; set; } = new List<QueueItem>();

    public virtual Song Song { get; set; } = null!;
}
