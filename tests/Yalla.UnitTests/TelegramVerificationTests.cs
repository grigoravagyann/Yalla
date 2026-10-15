using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;

namespace Yalla.UnitTests;

/// <summary>
/// The lifecycle of one Telegram challenge: pending, bound to the first Telegram user who opens
/// the link, verified by that user's contact, consumed exactly once by the app.
/// </summary>
public class TelegramVerificationTests
{
    private static readonly DateTime Issued = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private static TelegramVerification New() =>
        new("a".PadLeft(64, 'a'), "en", Issued, Lifetime, "203.0.113.9", callerDinerUserId: null);

    [Fact]
    public void A_fresh_challenge_is_pending_until_its_lifetime_runs_out()
    {
        var challenge = New();

        Assert.Equal(TelegramVerificationStatus.Pending, challenge.Status);
        Assert.Equal(Issued.Add(Lifetime), challenge.ExpiresAtUtc);
        Assert.True(challenge.IsPendingAt(Issued.AddMinutes(4)));
        Assert.False(challenge.IsPendingAt(Issued.Add(Lifetime)));
    }

    [Fact]
    public void The_first_telegram_user_to_start_binds_the_challenge_and_a_second_is_refused()
    {
        var challenge = New();

        Assert.True(challenge.TryBind(telegramUserId: 1001, chatId: 1001, Issued.AddSeconds(10)));
        Assert.Equal(1001, challenge.TelegramUserId);
        Assert.Equal(1001, challenge.ChatId);

        // The same person tapping the link twice is allowed, and changes nothing.
        Assert.True(challenge.TryBind(telegramUserId: 1001, chatId: 1001, Issued.AddSeconds(20)));

        // Somebody else holding a forwarded link is not: the nonce is bound.
        Assert.False(challenge.TryBind(telegramUserId: 2002, chatId: 2002, Issued.AddSeconds(30)));
        Assert.Equal(1001, challenge.TelegramUserId);
    }

    [Fact]
    public void An_expired_challenge_cannot_be_bound()
    {
        var challenge = New();

        Assert.False(challenge.TryBind(1001, 1001, Issued.Add(Lifetime)));
        Assert.Null(challenge.TelegramUserId);
    }

    [Fact]
    public void Verifying_records_the_number_and_keeps_the_challenge_claimable_for_a_short_grace()
    {
        var challenge = New();
        challenge.TryBind(1001, 1001, Issued.AddSeconds(10));

        var verifiedAt = Issued.AddMinutes(4).AddSeconds(50);
        challenge.Verify("+37411223344", verifiedAt);

        Assert.Equal(TelegramVerificationStatus.Verified, challenge.Status);
        Assert.Equal("+37411223344", challenge.PhoneE164);
        Assert.Equal(verifiedAt, challenge.VerifiedAtUtc);

        // Verified ten seconds before the deadline: the app polling two seconds later must still
        // be able to claim it, so the deadline moves to the end of the claim grace.
        Assert.Equal(verifiedAt.Add(TelegramVerification.ClaimGrace), challenge.ExpiresAtUtc);
        Assert.True(challenge.IsClaimableAt(verifiedAt.AddMinutes(1)));
        Assert.False(challenge.IsClaimableAt(verifiedAt.Add(TelegramVerification.ClaimGrace)));
    }

    [Fact]
    public void Verifying_an_unbound_or_expired_or_already_verified_challenge_is_refused()
    {
        var unbound = New();
        Assert.Throws<DomainStateException>(() => unbound.Verify("+37411223344", Issued.AddSeconds(5)));

        var expired = New();
        expired.TryBind(1001, 1001, Issued.AddSeconds(5));
        Assert.Throws<DomainStateException>(() => expired.Verify("+37411223344", Issued.Add(Lifetime)));

        var twice = New();
        twice.TryBind(1001, 1001, Issued.AddSeconds(5));
        twice.Verify("+37411223344", Issued.AddSeconds(30));
        Assert.Throws<DomainStateException>(() => twice.Verify("+37411223344", Issued.AddSeconds(40)));
    }

    [Fact]
    public void A_verified_challenge_is_consumed_exactly_once()
    {
        var challenge = New();
        challenge.TryBind(1001, 1001, Issued.AddSeconds(5));
        challenge.Verify("+37411223344", Issued.AddSeconds(30));

        challenge.Consume(Issued.AddSeconds(32));

        Assert.Equal(TelegramVerificationStatus.Consumed, challenge.Status);
        Assert.Equal(Issued.AddSeconds(32), challenge.ConsumedAtUtc);
        Assert.False(challenge.IsClaimableAt(Issued.AddSeconds(33)));

        Assert.Throws<DomainStateException>(() => challenge.Consume(Issued.AddSeconds(34)));
    }

    [Fact]
    public void A_pending_challenge_cannot_be_consumed()
    {
        var challenge = New();
        challenge.TryBind(1001, 1001, Issued.AddSeconds(5));

        Assert.Throws<DomainStateException>(() => challenge.Consume(Issued.AddSeconds(6)));
        Assert.Equal(TelegramVerificationStatus.Pending, challenge.Status);
    }

    [Fact]
    public void Marking_expired_retires_a_pending_or_verified_challenge_and_nothing_else()
    {
        var pending = New();
        pending.MarkExpired(Issued.Add(Lifetime));
        Assert.Equal(TelegramVerificationStatus.Expired, pending.Status);

        var consumed = New();
        consumed.TryBind(1001, 1001, Issued.AddSeconds(5));
        consumed.Verify("+37411223344", Issued.AddSeconds(30));
        consumed.Consume(Issued.AddSeconds(31));
        consumed.MarkExpired(Issued.AddHours(1));
        Assert.Equal(TelegramVerificationStatus.Consumed, consumed.Status);
    }

    [Fact]
    public void The_nonce_is_stored_as_a_hash_and_a_blank_one_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            new TelegramVerification(" ", "en", Issued, Lifetime, null, null));

        // Only the hash is ever on the row; the plaintext nonce lives in the deep link alone.
        Assert.Equal("a".PadLeft(64, 'a'), New().NonceHash);
    }
}
