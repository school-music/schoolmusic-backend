using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("users")]
[Index("RankId", Name = "idx_users_rank")]
[Index("UserLogin", Name = "uniq_user_login", IsUnique = true)]
[Index("LdapDn", Name = "uniq_users_ldap_dn", IsUnique = true)]
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

    /// <summary>
    /// Distinguished Name konta w szkolnym katalogu LDAP. Wartość pusta oznacza konto lokalne,
    /// którego nie wolno uwierzytelniać przez katalog.
    /// </summary>
    [Column("ldap_dn")]
    [StringLength(512)]
    public string? LdapDn { get; set; }

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
