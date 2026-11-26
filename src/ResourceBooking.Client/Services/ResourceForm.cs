using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Client.Services;

/// <summary>The resource form's fields in the units an admin types (minutes, hours, days); empty means no limit.</summary>
public sealed class ResourceForm
{
    public ResourceType Type { get; set; } = ResourceType.MeetingRoom;
    public string Name { get; set; } = "";
    public int Capacity { get; set; } = 1;
    public int Units { get; set; } = 1;
    public TimeOnly? OpensAt { get; set; }
    public TimeOnly? ClosesAt { get; set; }
    public int? MinMinutes { get; set; }
    public double? MaxHours { get; set; }
    public double? NoticeHours { get; set; }
    public int? MaxDaysAhead { get; set; }
    public int? GapMinutes { get; set; }
    public int? MaxFuturePerUser { get; set; }

    public ResourceRequest ToRequest() => new()
    {
        Type = Type,
        Name = Name,
        Capacity = Capacity,
        Units = Units,
        OpensAt = OpensAt,
        ClosesAt = ClosesAt,
        MinDuration = MinMinutes is { } min ? TimeSpan.FromMinutes(min) : null,
        MaxDuration = MaxHours is { } max ? TimeSpan.FromHours(max) : null,
        MinNotice = NoticeHours is { } notice ? TimeSpan.FromHours(notice) : null,
        MaxDaysAhead = MaxDaysAhead,
        Turnaround = GapMinutes is { } gap ? TimeSpan.FromMinutes(gap) : null,
        MaxFuturePerUser = MaxFuturePerUser,
    };

    public static ResourceForm From(ResourceDto r) => new()
    {
        Type = r.Type,
        Name = r.Name,
        Capacity = r.Capacity,
        Units = r.Units,
        OpensAt = r.OpensAt,
        ClosesAt = r.ClosesAt,
        MinMinutes = r.MinDuration is { } min ? (int)min.TotalMinutes : null,
        MaxHours = r.MaxDuration?.TotalHours,
        NoticeHours = r.MinNotice?.TotalHours,
        MaxDaysAhead = r.MaxDaysAhead,
        GapMinutes = r.Turnaround is { } gap ? (int)gap.TotalMinutes : null,
        MaxFuturePerUser = r.MaxFuturePerUser,
    };
}
