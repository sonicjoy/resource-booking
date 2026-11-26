using ResourceBooking.Data;
using ResourceBooking.Tests.Infrastructure;
using static ResourceBooking.Tests.Infrastructure.StubChatClient;

namespace ResourceBooking.Tests.Assistant;

/// <summary>
/// The eval runner, checked on the stub so it runs in CI: a scripted right answer to a case passes and a wrong
/// one fails. Without this an eval that can never fail would look the same as one that passes.
/// </summary>
public class EvalRunnerTests(DbFixture fixture) : IAsyncLifetime
{
    private const string Tomorrow = "2026-11-26";
    private ApiFactory _app = null!;

    public async ValueTask InitializeAsync() => _app = await ApiFactory.CreateAsync(fixture);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static ToolCall Room(Guid room, int people, string start = "09:00", string end = "11:00") =>
        Call("propose_booking", new { resourceId = room, start = $"{Tomorrow}T{start}", end = $"{Tomorrow}T{end}", attendees = people });

    private static ToolCall Lunch(Guid room, int portions) =>
        Call("propose_booking", new { resourceId = Seed.MainKitchenId, deliveryAt = $"{Tomorrow}T12:30", deliveryRoomId = room, attendees = portions });

    [Fact]
    public async Task A_right_answer_passes()
    {
        _app.Model.On("a room for 8", "Thames and lunch.", Room(Seed.ThamesId, 8), Lunch(Seed.ThamesId, 8));
        _app.Model.On("make it 10", "Severn and lunch.", Room(Seed.SevernId, 10), Lunch(Seed.SevernId, 10));

        await EvalTests.RunAsync(EvalTests.Load("room and lunch, then more people"), _app);
    }

    public static TheoryData<string> WrongAnswers => new() { "lunch to another room", "too few people", "afternoon", "an extra proposal", "nothing" };

    [Theory]
    [MemberData(nameof(WrongAnswers))]
    public async Task A_wrong_answer_fails(string wrong)
    {
        ToolCall[] first = wrong switch
        {
            "lunch to another room" => [Room(Seed.ThamesId, 8), Lunch(Seed.SevernId, 8)],
            "too few people" => [Room(Seed.ThamesId, 6), Lunch(Seed.ThamesId, 8)],
            "afternoon" => [Room(Seed.ThamesId, 8, start: "14:00", end: "15:00"), Lunch(Seed.ThamesId, 8)],
            "an extra proposal" => [Room(Seed.ThamesId, 8), Room(Seed.SevernId, 8), Lunch(Seed.ThamesId, 8)],
            _ => [],
        };
        _app.Model.On("a room for 8", "Here.", first);

        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => EvalTests.RunAsync(EvalTests.Load("room and lunch, then more people"), _app));
    }

    [Fact]
    public async Task A_refusal_case_passes_when_the_tools_refuse()
    {
        _app.Model.On("Thames for 20", "Booked.", Room(Seed.ThamesId, 20, start: "14:00", end: "15:00"));

        // Thames seats 8, so the tool refuses and nothing is proposed: the case passes on the app's own checks.
        await EvalTests.RunAsync(EvalTests.Load("a room too small is not proposed"), _app);
        Assert.Equal(0, await Proposals());
    }

    private async Task<int> Proposals()
    {
        await using var db = _app.Db();
        return db.Proposals.Count();
    }
}
