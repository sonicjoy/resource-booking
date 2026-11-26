namespace ResourceBooking.Assistant;

/// <summary>The assistant's limits, from the "Assistant" configuration section.</summary>
public sealed class AssistantOptions
{
    /// <summary>Tokens all users together may spend per London day; 429 BUDGET_EXHAUSTED past it.</summary>
    public long DailyTokenBudget { get; set; } = 200_000;

    /// <summary>Messages one user may send per minute; 0 turns the limit off.</summary>
    public int MessagesPerMinute { get; set; } = 10;

    /// <summary>How long one message may take before the assistant gives up on the model.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    public int MaxTurns { get; set; } = 20;

    /// <summary>Model calls per message, counting each round of tool calls.</summary>
    public int MaxIterations { get; set; } = 5;
}

/// <summary>The day's token budget is spent; thrown before a model call that would go over it.</summary>
public sealed class BudgetExhaustedException() : Exception("The assistant's daily budget is spent");

/// <summary>No model is configured (no OpenAI key), so the assistant can't answer.</summary>
public sealed class AssistantUnavailableException() : Exception("No model is configured");
