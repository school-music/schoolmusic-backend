using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class Moderator
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public int? ArtistsBanned { get; set; }

    public int? SongsBaned { get; set; }

    public int? SongsApproved { get; set; }

    public virtual ICollection<ArtistBlacklist> ArtistBlacklists { get; set; } = new List<ArtistBlacklist>();

    public virtual ICollection<BannedUser> BannedUsers { get; set; } = new List<BannedUser>();

    public virtual ICollection<QueueItem> QueueItems { get; set; } = new List<QueueItem>();

    public virtual ICollection<SongBlacklist> SongBlacklists { get; set; } = new List<SongBlacklist>();

    public virtual ICollection<SongGreenlist> SongGreenlists { get; set; } = new List<SongGreenlist>();

    public virtual User User { get; set; } = null!;
}
