using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("default_songs")]
[Index("SongId", Name = "song_id")]
public partial class DefaultSong
{
    [Key]
    [Column("id", TypeName = "int(11)")]
    public int Id { get; set; }

    [Column("song_id", TypeName = "int(11)")]
    public int SongId { get; set; }

    [ForeignKey("SongId")]
    [InverseProperty("DefaultSongs")]
    public virtual Song Song { get; set; } = null!;
}
