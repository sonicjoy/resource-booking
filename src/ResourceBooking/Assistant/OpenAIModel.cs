using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace ResourceBooking.Assistant;

/// <summary>
/// The app's model: OpenAI through the Responses API when OpenAI:ApiKey is set, otherwise
/// <see cref="UnavailableChatClient"/>. The key is read here only and never reaches the browser.
/// </summary>
public static class OpenAIModel
{
    public const string DefaultModel = "gpt-6-luna";

    public static IChatClient Create(IConfiguration config)
    {
        var key = config["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(key))
        {
            return new UnavailableChatClient();
        }
        // The Responses API, not Chat Completions: reasoning models (gpt-6-luna, for one) refuse function tools on
        // Chat Completions, and every current model takes them here. The OpenAI SDK still marks it as evaluation-only
        // (OPENAI001); it is confined to this file so a breaking SDK change lands in one place.
#pragma warning disable OPENAI001
        var model = new ResponsesClient(key).AsIChatClient(config["OpenAI:Model"] ?? DefaultModel);
        // Nothing is stored at OpenAI: each call carries the whole conversation, rebuilt from the app's own trace.
        return new ChatClientBuilder(model)
            .ConfigureOptions(options => options.RawRepresentationFactory ??= _ => new CreateResponseOptions { StoredOutputEnabled = false })
            .Build();
#pragma warning restore OPENAI001
    }
}
