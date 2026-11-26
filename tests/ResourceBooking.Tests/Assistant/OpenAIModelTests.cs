using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ResourceBooking.Assistant;

namespace ResourceBooking.Tests.Assistant;

/// <summary>Which model the app builds from its settings. Nothing here calls OpenAI.</summary>
public class OpenAIModelTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void With_no_key_the_assistant_is_not_set_up(string? key)
    {
        Assert.IsType<UnavailableChatClient>(OpenAIModel.Create(Config(("OpenAI:ApiKey", key))));
    }

    /// <summary>Records each request and answers 500, so nothing leaves the machine.</summary>
    private sealed class Capture : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
        }
    }

    private static async Task<(string Path, string Body)> SendOnceAsync(ChatOptions options)
    {
        var capture = new Capture();
        var client = OpenAIModel.Create(Config(("OpenAI:ApiKey", "sk-test-not-a-real-key")),
            new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(capture)));
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync("hello", options, TestContext.Current.CancellationToken));
        return capture.Requests[0];
    }

    [Fact]
    public async Task Every_request_goes_to_the_Responses_API_with_storage_off()
    {
        var (path, body) = await SendOnceAsync(new ChatOptions { MaxOutputTokens = 4000 });

        Assert.EndsWith("/responses", path);
        using var json = System.Text.Json.JsonDocument.Parse(body);
        Assert.False(json.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("gpt-6-luna", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(4000, json.RootElement.GetProperty("max_output_tokens").GetInt32());
    }

    [Fact]
    public async Task Storage_stays_off_even_when_a_caller_asks_for_it()
    {
#pragma warning disable OPENAI001
        var options = new ChatOptions { RawRepresentationFactory = _ => new OpenAI.Responses.CreateResponseOptions { StoredOutputEnabled = true } };
#pragma warning restore OPENAI001

        var (_, body) = await SendOnceAsync(options);

        using var json = System.Text.Json.JsonDocument.Parse(body);
        Assert.False(json.RootElement.GetProperty("store").GetBoolean());
    }

    [Fact]
    public void With_no_model_configured_the_default_is_gpt_6_luna()
    {
        var client = OpenAIModel.Create(Config(("OpenAI:ApiKey", "sk-test-not-a-real-key")));

        Assert.Equal("gpt-6-luna", client.GetService<ChatClientMetadata>()?.DefaultModelId);
    }

    [Fact]
    public void With_a_key_the_model_is_OpenAI_with_the_configured_model()
    {
        var client = OpenAIModel.Create(Config(("OpenAI:ApiKey", "sk-test-not-a-real-key"), ("OpenAI:Model", "gpt-4.1-mini")));

        Assert.IsNotType<UnavailableChatClient>(client);
        var metadata = client.GetService<ChatClientMetadata>();
        Assert.Equal("gpt-4.1-mini", metadata?.DefaultModelId);
        Assert.Equal("openai", metadata?.ProviderName);
    }
}
