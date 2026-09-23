using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace schoolmusic_backend.Models
{
    [Table("users")]
    public class User
    {
        [Column("id")]
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }
        [Column("login")]
        public string? Login { get; set; }
        [Column("password")]
        public string? Password { get; set; }
        [Column("skip")]
        public bool Skip {  get; set; }
        [Column("czyAdmin")]
        public bool czyAdmin { get; set; }

        public User()
        {

        }
    }
}
