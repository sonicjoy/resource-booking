using Microsoft.EntityFrameworkCore;
using Npgsql;
using ResourceBooking.Data;
using ResourceBooking.Tests.Infrastructure;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(DbFixture))]

namespace ResourceBooking.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL container per test assembly. The schema is migrated once into a
/// template database; each test class clones its own database from that template.
/// </summary>
public sealed class DbFixture : IAsyncLifetime
{
    private const string TemplateName = "rb_template";
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly SemaphoreSlim _cloneLock = new(1, 1);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await ExecuteOnServerAsync($"CREATE DATABASE {TemplateName}");
        await using (var db = CreateDb(ConnectionString(TemplateName)))
        {
            await db.Database.MigrateAsync();
        }
        // PostgreSQL refuses to clone a template while any session is connected to it,
        // so drop the pooled connections the migration left open.
        NpgsqlConnection.ClearAllPools();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates a migrated database with no rows and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        // Two clones of one template at the same moment can trip the same "in use" check.
        await _cloneLock.WaitAsync();
        try
        {
            await ExecuteOnServerAsync($"CREATE DATABASE {name} TEMPLATE {TemplateName}");
        }
        finally
        {
            _cloneLock.Release();
        }
        return ConnectionString(name);
    }

    /// <summary>Creates a database with no schema at all, as on a first deploy.</summary>
    public async Task<string> CreateUnmigratedDatabaseAsync()
    {
        var name = "e_" + Guid.NewGuid().ToString("N");
        await ExecuteOnServerAsync($"CREATE DATABASE {name}");
        return ConnectionString(name);
    }

    public static AppDb CreateDb(string connectionString, AuditInterceptor? audit = null)
    {
        var options = AppDb.Configure(new DbContextOptionsBuilder<AppDb>(), connectionString);
        if (audit is not null)
        {
            options.AddInterceptors(audit);
        }
        return new AppDb((DbContextOptions<AppDb>)options.Options);
    }

    private string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString;

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
