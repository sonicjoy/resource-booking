using System.Net;
using System.Net.Http.Json;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Api;

/// <summary>
/// Level 4: random sequences of user-admin operations against the real API, compared after every step with a
/// model of who exists, their role and whether they are active (design: API table, "Cascades", LAST_ADMIN).
/// Each sequence starts from the seed and is run by whichever admin is still active.
/// </summary>
public class UserAdminModelTests(DbFixture fixture) : IAsyncLifetime
{
    private ApiFactory _app = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _app = await ApiFactory.CreateAsync(fixture);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    /// <summary>One operation: add a user with a role, change someone's role (null: leave it), or deactivate someone.</summary>
    public sealed record Op(string Kind, int Target, Role? Role)
    {
        public override string ToString() => $"{Kind}({Target}, {Role})";
    }

    private sealed record Person(Guid Id, string Email, Role Role, bool Active);

    private static readonly Gen<Op> AnyOp = Gen.OneOf(
        Gen.Enum<Role>().Select(r => new Op("add", 0, r)),
        Gen.Select(Gen.Int[0, 5], Gen.Int[0, 2].Select(i => i == 0 ? (Role?)null : i == 1 ? Role.Employee : Role.Admin)).Select((t, r) => new Op("edit", t, r)),
        Gen.Int[0, 5].Select(t => new Op("deactivate", t, null)));

    [Fact]
    public async Task Every_sequence_matches_the_model_and_keeps_an_active_admin()
    {
        await AnyOp.List[1, 7].SampleAsync(async ops =>
        {
            await using (var scope = _app.Services.CreateAsyncScope())
            {
                await Seed.ReseedAsync(scope.ServiceProvider.GetRequiredService<AppDb>(), _app.Clock,
                    scope.ServiceProvider.GetRequiredService<TimeZoneInfo>(), Ct);
            }
            var people = new List<Person>
            {
                new(Seed.AnaId, "ana@sonicjoy-solutions.com", Role.Employee, true),
                new(Seed.BenId, "ben@sonicjoy-solutions.com", Role.Employee, true),
                new(Seed.SamId, "sam@sonicjoy-solutions.com", Role.Admin, true),
            };
            var added = 0;

            foreach (var op in ops)
            {
                var actor = people.First(p => p is { Role: Role.Admin, Active: true });
                using var admin = await _app.SignedInAsync(actor.Id);
                var target = people[op.Target % people.Count];
                var activeAdmins = people.Count(p => p is { Role: Role.Admin, Active: true });
                var removesLastAdmin = target is { Role: Role.Admin, Active: true } && activeAdmins == 1;

                switch (op.Kind)
                {
                    case "add":
                        var email = $"new{++added}@sonicjoy-solutions.com";
                        var created = await admin.PostAsJsonAsync("/api/users", new { email, displayName = $"New {added}", role = op.Role }, Ct);
                        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                        people.Add(new Person((await created.Content.ReadFromJsonAsync<UserDto>(Ct))!.Id, email, op.Role!.Value, true));
                        break;
                    case "edit":
                        var role = op.Role ?? target.Role;
                        var edited = await admin.PutAsJsonAsync($"/api/users/{target.Id}",
                            new { email = target.Email, displayName = "Renamed", role = op.Role }, Ct);
                        var refused = removesLastAdmin && role != Role.Admin;
                        Assert.Equal(refused ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, edited.StatusCode);
                        if (!refused)
                        {
                            people[people.IndexOf(target)] = target with { Role = role };
                        }
                        break;
                    default:
                        var deactivated = await admin.PostAsync($"/api/users/{target.Id}/deactivation", null, Ct);
                        Assert.Equal(removesLastAdmin ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, deactivated.StatusCode);
                        if (!removesLastAdmin)
                        {
                            people[people.IndexOf(target)] = target with { Active = false };
                        }
                        break;
                }

                // The actor may have just demoted themselves, which ends their session; check as an admin who remains.
                using var checker = await _app.SignedInAsync(people.First(p => p is { Role: Role.Admin, Active: true }).Id);
                var stored = (await checker.GetFromJsonAsync<List<UserDto>>("/api/users", Ct))!;
                Assert.Equal(people.Select(p => (p.Id, p.Role, p.Active)).OrderBy(p => p.Id),
                    stored.Select(u => (u.Id, u.Role, u.IsActive)).OrderBy(p => p.Id));
                Assert.Contains(stored, u => u is { Role: Role.Admin, IsActive: true });
            }
        }, iter: 60, threads: 1);
    }
}
