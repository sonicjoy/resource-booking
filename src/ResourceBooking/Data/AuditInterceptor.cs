using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ResourceBooking.Data.Entities;

namespace ResourceBooking.Data;

/// <summary>
/// Adds an audit event for every added or changed user, resource and booking, inside the same
/// SaveChanges call and so the same transaction. A bulk update or raw SQL bypasses it, which is why
/// only migrations and reseed may write those tables that way.
/// </summary>
/// <param name="currentActor">The signed-in user's id, or null for the system (start-up seeding).</param>
public sealed class AuditInterceptor(TimeProvider clock, Func<Guid?> currentActor) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        AddEvents(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        AddEvents(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void AddEvents(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var now = clock.GetUtcNow();
        var actor = currentActor();
        var audited = context.ChangeTracker.Entries()
            .Where(e => e.Entity is User or Resource or Booking && e.State is EntityState.Added or EntityState.Modified)
            .ToList();

        foreach (var entry in audited)
        {
            var changes = new Dictionary<string, object?[]>();
            foreach (var property in entry.Properties.Where(p => p.Metadata.Name != "Id"))
            {
                if (entry.State == EntityState.Added)
                {
                    changes[property.Metadata.Name] = [null, property.CurrentValue];
                }
                else if (property.IsModified && !Equals(property.OriginalValue, property.CurrentValue))
                {
                    changes[property.Metadata.Name] = [property.OriginalValue, property.CurrentValue];
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
}
