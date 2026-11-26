using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ResourceBooking.Data;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;
using ResourceBooking.Tests.Infrastructure;

namespace ResourceBooking.Tests.Assistant;

/// <summary>
/// The eval cases in evals/cases.json against the real model (design: "Evals"). On demand only: run with
/// <c>--filter-trait "Category=Eval"</c> and OpenAI__ApiKey set; without a key each case is skipped. A model's
/// answers vary, so a case checks what was proposed, not the wording, and every conversation must be traced.
/// </summary>
[Trait("Category", "Eval")]
public class EvalTests(DbFixture fixture)
{
    private static readonly Lazy<EvalFile> File = new(() =>
        JsonSerializer.Deserialize<EvalFile>(System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "evals", "cases.json")), JsonSerializerOptions.Web)!);

    private static readonly Dictionary<string, Guid> Users = new() { ["Ana"] = Seed.AnaId, ["Ben"] = Seed.BenId, ["Sam"] = Seed.SamId };

    internal static EvalCase Load(string name) => File.Value.Cases.Single(c => c.Name == name);

    public static TheoryData<string> Cases => new(File.Value.Cases.Select(c => c.Name));

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Case(string name)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OpenAI__ApiKey")),
            "Set OpenAI__ApiKey to run the evals against the real model.");
        var evalCase = File.Value.Cases.Single(c => c.Name == name);
        await using var app = await ApiFactory.CreateAsync(fixture, realModel: true,
            settings: new Dictionary<string, string> { ["Assistant:MessagesPerMinute"] = "0" });
        await RunAsync(evalCase, app);
    }

    /// <summary>Plays a case's turns as its user and checks each turn's proposals and the trace.</summary>
    internal static async Task RunAsync(EvalCase evalCase, ApiFactory app)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = evalCase.Name;
        Dictionary<Guid, string> names;
        Dictionary<Guid, Guid> resourceOfBooking;
        await using (var db = app.Db())
        {
            names = await db.Resources.ToDictionaryAsync(r => r.Id, r => r.Name, ct);
            resourceOfBooking = await db.Bookings.ToDictionaryAsync(b => b.Id, b => b.ResourceId, ct);
        }
        var client = await app.SignedInAsync(Users[evalCase.User]);

        Guid? conversationId = null;
        foreach (var turn in evalCase.Turns)
        {
            var response = await client.PostAsJsonAsync("/api/assistant/messages", new AssistantRequest { ConversationId = conversationId, Text = turn.Text }, ct);
            response.EnsureSuccessStatusCode();
            var reply = (await response.Content.ReadFromJsonAsync<AssistantReply>(ct))!;
            conversationId = reply.ConversationId;
            TestContext.Current.SendDiagnosticMessage($"{name} / {turn.Text}: {reply.Text} [{string.Join("; ", reply.Proposals.Select(p => p.Summary))}]");

            Assert.NotEqual(ResourceBooking.Assistant.AssistantService.Unavailable, reply.Text);
            var unmatched = reply.Proposals.ToList();
            foreach (var expected in turn.Expect)
            {
                var match = unmatched.FirstOrDefault(p => Matches(expected, p, reply.Proposals, names, resourceOfBooking));
                Assert.True(match is not null, $"Turn \"{turn.Text}\": no proposal matches {JsonSerializer.Serialize(expected)}; got: {Describe(reply)}");
                unmatched.Remove(match);
            }
            Assert.True(unmatched.Count == 0, $"Turn \"{turn.Text}\": unexpected proposals: {Describe(reply)}");
        }

        await using var check = app.Db();
        var kinds = await check.AssistantSteps.Where(s => s.ConversationId == conversationId).Select(s => s.Kind).ToListAsync(ct);
        Assert.Equal(evalCase.Turns.Count, kinds.Count(k => k == StepKind.UserMessage));
        Assert.Equal(evalCase.Turns.Count, kinds.Count(k => k == StepKind.Reply));
        Assert.Contains(StepKind.ModelCall, kinds);
    }

    private static bool Matches(Expected e, ProposalDto p, IReadOnlyList<ProposalDto> turn, Dictionary<Guid, string> names, Dictionary<Guid, Guid> resourceOfBooking)
    {
        if (p.Kind != e.Kind)
        {
            return false;
        }
        var resourceId = p.Request?.ResourceId ?? (p.BookingId is { } b ? resourceOfBooking.GetValueOrDefault(b) : Guid.Empty);
        if (!e.Resource.Contains(names.GetValueOrDefault(resourceId, "?")))
        {
            return false;
        }
        if (p.Request is not { } r)
        {
            return e.StartFrom is null && e.Attendees is null;
        }
        var at = r.DeliveryAt ?? r.Start;
        if (e.StartFrom is { } from && (at is null || at < Local(from) || at > Local(e.StartTo ?? from)))
        {
            return false;
        }
        if (e.Attendees is { } people && r.Attendees != people)
        {
            return false;
        }
        // "same": delivered to the room proposed in the same turn.
        return e.DeliveryRoom switch
        {
            null => true,
            "same" => turn.Any(o => o != p && o.Request?.ResourceId == r.DeliveryRoomId && r.DeliveryRoomId is not null),
            var room => names.GetValueOrDefault(r.DeliveryRoomId ?? Guid.Empty) == room,
        };
    }

    private static DateTime Local(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture);

    private static string Describe(AssistantReply reply) =>
        reply.Proposals.Count == 0 ? $"none (\"{reply.Text}\")" : string.Join("; ", reply.Proposals.Select(p => $"{p.Kind} {p.Summary}"));

    internal sealed record EvalFile(List<EvalCase> Cases);

    internal sealed record EvalCase(string Name, string User, List<Turn> Turns);

    internal sealed record Turn(string Text, List<Expected> Expect);

    internal sealed record Expected(ProposalKind Kind, List<string> Resource, string? StartFrom, string? StartTo, int? Attendees, string? DeliveryRoom);
}
