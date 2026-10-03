using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class ArtistBlacklist
{
    public int Id { get; set; }

    public int ArtistId { get; set; }

    public int ModeratorId { get; set; }

    public virtual Artist Artist { get; set; } = null!;

    public virtual Moderator Moderator { get; set; } = null!;
}
