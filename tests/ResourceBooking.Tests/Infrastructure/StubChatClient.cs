using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ResourceBooking.Tests.Infrastructure;

/// <summary>
/// A scripted model. For each user message it plays back the tool calls scripted for it, then, once their
/// results come back, a final reply. It reports a fixed token count per call so budgets can be tested.
/// </summary>
public sealed class StubChatClient : IChatClient
{
    private readonly List<(Func<string, bool> Matches, Script Script)> _scripts = [];

    public int TokensPerCall { get; set; } = 100;

    /// <summary>Thrown from every call instead of answering, to test failures.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Waits before answering, to test timeouts.</summary>
    public TimeSpan Delay { get; set; }

    public enum UsageReport { Full, NoTotal, None }

    /// <summary>How each response reports its token usage: in full, without a total, or not at all.</summary>
    public UsageReport Usage { get; set; }

    /// <summary>Asks for the scripted tool calls on every call, never replying, to test the loop cap.</summary>
    public bool NeverReply { get; set; }

    /// <summary>Every message list the model was sent, in order.</summary>
    public List<List<ChatMessage>> Calls { get; } = [];

    /// <summary>The options each call was sent with, in order.</summary>
    public List<ChatOptions?> Options { get; } = [];

    public sealed record ToolCall(string Name, object Arguments);

    public sealed record Script(IReadOnlyList<ToolCall> Calls, string Reply);

    /// <summary>When the latest user message contains <paramref name="text"/>, call these tools, then reply.</summary>
    public StubChatClient On(string text, string reply, params ToolCall[] calls)
    {
        _scripts.Add((m => m.Contains(text, StringComparison.OrdinalIgnoreCase), new Script(calls, reply)));
        return this;
    }

    public static ToolCall Call(string name, object arguments) => new(name, arguments);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Calls.Add(list);
        Options.Add(options);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }
        if (Failure is { } failure)
        {
            throw failure;
        }

        var usage = Usage switch
        {
            UsageReport.None => null,
            UsageReport.NoTotal => new UsageDetails { InputTokenCount = TokensPerCall / 2, OutputTokenCount = TokensPerCall - TokensPerCall / 2 },
            _ => new UsageDetails { InputTokenCount = TokensPerCall / 2, OutputTokenCount = TokensPerCall - TokensPerCall / 2, TotalTokenCount = TokensPerCall },
        };
        var lastUser = list.Last(m => m.Role == ChatRole.User).Text;
        var script = _scripts.LastOrDefault(s => s.Matches(lastUser)).Script ?? new Script([], "I can help with rooms, cars and catering.");

        // Tool results after the latest user message mean the calls were made: answer with the reply.
        var sinceUser = list.SkipWhile(m => m != list.Last(x => x.Role == ChatRole.User)).Skip(1);
        if (script.Calls.Count == 0 || (!NeverReply && sinceUser.Any(m => m.Contents.OfType<FunctionResultContent>().Any())))
        {
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, script.Reply)) { Usage = usage };
        }

        var contents = script.Calls.Select((c, i) => (AIContent)new FunctionCallContent($"call{Calls.Count}_{i}", c.Name,
            JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(c.Arguments, JsonSerializerOptions.Web)))).ToList();
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)) { Usage = usage };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The assistant doesn't stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
