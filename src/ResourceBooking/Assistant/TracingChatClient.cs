using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ResourceBooking.Shared;

namespace ResourceBooking.Assistant;

/// <summary>
/// Sits between function invocation and the model: checks the token budget before each call and reserves an
/// upper bound for it, settles that to the real usage afterwards (keeping it if the call fails), and records the
/// call as a ModelCall step (the messages new since the last call, the tool calls asked for, tokens and duration).
/// <paramref name="seen"/> is how many messages earlier turns already traced, so a turn records only its own.
/// </summary>
public sealed class TracingChatClient(IChatClient inner, Trace trace, TokenBudget budget, int maxOutputTokens, int seen)
    : DelegatingChatClient(inner)
{
    private int _seen = seen;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!await budget.HasRoomAsync(cancellationToken))
        {
            throw new BudgetExhaustedException();
        }
        var list = messages.ToList();
        var sent = list.Skip(_seen).Select(Describe).ToList();
        options = options?.Clone() ?? new ChatOptions();
        options.MaxOutputTokens = maxOutputTokens;
        var reserved = InputBound(list, options) + maxOutputTokens;
        var day = await budget.ReserveAsync(reserved, CancellationToken.None);
        var watch = Stopwatch.StartNew();
        ChatResponse response;
        try
        {
            // A call that throws (timeout, abandoned request, model error) keeps its whole reservation.
            response = await base.GetResponseAsync(list, options, cancellationToken);
        }
        catch (AssistantUnavailableException)
        {
            // No model is configured, so nothing was sent and nothing is spent.
            await budget.SettleAsync(day, reserved, 0, CancellationToken.None);
            throw;
        }
        watch.Stop();
        _seen = list.Count + response.Messages.Count;

        var input = (int?)response.Usage?.InputTokenCount;
        var output = (int?)response.Usage?.OutputTokenCount;
        long? used = response.Usage?.TotalTokenCount ?? (input is null && output is null ? null : (input ?? 0) + (output ?? 0));
        await budget.SettleAsync(day, reserved, used ?? reserved, CancellationToken.None);
        var toolCalls = response.Messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => new { c.Name, c.Arguments }).ToList();
        await trace.AddAsync(StepKind.ModelCall, new { sent, toolCalls, text = response.Text }, input, output, (int)watch.ElapsedMilliseconds);
        return response;
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The assistant doesn't stream.");

    /// <summary>
    /// An upper bound on a call's input tokens for a byte-level tokenizer, where every token is at least one byte:
    /// the UTF-8 bytes of every text, tool call, tool result and tool schema sent, plus a little per message.
    /// </summary>
    private static long InputBound(List<ChatMessage> messages, ChatOptions options)
    {
        static long Bytes(string? s) => s is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(s);
        var sent = messages.Sum(m => 8 + Bytes(m.Text)
            + m.Contents.OfType<FunctionCallContent>().Sum(c => Bytes(c.Name) + Bytes(JsonSerializer.Serialize(c.Arguments)))
            + m.Contents.OfType<FunctionResultContent>().Sum(r => Bytes(r.Result is string text ? text : JsonSerializer.Serialize(r.Result))));
        var tools = options.Tools?.OfType<AIFunction>().Sum(t => Bytes(t.Name) + Bytes(t.Description) + Bytes(t.JsonSchema.GetRawText())) ?? 0;
        return 50 + sent + tools;
    }

    private static object Describe(ChatMessage m) => new
    {
        role = m.Role.Value,
        text = m.Text,
        results = m.Contents.OfType<FunctionResultContent>().Select(r => new { r.CallId, r.Result }).ToList(),
    };
}

/// <summary>Records each tool call as a ToolCall step: its arguments, result or error, and duration.</summary>
public sealed class TracedFunction(AIFunction inner, Trace trace) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            await trace.AddAsync(StepKind.ToolCall, new { name = Name, arguments, result }, durationMs: (int)watch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await trace.AddAsync(StepKind.ToolCall, new { name = Name, arguments, error = e.Message }, durationMs: (int)watch.ElapsedMilliseconds);
            throw;
        }
    }
}
