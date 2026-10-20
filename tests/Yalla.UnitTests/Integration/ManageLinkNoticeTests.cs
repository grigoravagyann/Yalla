using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Notifications;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// What the account holder is sent when somebody holding the booking's manage link changes or cancels
/// it: the words in each language, and the push the outbox carries reaching the right person.
/// </summary>
/// <remarks>
/// That the notice is written at all - in the same save, for the link only, once per change - is
/// <see cref="ManageLinkRescheduleTests"/>' subject. These are about delivery.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ManageLinkNoticeTests(SqlServerFixture fixture)
{
    /// <summary>12:00 on Thursday 10 September 2026 in Yerevan.</summary>
    private static readonly DateTime Now = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly BookingDate = new(2026, 9, 13);

    // ---------------------------------------------------------------- the words

    [Theory]
    [InlineData("hy", "Ձեր ամրագրումը փոխվել է", "հղումով")]
    [InlineData("ru", "Ваша бронь изменена", "по ссылке")]
    [InlineData("en", "Your booking was changed", "booking link")]
    [InlineData("ru-RU", "Ваша бронь изменена", "по ссылке")]
    [InlineData("de", "Ձեր ամրագրումը փոխվել է", "հղումով")]
    public void A_change_from_the_link_says_where_the_booking_stands_and_how_it_was_changed(
        string locale, string title, string how)
    {
        var (confirmedTitle, confirmed) = NotificationText.ReservationChangedByLink(
            locale, "Kavkaz", new DateOnly(2026, 10, 24), new TimeOnly(20, 0), 3, waiting: false);
        var (_, waiting) = NotificationText.ReservationChangedByLink(
            locale, "Kavkaz", new DateOnly(2026, 10, 24), new TimeOnly(20, 0), 3, waiting: true);

        Assert.Equal(title, confirmedTitle);

        foreach (var part in new[] { "Kavkaz", "24.10", "20:00", "3", how })
        {
            Assert.Contains(part, confirmed, StringComparison.Ordinal);
        }

        // Waiting for the venue says so; confirmed does not.
        Assert.NotEqual(confirmed, waiting);
        Assert.StartsWith(confirmed[..confirmed.IndexOf("3", StringComparison.Ordinal)], waiting, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hy", "Ձեր ամրագրումը չեղարկվել է", "հղումով")]
    [InlineData("ru", "Ваша бронь отменена", "по ссылке")]
    [InlineData("en", "Your booking was cancelled", "booking link")]
    public void A_cancel_from_the_link_says_which_booking_and_how(string locale, string title, string how)
    {
        var (actualTitle, body) = NotificationText.ReservationCancelledByLink(
            locale, "Kavkaz", new DateOnly(2026, 10, 24), new TimeOnly(20, 0));

        Assert.Equal(title, actualTitle);

        foreach (var part in new[] { "Kavkaz", "24.10", "20:00", how })
        {
            Assert.Contains(part, body, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- the push

    [SkippableFact]
    public async Task The_push_the_outbox_carries_reaches_the_account_holder_in_their_language()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        // A diner whose phone is set to Russian.
        var diner = new DinerUser($"+3749{Random.Shared.Next(1_000_000, 9_999_999)}", "hy");
        diner.ProveNumber(clock.UtcNow);
        db.DinerUsers.Add(diner);
        db.DinerDevices.Add(new DinerDevice(diner.Id, $"ExponentPushToken[{Guid.NewGuid():N}]", DevicePlatform.Ios, "ru", clock.UtcNow));
        await db.SaveChangesAsync();

        var booked = await fixture.CreateReservationService(db, clock, TestActor.Diner(diner.Id)).CreateAsync(
            new CreateReservationCommand(
                branch.BranchId, branch.FirstTableId, BookingDate, new TimeOnly(18, 0), 2,
                GuestName: "Ani Link", GuestPhone: "+37411223366", ClientCommandId: Guid.CreateVersion7()));

        var link = fixture.CreateReservationService(db, clock, new TestActor(ActorType.System, null, null));
        await link.RescheduleByManageTokenAsync(
            booked.ManageToken!,
            new RescheduleByManageTokenCommand(branch.TableIds[1], BookingDate, new TimeOnly(20, 0), 3, Guid.CreateVersion7()));

        var channel = new RecordingChannel();
        await DeliverAsync(new ReservationChangedByLinkHandler(db, channel), OutboxMessageTypes.ReservationChangedByLink, booked.Id, clock);

        var changed = Assert.Single(channel.OfKind("reservation-changed-by-link"));
        Assert.Equal(diner.Id, changed.DinerUserId);
        Assert.Equal("Ваша бронь изменена", changed.Title);
        Assert.Contains("13.09", changed.Body, StringComparison.Ordinal);
        Assert.Contains("20:00", changed.Body, StringComparison.Ordinal);
        Assert.Equal(booked.Id.ToString(), changed.Data["reservationId"]);

        // Then cancelled from the link: the change's push, if still unsent, gives way to this one.
        await link.CancelByManageTokenAsync(booked.ManageToken!);
        await DeliverAsync(new ReservationCancelledByLinkHandler(db, channel), OutboxMessageTypes.ReservationCancelledByLink, booked.Id, clock);

        var cancelled = Assert.Single(channel.OfKind("reservation-cancelled-by-link"));
        Assert.Equal(diner.Id, cancelled.DinerUserId);
        Assert.Equal("Ваша бронь отменена", cancelled.Title);
        Assert.Contains("20:00", cancelled.Body, StringComparison.Ordinal);
    }

    /// <summary>Every message type the API writes has a handler registered for it, these two included.</summary>
    [SkippableFact]
    public void Every_outbox_message_type_has_a_handler_in_the_container()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        using var scope = factory.Services.CreateScope();

        var handled = scope.ServiceProvider.GetServices<Yalla.Application.Messaging.IOutboxHandler>()
            .Select(h => h.MessageType)
            .ToHashSet(StringComparer.Ordinal);

        var types = typeof(OutboxMessageTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Contains(OutboxMessageTypes.ReservationChangedByLink, types);
        Assert.Contains(OutboxMessageTypes.ReservationCancelledByLink, types);

        foreach (var type in types)
        {
            Assert.True(handled.Contains(type), $"No outbox handler is registered for {type}.");
        }
    }

    /// <summary>Hands the booking's unsent message of one type to its handler, as the dispatcher would.</summary>
    private async Task DeliverAsync(
        Yalla.Application.Messaging.IOutboxHandler handler, string type, Guid reservationId, TestClock clock)
    {
        await using var db = fixture.CreateContext(clock);
        var prefix = OutboxMessageTypes.PrefixFor("reservation", reservationId);

        var payload = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.IdempotencyKey.StartsWith(prefix) && m.Type == type && m.SentAtUtc == null)
            .Select(m => m.PayloadJson)
            .SingleAsync();

        await handler.HandleAsync(payload, CancellationToken.None);
    }
}
