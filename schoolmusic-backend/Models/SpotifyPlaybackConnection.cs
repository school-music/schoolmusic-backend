using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace schoolmusic_backend.Models;

[Table("spotify_playback_connection")]
public sealed class SpotifyPlaybackConnection
{
    [Key]
    [Column("id", TypeName = "int")]
    public int Id { get; set; }

    [Required]
    [Column("spotify_user_id")]
    [StringLength(128)]
    public string SpotifyUserId { get; set; } = null!;

    [Required]
    [Column("refresh_token_ciphertext", TypeName = "text")]
    public string RefreshTokenCiphertext { get; set; } = null!;

    [Column("refresh_token_expires_at", TypeName = "datetime")]
    public DateTime RefreshTokenExpiresAt { get; set; }

    [Column("created_at", TypeName = "datetime")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at", TypeName = "datetime")]
    public DateTime UpdatedAt { get; set; }
}
