using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("users")]
[Index("RankId", Name = "idx_users_rank")]
[Index("UserLogin", Name = "uniq_user_login", IsUnique = true)]
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

    [Column("display_name")]
    [StringLength(80)]
    public string? DisplayName { get; set; }

    [Column("email")]
    [StringLength(254)]
    public string? Email { get; set; }

    [Column("email_verified_at", TypeName = "datetime")]
    public DateTime? EmailVerifiedAt { get; set; }

    [Column("auth_provider", TypeName = "enum('local','ldap')")]
    public string AuthProvider { get; set; } = "local";

    [Column("token_version", TypeName = "int(11)")]
    public int TokenVersion { get; set; } = 1;

    [Column("rank_id", TypeName = "int(11)")]
    public int RankId { get; set; }

    [Column("created_at", TypeName = "datetime")]
    public DateTime CreatedAt { get; set; }

    [Column("password_changed_at", TypeName = "datetime")]
    public DateTime? PasswordChangedAt { get; set; }

    [InverseProperty("User")]
    public virtual ICollection<BannedUser> BannedUsers { get; set; } = new List<BannedUser>();

    [InverseProperty("User")]
    public virtual ICollection<EmailVerificationToken> EmailVerificationTokens { get; set; } = new List<EmailVerificationToken>();

    [InverseProperty("User")]
    public virtual Moderator? Moderator { get; set; }

    [InverseProperty("User")]
    public virtual ICollection<PasswordResetToken> PasswordResetTokens { get; set; } = new List<PasswordResetToken>();

    [InverseProperty("User")]
    public virtual ICollection<QueueItem> QueueItems { get; set; } = new List<QueueItem>();

    [ForeignKey("RankId")]
    [InverseProperty("Users")]
    public virtual Rank Rank { get; set; } = null!;

    [InverseProperty("User")]
    public virtual ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

    [InverseProperty("User")]
    public virtual ICollection<Vote> Votes { get; set; } = new List<Vote>();

    [ForeignKey("UserId")]
    [InverseProperty("Users")]
    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();
}
