using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class Rank
{
    public int Id { get; set; }

    public string Rank1 { get; set; } = null!;

    public virtual ICollection<User> Users { get; set; } = new List<User>();
}
