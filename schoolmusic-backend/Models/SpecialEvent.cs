using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class SpecialEvent
{
    public int Id { get; set; }

    public string? Title { get; set; }

    public string FileUrl { get; set; } = null!;

    public DateTime StartsAt { get; set; }

    public string EventStatus { get; set; } = null!;
}
