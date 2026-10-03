using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class ExceptionBreak
{
    public int Id { get; set; }

    public DateTime StartsAt { get; set; }

    public DateTime? EndsAt { get; set; }

    public string? Description { get; set; }
}
