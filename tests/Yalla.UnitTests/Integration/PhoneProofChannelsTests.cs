using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Telegram as the only phone proof, decided by the user on 2026-10-20 until an SMS provider is
/// chosen.
/// </summary>
/// <remarks>
/// With no SMS sender configured outside Development, a code request used to answer 200 and send
/// nothing: a guest at a booking form waited for a message nobody would ever send. Now the API says
/// in advance which proofs are on, and a code request it cannot deliver is refused by name.
/// Neither needs a database - the refusal comes before a code is minted.
/// </remarks>
public sealed class PhoneProofChannelsTests
{
    [Fact]
    public async Task A_deployment_with_no_sms_provider_says_so_and_refuses_a_code_request_by_name()
    {
        await using var factory = new YallaApiFactory().WithEnvironment(Environments.Staging);
        using var client = factory.CreateClient();

        var channels = await client.GetFromJsonAsync<JsonElement>("/api/auth/diner/phone-proof");
        Assert.False(channels.GetProperty("sms").GetBoolean());
        Assert.False(channels.GetProperty("telegram").GetBoolean());

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = "+37491000001" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, requested.StatusCode);

        var problem = await requested.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sms-unavailable", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Development_offers_codes_because_they_go_to_the_log()
    {
        await using var factory = new YallaApiFactory();
        using var client = factory.CreateClient();

        var channels = await client.GetFromJsonAsync<JsonElement>("/api/auth/diner/phone-proof");
        Assert.True(channels.GetProperty("sms").GetBoolean());
    }

    [Fact]
    public async Task The_telegram_flag_follows_the_bot_switch()
    {
        await using var factory = new YallaApiFactory()
            .With("Telegram:Enabled", "true")
            .With("Telegram:BotToken", "0:test-only")
            .With("Telegram:BotUsername", "YallaTestBot")
            .With("Telegram:WebhookSecret", "test-only-webhook-secret")
            .With("Telegram:ApiBaseUrl", "http://127.0.0.1:9");
        using var client = factory.CreateClient();

        var channels = await client.GetFromJsonAsync<JsonElement>("/api/auth/diner/phone-proof");
        Assert.True(channels.GetProperty("telegram").GetBoolean());
    }
}
