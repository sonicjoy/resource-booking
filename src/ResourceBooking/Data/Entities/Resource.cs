using ResourceBooking.Shared;

namespace ResourceBooking.Data.Entities;

public class Resource
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public ResourceType Type { get; set; }

    /// <summary>Seats, or for a kitchen the portions allowed per order.</summary>
    public int Capacity { get; set; }

    /// <summary>Orders per day for a kitchen; 1 for every other resource.</summary>
    public int Units { get; set; } = 1;

    public DateOnly? LastDay { get; set; }

    // Booking rules. Null means the rule is off.
    public TimeOnly? OpensAt { get; set; }
    public TimeOnly? ClosesAt { get; set; }
    public TimeSpan? MinDuration { get; set; }
    public TimeSpan? MaxDuration { get; set; }
    public TimeSpan? MinNotice { get; set; }
    public int? MaxDaysAhead { get; set; }
    public TimeSpan? Turnaround { get; set; }
    public int? MaxFuturePerUser { get; set; }
}
