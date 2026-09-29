using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("users")]
[Index("RankId", Name = "rank_id")]
[Index("UserLogin", Name = "user_login", IsUnique = true)]
public partial class User
{
    [Key]
    [Column("id", TypeName = "int(11)")]
    public int Id { get; set; }

    [Column("user_login")]
    public string UserLogin { get; set; } = null!;

    [Column("user_password")]
    [StringLength(255)]
    public string UserPassword { get; set; } = null!;

    [Column("rank_id", TypeName = "int(11)")]
    public int RankId { get; set; }

    [Column("created_at", TypeName = "datetime")]
    public DateTime CreatedAt { get; set; }

    [Column("password_changed_at", TypeName = "datetime")]
    public DateTime? PasswordChangedAt { get; set; }

    [InverseProperty("User")]
    public virtual ICollection<BannedUser> BannedUsers { get; set; } = new List<BannedUser>();

    [InverseProperty("User")]
    public virtual Moderator? Moderator { get; set; }

    [InverseProperty("User")]
    public virtual ICollection<QueueItem> QueueItems { get; set; } = new List<QueueItem>();

    [ForeignKey("RankId")]
    [InverseProperty("Users")]
    public virtual Rank Rank { get; set; } = null!;

    [InverseProperty("User")]
    public virtual ICollection<Vote> Votes { get; set; } = new List<Vote>();

    [ForeignKey("UserId")]
    [InverseProperty("Users")]
    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();
}
