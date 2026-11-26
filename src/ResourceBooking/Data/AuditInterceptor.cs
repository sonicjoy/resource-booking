using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ResourceBooking.Data.Entities;

namespace ResourceBooking.Data;

/// <summary>
/// Adds an audit event for every added or changed user, resource and booking, inside the same
/// SaveChanges call and so the same transaction. A bulk update or raw SQL bypasses it, which is why
/// only migrations and reseed may write those tables that way.
/// </summary>
/// <param name="currentActor">The signed-in user's id, or null when no one is signed in.</param>
public sealed class AuditInterceptor(TimeProvider clock, Func<Guid?> currentActor) : SaveChangesInterceptor
{
    private static readonly AsyncLocal<bool> SystemWrite = new();

    /// <summary>Records writes inside the returned scope as the system (actor null), as seeding is.</summary>
    public static IDisposable AsSystem()
    {
        SystemWrite.Value = true;
        return new EndSystemWrite();
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context)
        {
            AddEvents(context, entry => entry.GetDatabaseValues());
        }
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            // Load what the database holds for entries whose old values EF doesn't know (see AddEvents).
            // Keyed by entity: EF hands out a new EntityEntry wrapper on every enumeration.
            var stored = new Dictionary<object, PropertyValues?>(ReferenceEqualityComparer.Instance);
            foreach (var entry in Audited(context).Where(OldValuesUnknown))
            {
                stored[entry.Entity] = await entry.GetDatabaseValuesAsync(cancellationToken);
            }
            AddEvents(context, entry => stored.GetValueOrDefault(entry.Entity));
        }
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => DropQueuedEvents(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        DropQueuedEvents(eventData.Context);
        return Task.CompletedTask;
    }

    private void AddEvents(DbContext context, Func<EntityEntry, PropertyValues?> databaseValues)
    {
        var now = clock.GetUtcNow();
        var actor = SystemWrite.Value ? null : currentActor();

        foreach (var entry in Audited(context).ToList())
        {
            // An entity saved with Update() has no snapshot: EF reports its old values as equal to the new
            // ones. Compare against what the database holds instead.
            var stored = OldValuesUnknown(entry) ? databaseValues(entry) : null;
            var changes = new Dictionary<string, object?[]>();
            foreach (var property in entry.Properties.Where(p => p.Metadata.Name != "Id"))
            {
                var before = stored is not null ? stored[property.Metadata] : property.OriginalValue;
                if (entry.State == EntityState.Added)
                {
                    changes[property.Metadata.Name] = [null, property.CurrentValue];
                }
                else if ((property.IsModified || stored is not null) && !Equals(before, property.CurrentValue))
                {
                    changes[property.Metadata.Name] = [before, property.CurrentValue]; // [old, new], read by HistoryLabels
                }
            }
            if (changes.Count == 0)
            {
                continue;
            }

            context.Add(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                At = now,
                ActorId = actor,
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = (Guid)entry.Property("Id").CurrentValue!,
                Action = entry.State.ToString(),
                Changes = JsonSerializer.Serialize(changes),
            });
        }
    }

    private static IEnumerable<EntityEntry> Audited(DbContext context)
    {
        context.ChangeTracker.DetectChanges();
        return context.ChangeTracker.Entries()
            .Where(e => e.Entity is User or Resource or Booking && e.State is EntityState.Added or EntityState.Modified);
    }

    /// <summary>True for an entity attached with Update(): every property is marked modified with an unchanged value.</summary>
    private static bool OldValuesUnknown(EntityEntry entry) =>
        entry.State == EntityState.Modified
        && entry.Properties.Where(p => !p.Metadata.IsPrimaryKey()).All(p => p.IsModified && Equals(p.OriginalValue, p.CurrentValue));

    /// <summary>A failed save leaves its audit events queued; drop them so a retry doesn't write events for changes that never happened.</summary>
    private static void DropQueuedEvents(DbContext? context)
    {
        foreach (var queued in context?.ChangeTracker.Entries<AuditEvent>().Where(e => e.State == EntityState.Added).ToList() ?? [])
        {
            queued.State = EntityState.Detached;
        }
    }

    private sealed class EndSystemWrite : IDisposable
    {
        public void Dispose() => SystemWrite.Value = false;
    }
}
