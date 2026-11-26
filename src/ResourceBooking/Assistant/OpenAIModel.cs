using System.ClientModel;
using System.ClientModel.Primitives;
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

    /// <param name="transport">Replaces the HTTP transport; for tests that inspect the requests sent.</param>
    public static IChatClient Create(IConfiguration config, PipelineTransport? transport = null)
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
        var options = new ResponsesClientOptions();
        if (transport is not null)
        {
            options.Transport = transport;
        }
        var model = new ResponsesClient(new ApiKeyCredential(key), options).AsIChatClient(config["OpenAI:Model"] ?? DefaultModel);
        // store:false on every call, whatever other request options a caller sets: responses aren't kept at OpenAI
        // for later use (its abuse-monitoring retention still applies). Each call carries the whole conversation,
        // rebuilt from the app's own trace, so nothing relies on server-side state.
        return new ChatClientBuilder(model)
            .ConfigureOptions(chat =>
            {
                var earlier = chat.RawRepresentationFactory;
                chat.RawRepresentationFactory = client =>
                {
                    var raw = earlier?.Invoke(client) as CreateResponseOptions ?? new CreateResponseOptions();
                    raw.StoredOutputEnabled = false;
                    return raw;
                };
            })
            .Build();
#pragma warning restore OPENAI001
    }
}
