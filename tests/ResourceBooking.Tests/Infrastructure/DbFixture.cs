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
        NpgsqlConnection.ClearAllPools();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates an empty, migrated database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
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

    public static AppDb CreateDb(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDb>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

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
