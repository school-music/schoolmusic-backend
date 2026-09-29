using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("moderators")]
[Index("UserId", Name = "uniq_mod_user", IsUnique = true)]
public partial class Moderator
{
    [Key]
    [Column("id", TypeName = "int(11)")]
    public int Id { get; set; }

    [Column("user_id", TypeName = "int(11)")]
    public int UserId { get; set; }

    [Column("artists_banned", TypeName = "int(11)")]
    public int? ArtistsBanned { get; set; }

    [Column("songs_baned", TypeName = "int(11)")]
    public int? SongsBaned { get; set; }

    [Column("songs_approved", TypeName = "int(11)")]
    public int? SongsApproved { get; set; }

    [InverseProperty("Moderator")]
    public virtual ICollection<ArtistBlacklist> ArtistBlacklists { get; set; } = new List<ArtistBlacklist>();

    [InverseProperty("Moderator")]
    public virtual ICollection<BannedUser> BannedUsers { get; set; } = new List<BannedUser>();

    [InverseProperty("Moderator")]
    public virtual ICollection<QueueItem> QueueItems { get; set; } = new List<QueueItem>();

    [InverseProperty("Mod")]
    public virtual ICollection<SongBlacklist> SongBlacklists { get; set; } = new List<SongBlacklist>();

    [InverseProperty("Mod")]
    public virtual ICollection<SongGreenlist> SongGreenlists { get; set; } = new List<SongGreenlist>();

    [ForeignKey("UserId")]
    [InverseProperty("Moderator")]
    public virtual User User { get; set; } = null!;
}
