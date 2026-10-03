using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class BannedUser
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public string? Reason { get; set; }

    public int ModeratorId { get; set; }

    public DateTime BannedAt { get; set; }

    public virtual Moderator Moderator { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
