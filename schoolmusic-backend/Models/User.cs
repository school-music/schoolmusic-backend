using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class User
{
    public int Id { get; set; }

    public string UserLogin { get; set; } = null!;

    public string UserPassword { get; set; } = null!;

    public int RankId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? PasswordChangedAt { get; set; }

    public virtual ICollection<BannedUser> BannedUsers { get; set; } = new List<BannedUser>();

    public virtual Moderator? Moderator { get; set; }

    public virtual ICollection<QueueItem> QueueItems { get; set; } = new List<QueueItem>();

    public virtual Rank Rank { get; set; } = null!;

    public virtual ICollection<Vote> Votes { get; set; } = new List<Vote>();

    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();
}
