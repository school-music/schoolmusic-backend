using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class DefaultSong
{
    public int Id { get; set; }

    public int SongId { get; set; }

    public virtual Song Song { get; set; } = null!;
}
