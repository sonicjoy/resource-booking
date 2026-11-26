using Microsoft.EntityFrameworkCore;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;

namespace ResourceBooking.Data;

/// <summary>
/// Demo data for the user journeys. Ids are fixed so sessions survive a reseed;
/// dates are relative to today in the company time zone.
/// </summary>
public static class Seed
{
    public static readonly Guid AnaId = new("0193a000-0000-7000-8000-000000000001");
    public static readonly Guid BenId = new("0193a000-0000-7000-8000-000000000002");
    public static readonly Guid SamId = new("0193a000-0000-7000-8000-000000000003");

    public static readonly Guid ThamesId = new("0193a000-0000-7000-8000-000000000101");
    public static readonly Guid SevernId = new("0193a000-0000-7000-8000-000000000102");
    public static readonly Guid PoolCar1Id = new("0193a000-0000-7000-8000-000000000103");
    public static readonly Guid MainKitchenId = new("0193a000-0000-7000-8000-000000000104");

    private const string Domain = "sonicjoy-solutions.com";

    /// <summary>
    /// Empties every app table except token usage and the Data Protection keys, then seeds again,
    /// in one transaction. The only bulk delete in the app, so the audit interceptor never sees it.
    /// </summary>
    public static async Task ReseedAsync(AppDb db, TimeProvider clock, TimeZoneInfo zone, CancellationToken ct)
    {
        // Seeding is the system's write, whoever asked for it; the endpoint logs who did.
        using var asSystem = AuditInterceptor.AsSystem();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE notices, proposals, assistant_steps, audit_events, bookings, resources, users", ct);
        db.ChangeTracker.Clear();
        await SeedIfEmptyAsync(db, clock, zone, ct);
        await transaction.CommitAsync(ct);
    }

    public static async Task SeedIfEmptyAsync(AppDb db, TimeProvider clock, TimeZoneInfo zone, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct))
        {
            return;
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        DateTimeOffset At(int dayOffset, int hour, int minute = 0) =>
            new(TimeZoneInfo.ConvertTimeToUtc(today.AddDays(dayOffset).ToDateTime(new TimeOnly(hour, minute)), zone));

        db.Users.AddRange(
            new User { Id = AnaId, Email = $"ana@{Domain}", DisplayName = "Ana", Role = Role.Employee, IsActive = true },
            new User { Id = BenId, Email = $"ben@{Domain}", DisplayName = "Ben", Role = Role.Employee, IsActive = true },
            new User { Id = SamId, Email = $"sam@{Domain}", DisplayName = "Sam", Role = Role.Admin, IsActive = true });

        var thames = new Resource
        {
            Id = ThamesId,
            Name = "Thames",
            Type = ResourceType.MeetingRoom,
            Capacity = 8,
            OpensAt = new TimeOnly(8, 0),
            ClosesAt = new TimeOnly(18, 0),
            MaxDuration = TimeSpan.FromHours(4),
        };
        var severn = new Resource
        {
            Id = SevernId,
            Name = "Severn",
            Type = ResourceType.MeetingRoom,
            Capacity = 12,
            OpensAt = new TimeOnly(8, 0),
            ClosesAt = new TimeOnly(18, 0),
        };
        var car = new Resource
        {
            Id = PoolCar1Id,
            Name = "Pool Car 1",
            Type = ResourceType.Car,
            Capacity = 4,
            OpensAt = new TimeOnly(7, 0),
            ClosesAt = new TimeOnly(19, 0),
            MaxDuration = TimeSpan.FromDays(3),
            Turnaround = TimeSpan.FromMinutes(60),
        };
        var kitchen = new Resource
        {
            Id = MainKitchenId,
            Name = "Main Kitchen",
            Type = ResourceType.Catering,
            Capacity = 25,
            Units = 5,
            OpensAt = new TimeOnly(8, 0),
            ClosesAt = new TimeOnly(16, 0),
        };
        db.Resources.AddRange(thames, severn, car, kitchen);

        var bookingNo = 0;
        Guid NextBookingId() => new($"0193a000-0000-7000-8000-{0x200 + ++bookingNo:x12}");

        Booking Book(Resource resource, Guid userId, DateTimeOffset start, DateTimeOffset end, int attendees, string? title = null) => new()
        {
            Id = NextBookingId(),
            ResourceId = resource.Id,
            UserId = userId,
            Title = title,
            StartsAt = start,
            EndsAt = end,
            BlockedUntil = end + (resource.Turnaround ?? TimeSpan.Zero),
            Attendees = attendees,
            Status = BookingStatus.Confirmed,
        };

        Booking Lunch(int dayOffset, int unit, Guid userId, int portions, Resource room, int hour) => new()
        {
            Id = NextBookingId(),
            ResourceId = kitchen.Id,
            UserId = userId,
            UnitNo = unit,
            StartsAt = At(dayOffset, 0),
            EndsAt = At(dayOffset + 1, 0),
            BlockedUntil = At(dayOffset + 1, 0),
            Attendees = portions,
            DeliveryAt = At(dayOffset, hour),
            DeliveryRoomId = room.Id,
            Status = BookingStatus.Confirmed,
        };

        db.Bookings.AddRange(
            // Future: CUJ 2 tries Pool Car 1 inside this booking's gap; CUJ 4 cancels Ana's Severn
            // booking and Ben's two bookings.
            Book(car, BenId, At(1, 9), At(1, 11), 2, "Site visit"),
            Book(thames, BenId, At(2, 9), At(2, 10), 4, "Planning"),
            Book(severn, AnaId, At(3, 10), At(3, 11), 6, "Quarterly review"),

            // Last week, for utilisation and history.
            Book(thames, AnaId, At(-7, 10), At(-7, 12), 6, "Design review"),
            Book(thames, BenId, At(-6, 14), At(-6, 16), 5, "Sprint planning"),
            Book(thames, AnaId, At(-4, 9), At(-4, 11), 8, "Workshop"),
            Book(severn, SamId, At(-5, 13), At(-5, 17), 10, "All hands"),
            Book(car, AnaId, At(-6, 8), At(-4, 17), 3, "Client tour"),
            Lunch(-4, 0, AnaId, 8, thames, 12));

        await db.SaveChangesAsync(ct);
    }
}
