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
