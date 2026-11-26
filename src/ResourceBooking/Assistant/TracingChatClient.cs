using System.Diagnostics;
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
        var reserved = Estimate(list) + maxOutputTokens;
        var day = await budget.ReserveAsync(reserved, CancellationToken.None);
        var watch = Stopwatch.StartNew();
        // A call that throws (timeout, abandoned request, model error) keeps its whole reservation.
        var response = await base.GetResponseAsync(list, options, cancellationToken);
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

    /// <summary>A generous guess at a call's input tokens: about one token per three characters sent, tool schemas included.</summary>
    private static long Estimate(List<ChatMessage> messages) =>
        700 + messages.Sum(m => (long)(m.Text?.Length ?? 0)
            + m.Contents.OfType<FunctionResultContent>().Sum(r => (long)(r.Result?.ToString()?.Length ?? 0))) / 3;

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
