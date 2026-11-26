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
            DropQueuedEvents(context);
            AddEvents(context, entry => entry.GetDatabaseValues());
        }
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            DropQueuedEvents(context);
            // Load what the database holds for every edited entity (see AddEvents).
            // Keyed by entity: EF hands out a new EntityEntry wrapper on every enumeration.
            var stored = new Dictionary<object, PropertyValues?>(ReferenceEqualityComparer.Instance);
            foreach (var entry in Audited(context).Where(e => e.State == EntityState.Modified))
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
            // Old values come from the database, not EF's snapshot: an entity saved with Update(), or a stub
            // attached and then edited, has no real snapshot. A row already gone has nothing to compare.
            var stored = entry.State == EntityState.Modified ? databaseValues(entry) : null;
            if (entry.State == EntityState.Modified && stored is null)
            {
                continue;
            }
            var changes = new Dictionary<string, object?[]>();
            foreach (var property in entry.Properties.Where(p => p.Metadata.Name != "Id"))
            {
                if (entry.State == EntityState.Added)
                {
                    changes[property.Metadata.Name] = [null, property.CurrentValue];
                }
                else if (property.IsModified && !Equals(stored![property.Metadata], property.CurrentValue))
                {
                    changes[property.Metadata.Name] = [stored[property.Metadata], property.CurrentValue]; // [old, new], read by HistoryLabels
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

    /// <summary>
    /// A save that didn't commit (failed, cancelled, or a concurrency conflict) leaves its audit events queued.
    /// This interceptor is their only writer, so any queued at the start of a save are left over: drop them.
    /// </summary>
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
