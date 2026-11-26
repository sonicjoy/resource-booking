using System.Text.Json;
using ResourceBooking.Data.Entities;

namespace ResourceBooking.Reports;

/// <summary>Turns an audit event into the word History shows for it.</summary>
public static class HistoryLabels
{
    public static string Verb(AuditEvent auditEvent)
    {
        if (auditEvent.Action == "Added")
        {
            return auditEvent.EntityType == nameof(Booking) ? "booked" : "added";
        }

        using var changes = JsonDocument.Parse(auditEvent.Changes);
        var root = changes.RootElement;
        return auditEvent.EntityType switch
        {
            nameof(Booking) when NewValue(root, nameof(Booking.Status)) is "Cancelled" => "cancelled",
            nameof(Resource) when root.TryGetProperty(nameof(Resource.LastDay), out var lastDay)
                && lastDay[1].ValueKind != JsonValueKind.Null => "retired",
            nameof(User) when root.TryGetProperty(nameof(User.IsActive), out var active)
                && active[1].ValueKind == JsonValueKind.False => "deactivated",
            _ => "changed",
        };
    }

    private static string? NewValue(JsonElement changes, string field) =>
        changes.TryGetProperty(field, out var pair) && pair[1].ValueKind == JsonValueKind.String ? pair[1].GetString() : null;
}
