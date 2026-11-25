namespace ResourceBooking.Data.Entities;

public class AuditEvent
{
    public Guid Id { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>Null when the system wrote the change (seeding, reseed).</summary>
    public Guid? ActorId { get; set; }

    public required string EntityType { get; set; }
    public Guid EntityId { get; set; }
    public required string Action { get; set; }

    /// <summary>JSON object mapping each changed field to [old, new].</summary>
    public required string Changes { get; set; }
}
