namespace ResourceBooking.Shared.Dtos;

// All times on the wire are company-local wall-clock times with no offset ("2026-11-26T14:00").

public sealed record BookingDto(
    Guid Id,
    Guid ResourceId,
    string ResourceName,
    ResourceType ResourceType,
    DateTime Start,
    DateTime End,
    int Attendees,
    string? Title,
    DateTime? DeliveryAt,
    Guid? DeliveryRoomId,
    BookingStatus Status);

/// <summary>A busy stretch on the overview. Owner and title are shown only to admins and to the booking's owner.</summary>
public sealed record BusyBlock(DateTime Start, DateTime End, bool Mine, string? Title, string? Owner);

public sealed record ResourceAvailability(
    Guid Id,
    string Name,
    ResourceType Type,
    int Capacity,
    TimeOnly? OpensAt,
    TimeOnly? ClosesAt,
    int Units,
    int FreeUnits,
    IReadOnlyList<BusyBlock> Busy);

public sealed record AvailabilityDay(DateOnly Date, IReadOnlyList<ResourceAvailability> Resources);

public sealed record ResourceDto(
    Guid Id,
    string Name,
    ResourceType Type,
    int Capacity,
    int Units,
    DateOnly? LastDay,
    TimeOnly? OpensAt,
    TimeOnly? ClosesAt,
    TimeSpan? MinDuration,
    TimeSpan? MaxDuration,
    TimeSpan? MinNotice,
    int? MaxDaysAhead,
    TimeSpan? Turnaround,
    int? MaxFuturePerUser);

public sealed record NoticeDto(Guid Id, Guid BookingId, string Message, DateTime CreatedAt, bool Read);
