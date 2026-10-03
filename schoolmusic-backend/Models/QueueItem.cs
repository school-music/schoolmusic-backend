using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class QueueItem
{
    public int Id { get; set; }

    public int BreakId { get; set; }

    public int SongId { get; set; }

    public int UserId { get; set; }

    public int? GreenlistId { get; set; }

    public int? BlacklistId { get; set; }

    public int? ModeratorId { get; set; }

    public string? ModerationStatus { get; set; }

    public int OrderIndex { get; set; }

    public virtual SongBlacklist? Blacklist { get; set; }

    public virtual Break Break { get; set; } = null!;

    public virtual SongGreenlist? Greenlist { get; set; }

    public virtual Moderator? Moderator { get; set; }

    public virtual Song Song { get; set; } = null!;

    public virtual User User { get; set; } = null!;

    public virtual ICollection<Vote> Votes { get; set; } = new List<Vote>();
}
