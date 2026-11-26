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
            nameof(Booking) when NewValue(root, nameof(Booking.Status)) is { ValueKind: JsonValueKind.String } status
                && status.GetString() == "Cancelled" => "cancelled",
            nameof(Resource) when NewValue(root, nameof(Resource.LastDay)) is { ValueKind: not JsonValueKind.Null } => "retired",
            nameof(User) when NewValue(root, nameof(User.IsActive)) is { ValueKind: JsonValueKind.False } => "deactivated",
            _ => "changed",
        };
    }

    /// <summary>The new value of a changed field: the interceptor writes each change as [old, new].</summary>
    private static JsonElement? NewValue(JsonElement changes, string field) =>
        changes.TryGetProperty(field, out var pair) ? pair[1] : null;
}
