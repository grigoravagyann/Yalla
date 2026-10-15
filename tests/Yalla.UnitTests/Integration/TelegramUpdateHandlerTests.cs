using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Telegram;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Telegram;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The bot's side of the conversation, driven with Telegram's own update shapes and a bot client
/// that records instead of sending.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class TelegramUpdateHandlerTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly long Ani = TelegramTestHost.NewTelegramUserId();

    private readonly long Stranger = TelegramTestHost.NewTelegramUserId();

    [SkippableFact]
    public async Task Start_with_a_live_nonce_binds_the_chat_and_asks_for_the_contact_in_the_challenge_language()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var (challengeId, nonce) = await SeedAsync(db, clock, "hy");
        var bot = new FakeTelegramBotClient();

        await Handler(db, clock, bot).HandleAsync(Parse(TelegramTestHost.StartUpdate(Ani, nonce)), CancellationToken.None);

        var stored = await Reload(fixture, clock, challengeId);

        Assert.Equal(Ani, stored.TelegramUserId);
        Assert.Equal(Ani, stored.ChatId);
        Assert.Equal(TelegramVerificationStatus.Pending, stored.Status);

        var (chatId, text, markup) = Assert.Single(bot.Sent);

        Assert.Equal(Ani, chatId);
        Assert.Equal(AuthMessages.TelegramAskForContact("hy"), text);

        // The reply keyboard with the one button that makes Telegram hand over the number.
        var keyboard = Assert.IsType<TelegramRequestContactKeyboard>(markup);
        Assert.Equal(AuthMessages.TelegramShareContactButton("hy"), keyboard.ButtonText);
    }

    [SkippableFact]
    public async Task A_contact_from_the_bound_user_verifies_the_challenge_with_the_number_normalised()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var (challengeId, nonce) = await SeedAsync(db, clock, "en");
        var bot = new FakeTelegramBotClient();
        var handler = Handler(db, clock, bot);

        await handler.HandleAsync(Parse(TelegramTestHost.StartUpdate(Ani, nonce)), CancellationToken.None);

        // Telegram sends the number without the plus more often than with it.
        await handler.HandleAsync(
            Parse(TelegramTestHost.ContactUpdate(Ani, Ani, "37411223344")), CancellationToken.None);

        var stored = await Reload(fixture, clock, challengeId);

        Assert.Equal(TelegramVerificationStatus.Verified, stored.Status);
        Assert.Equal("+37411223344", stored.PhoneE164);
        Assert.Equal(Now, stored.VerifiedAtUtc);

        // The keyboard is taken away with the confirmation, then a button reopens the app on this
        // very challenge so it resumes polling wherever the person came from.
        Assert.Equal(3, bot.Sent.Count);
        Assert.Equal(AuthMessages.TelegramVerified("en"), bot.Sent[1].Text);
        Assert.IsType<TelegramRemoveKeyboard>(bot.Sent[1].Markup);

        var open = Assert.IsType<TelegramInlineLinkKeyboard>(bot.Sent[2].Markup);
        Assert.Equal(AuthMessages.TelegramOpenApp("en"), bot.Sent[2].Text);
        Assert.Equal(AuthMessages.TelegramOpenAppButton("en"), open.ButtonText);
        Assert.Equal($"yalla://verify?tg={challengeId}", open.Url);
    }

    [SkippableFact]
    public async Task A_contact_card_for_somebody_else_is_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var (challengeId, nonce) = await SeedAsync(db, clock, "en");
        var bot = new FakeTelegramBotClient();
        var handler = Handler(db, clock, bot);

        await handler.HandleAsync(Parse(TelegramTestHost.StartUpdate(Ani, nonce)), CancellationToken.None);

        // Ani forwards a stranger's contact card, or shares a contact with no Telegram account at
        // all. Neither proves Ani holds that number.
        await handler.HandleAsync(
            Parse(TelegramTestHost.ContactUpdate(Ani, Stranger, "+37411223344")), CancellationToken.None);
        await handler.HandleAsync(
            Parse(TelegramTestHost.ContactUpdate(Ani, null, "+37411223344", updateId: 3)), CancellationToken.None);

        var stored = await Reload(fixture, clock, challengeId);

        Assert.Equal(TelegramVerificationStatus.Pending, stored.Status);
        Assert.Null(stored.PhoneE164);

        Assert.Equal(3, bot.Sent.Count);
        Assert.Equal(AuthMessages.TelegramContactMismatch("en"), bot.Sent[1].Text);
        Assert.Equal(AuthMessages.TelegramContactMismatch("en"), bot.Sent[2].Text);
    }

    [SkippableFact]
    public async Task An_unknown_nonce_is_told_the_link_expired_and_binds_nothing()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var (challengeId, _) = await SeedAsync(db, clock, "ru");
        var bot = new FakeTelegramBotClient();

        await Handler(db, clock, bot).HandleAsync(
            Parse(TelegramTestHost.StartUpdate(Ani, "not-a-nonce-anyone-issued")), CancellationToken.None);

        Assert.Null((await Reload(fixture, clock, challengeId)).TelegramUserId);

        // Said in the user's own language, since no challenge was found to take a locale from.
        var (chatId, text, markup) = Assert.Single(bot.Sent);
        Assert.Equal(Ani, chatId);
        Assert.Equal(AuthMessages.TelegramLinkExpired("en"), text);
        Assert.Null(markup);
    }

    [SkippableFact]
    public async Task An_expired_challenge_is_told_the_link_expired()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var (challengeId, nonce) = await SeedAsync(db, clock, "en");
        var bot = new FakeTelegramBotClient();

        clock.Advance(TimeSpan.FromMinutes(5));

        await Handler(db, clock, bot).HandleAsync(Parse(TelegramTestHost.StartUpdate(Ani, nonce)), CancellationToken.None);

        Assert.Null((await Reload(fixture, clock, challengeId)).TelegramUserId);
        Assert.Equal(AuthMessages.TelegramLinkExpired("en"), Assert.Single(bot.Sent).Text);
    }

    [SkippableFact]
    public async Task A_second_telegram_user_opening_a_bound_link_is_refused_and_cannot_verify_it()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var (challengeId, nonce) = await SeedAsync(db, clock, "en");
        var bot = new FakeTelegramBotClient();
        var handler = Handler(db, clock, bot);

        await handler.HandleAsync(Parse(TelegramTestHost.StartUpdate(Ani, nonce)), CancellationToken.None);
        await handler.HandleAsync(Parse(TelegramTestHost.StartUpdate(Stranger, nonce, updateId: 2)), CancellationToken.None);

        // The forwarded link does not work, and neither does the stranger's own contact.
        await handler.HandleAsync(
            Parse(TelegramTestHost.ContactUpdate(Stranger, Stranger, "+37499000000", updateId: 3)), CancellationToken.None);

        var stored = await Reload(fixture, clock, challengeId);

        Assert.Equal(Ani, stored.TelegramUserId);
        Assert.Equal(TelegramVerificationStatus.Pending, stored.Status);
        Assert.Null(stored.PhoneE164);

        Assert.Equal(Stranger, bot.Sent[1].ChatId);
        Assert.Equal(AuthMessages.TelegramLinkExpired("en"), bot.Sent[1].Text);
        Assert.Equal(AuthMessages.TelegramLinkExpired("en"), bot.Sent[2].Text);
    }

    [SkippableFact]
    public async Task Anything_else_gets_the_unknown_command_text()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var bot = new FakeTelegramBotClient();

        await Handler(db, clock, bot).HandleAsync(
            Parse("{\"update_id\":9,\"message\":{\"message_id\":9,\"from\":{\"id\":" + Ani + ",\"is_bot\":false,\"first_name\":\"Ani\",\"language_code\":\"ru\"},\"chat\":{\"id\":" + Ani + ",\"type\":\"private\"},\"date\":1,\"text\":\"hello?\"}}"),
            CancellationToken.None);

        Assert.Equal(AuthMessages.TelegramUnknownCommand("ru"), Assert.Single(bot.Sent).Text);

        // An update with no message at all - an edited message, a channel post - is ignored.
        Assert.Null(TelegramUpdate.TryParse("""{"update_id":10,"edited_message":{"message_id":1}}""")?.Message);
        Assert.Null(TelegramUpdate.TryParse("not json"));
    }

    private static TelegramUpdate Parse(string json) =>
        TelegramUpdate.TryParse(json) ?? throw new InvalidOperationException("The test update did not parse.");

    private static TelegramUpdateHandler Handler(YallaDbContext db, TestClock clock, FakeTelegramBotClient bot) =>
        new(db, clock, bot, NullLogger<TelegramUpdateHandler>.Instance);

    private static async Task<(Guid ChallengeId, string Nonce)> SeedAsync(YallaDbContext db, TestClock clock, string locale)
    {
        var nonce = Secrets.NewOpaqueToken();

        var challenge = new TelegramVerification(
            Secrets.Hash(nonce), locale, clock.UtcNow, TimeSpan.FromMinutes(5), "203.0.113.9", callerDinerUserId: null);

        db.TelegramVerifications.Add(challenge);
        await db.SaveChangesAsync();

        return (challenge.Id, nonce);
    }

    private static async Task<TelegramVerification> Reload(SqlServerFixture fixture, TestClock clock, Guid id)
    {
        await using var db = fixture.CreateContext(clock);

        return await db.TelegramVerifications.AsNoTracking().SingleAsync(v => v.Id == id);
    }
}
