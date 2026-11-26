using Microsoft.Extensions.AI;

namespace ResourceBooking.Assistant;

/// <summary>The model when no OpenAI key is configured: every call says the assistant isn't set up.</summary>
public sealed class UnavailableChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new AssistantUnavailableException();

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new AssistantUnavailableException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
