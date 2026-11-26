using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ResourceBooking.Bookings;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Reports;

/// <summary>Audit events in words, newest first: who did what to which user, resource or booking.</summary>
public sealed class History(AppDb db, TimeZoneInfo zone)
{
    public const int Limit = 500;
    public static readonly IReadOnlySet<string> EntityTypes = new HashSet<string> { nameof(User), nameof(Resource), nameof(Booking) };

    public async Task<HistoryPage> ListAsync(string? entityType, Guid? entityId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var query = db.AuditEvents.AsNoTracking();
        if (entityType is not null)
        {
            query = query.Where(e => e.EntityType == entityType);
        }
        if (entityId is { } id)
        {
            query = query.Where(e => e.EntityId == id);
        }
        if (from is { } first)
        {
            var start = CompanyTime.StartOfDay(first, zone);
            query = query.Where(e => e.At >= start);
        }
        if (to is { } last)
        {
            var end = CompanyTime.StartOfDay(last.AddDays(1), zone);
            query = query.Where(e => e.At < end);
        }
        var events = await query.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Take(Limit + 1).ToListAsync(ct);
        var more = events.Count > Limit;
        events = events.Take(Limit).ToList();

        var userIds = events.Where(e => e.EntityType == nameof(User)).Select(e => e.EntityId)
            .Concat(events.Select(e => e.ActorId).OfType<Guid>()).ToHashSet();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var bookingIds = events.Where(e => e.EntityType == nameof(Booking)).Select(e => e.EntityId).ToHashSet();
        var bookings = await db.Bookings.AsNoTracking().Include(b => b.Resource).Where(b => bookingIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var resourceIds = events.Where(e => e.EntityType == nameof(Resource)).Select(e => e.EntityId).ToHashSet();
        var resources = await db.Resources.AsNoTracking().Where(r => resourceIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);

        string Name(AuditEvent e) => e.EntityType switch
        {
            nameof(User) => users.GetValueOrDefault(e.EntityId, "A removed user"),
            nameof(Resource) => resources.GetValueOrDefault(e.EntityId, "A removed resource"),
            nameof(Booking) when bookings.TryGetValue(e.EntityId, out var b) =>
                $"{b.Resource!.Name}, {CompanyTime.ToLocal(b.DeliveryAt ?? b.StartsAt, zone).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)}",
            nameof(Booking) => "A removed booking",
            _ => e.EntityType,
        };

        var items = events.Select(e => new HistoryItem(
            CompanyTime.ToLocal(e.At, zone),
            e.ActorId is { } actor ? users.GetValueOrDefault(actor, "A removed user") : "System",
            e.EntityType,
            e.EntityId,
            Name(e),
            HistoryLabels.Verb(e),
            Changes(e.Changes))).ToList();
        return new HistoryPage(items, more);
    }

    /// <summary>The interceptor's {"Field": [old, new]} JSON as field changes, values in their JSON text.</summary>
    private static List<FieldChange> Changes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject()
            .Select(p => new FieldChange(p.Name, Text(p.Value[0]), Text(p.Value[1])))
            .ToList();
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };
}
