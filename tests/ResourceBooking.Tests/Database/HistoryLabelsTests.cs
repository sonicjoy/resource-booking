using ResourceBooking.Data.Entities;
using ResourceBooking.Reports;

namespace ResourceBooking.Tests.Database;

/// <summary>History shows audit events in words, as the design's Audit section lists them.</summary>
public class HistoryLabelsTests
{
    private static AuditEvent Event(string type, string action, string changes) => new()
    {
        EntityType = type,
        Action = action,
        Changes = changes,
        EntityId = Guid.NewGuid(),
    };

    [Theory]
    [InlineData("Booking", "Added", "{}", "booked")]
    [InlineData("Booking", "Modified", """{"Status":["Confirmed","Cancelled"]}""", "cancelled")]
    [InlineData("Booking", "Modified", """{"StartsAt":["2026-11-26T10:00:00+00:00","2026-11-26T11:00:00+00:00"]}""", "changed")]
    [InlineData("Resource", "Added", "{}", "added")]
    [InlineData("Resource", "Modified", """{"LastDay":[null,"2026-11-27"]}""", "retired")]
    [InlineData("Resource", "Modified", """{"Capacity":[8,10]}""", "changed")]
    [InlineData("User", "Added", "{}", "added")]
    [InlineData("User", "Modified", """{"IsActive":[true,false]}""", "deactivated")]
    [InlineData("User", "Modified", """{"Role":["Employee","Admin"]}""", "changed")]
    [InlineData("User", "Modified", """{"IsActive":[false,true]}""", "changed")]
    [InlineData("Resource", "Modified", """{"LastDay":["2026-11-27",null]}""", "changed")]
    public void Each_event_has_a_verb(string type, string action, string changes, string verb) =>
        Assert.Equal(verb, HistoryLabels.Verb(Event(type, action, changes)));
}
