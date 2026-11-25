using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using ResourceBooking.Data;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Api;

public class StartupTests(DbFixture fixture)
{
    [Fact]
    public async Task Starting_the_app_on_an_empty_database_seeds_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.CreateDatabaseAsync();
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseSetting("ConnectionStrings:Db", connectionString));

        var response = await factory.CreateClient().GetAsync("/healthz", ct);

        response.EnsureSuccessStatusCode();
        await using var db = DbFixture.CreateDb(connectionString);
        Assert.Equal(3, await db.Users.CountAsync(ct));
        Assert.True(await db.Users.AnyAsync(u => u.Id == Seed.SamId, ct));
    }
}
