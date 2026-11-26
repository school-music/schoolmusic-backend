using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("email_verification_tokens")]
[Index("TokenHash", Name = "uniq_email_verification_token_hash", IsUnique = true)]
[Index("UserId", Name = "idx_email_verification_user")]
public partial class EmailVerificationToken
{
    [Key]
    [Column("id", TypeName = "int(11)")]
    public int Id { get; set; }

    [Column("user_id", TypeName = "int(11)")]
    public int UserId { get; set; }

    [Column("email")]
    [StringLength(254)]
    public string Email { get; set; } = null!;

    [Column("token_hash")]
    [StringLength(64)]
    public string TokenHash { get; set; } = null!;

    [Column("expires_at", TypeName = "datetime")]
    public DateTime ExpiresAt { get; set; }

    [Column("used_at", TypeName = "datetime")]
    public DateTime? UsedAt { get; set; }

    [Column("created_at", TypeName = "datetime")]
    public DateTime CreatedAt { get; set; }

    [ForeignKey("UserId")]
    [InverseProperty("EmailVerificationTokens")]
    public virtual User User { get; set; } = null!;
}
