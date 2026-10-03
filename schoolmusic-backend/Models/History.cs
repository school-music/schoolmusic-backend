using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class History
{
    public int Id { get; set; }

    public int SongId { get; set; }

    public DateTime PlayedAt { get; set; }

    public int BreakId { get; set; }

    public int Votes { get; set; }

    public virtual Break Break { get; set; } = null!;

    public virtual Song Song { get; set; } = null!;
}
