using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("refresh_tokens")]
[Index("TokenHash", Name = "uniq_refresh_token_hash", IsUnique = true)]
[Index("UserId", Name = "idx_refresh_tokens_user")]
[Index("FamilyId", Name = "idx_refresh_tokens_family")]
public partial class RefreshToken
{
    [Key]
    [Column("id", TypeName = "int(11)")]
    public int Id { get; set; }

    [Column("user_id", TypeName = "int(11)")]
    public int UserId { get; set; }

    [Column("token_hash")]
    [StringLength(64)]
    public string TokenHash { get; set; } = null!;

    [Column("family_id", TypeName = "char(36)")]
    public Guid FamilyId { get; set; }

    [Column("expires_at", TypeName = "datetime")]
    public DateTime ExpiresAt { get; set; }

    [Column("revoked_at", TypeName = "datetime")]
    public DateTime? RevokedAt { get; set; }

    [Column("replaced_by_id", TypeName = "int(11)")]
    public int? ReplacedById { get; set; }

    [Column("created_at", TypeName = "datetime")]
    public DateTime CreatedAt { get; set; }

    [ForeignKey("UserId")]
    [InverseProperty("RefreshTokens")]
    public virtual User User { get; set; } = null!;
}
