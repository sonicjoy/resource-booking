using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;

namespace ResourceBooking.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Notice> Notices => Set<Notice>();
    public DbSet<AssistantStep> AssistantSteps => Set<AssistantStep>();
    public DbSet<Proposal> Proposals => Set<Proposal>();
    public DbSet<TokenUsage> TokenUsage => Set<TokenUsage>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<ResourceType>().HaveConversion<string>();
        configurationBuilder.Properties<Role>().HaveConversion<string>();
        configurationBuilder.Properties<BookingStatus>().HaveConversion<string>();
        configurationBuilder.Properties<StepKind>().HaveConversion<string>();
        configurationBuilder.Properties<ProposalKind>().HaveConversion<string>();
        configurationBuilder.Properties<ProposalStatus>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("btree_gist");

        model.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
        });

        model.Entity<Resource>(e =>
        {
            e.HasIndex(r => r.Name).IsUnique();
        });

        model.Entity<Booking>(e =>
        {
            e.HasOne(b => b.Resource).WithMany().HasForeignKey(b => b.ResourceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(b => b.User).WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Resource>().WithMany().HasForeignKey(b => b.DeliveryRoomId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(b => b.UserId);
        });

        model.Entity<AuditEvent>(e =>
        {
            e.Property(a => a.Changes).HasColumnType("jsonb");
            e.HasIndex(a => new { a.EntityType, a.EntityId });
            e.HasIndex(a => a.At);
        });

        model.Entity<Notice>(e =>
        {
            e.HasOne<User>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Booking>().WithMany().HasForeignKey(n => n.BookingId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<AssistantStep>(e =>
        {
            e.Property(s => s.Payload).HasColumnType("jsonb");
            e.HasIndex(s => new { s.ConversationId, s.Seq }).IsUnique();
        });

        model.Entity<Proposal>(e =>
        {
            e.Property(p => p.Payload).HasColumnType("jsonb");
            e.HasIndex(p => p.ConversationId);
        });

        model.Entity<TokenUsage>(e => e.HasKey(t => t.Day));
    }
}
