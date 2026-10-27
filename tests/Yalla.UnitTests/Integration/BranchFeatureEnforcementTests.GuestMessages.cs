using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Messaging;
using Yalla.Domain.Enums;
using static Yalla.UnitTests.Integration.BookingPerNumberTests;

namespace Yalla.UnitTests.Integration;

public sealed partial class BranchFeatureEnforcementTests
{
    /// <summary>
    /// GuestMessages: blocked, nobody writes - the guest, a waiter or a manager - the floor neither badges nor reminds, and
    /// a guest's answer buzzes no staff phone or tablet; the thread written before stays readable on both sides, and the
    /// booking's own "are you coming?" and its answer run on.
    /// </summary>
    [SkippableFact]
    public async Task Blocked_guest_messages_refuse_every_writer_and_the_badge_while_the_thread_stays_readable()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        await fixture.ClearOutboxAsync(factory.Clock);
        var (branch, _) = await ArrangeAsync(factory, tableCount: 2);

        using var diner = factory.CreateClientWithToken((await SignInVerifiedDinerAsync(factory)).Token);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        var booking = await CreatedAsync(diner, AppBooking(factory, branch, daysAhead: 2));
        var id = booking.GetProperty("id").GetGuid();
        var startUtc = booking.GetProperty("startUtc").GetDateTime();
        var guestThread = $"/api/diner/reservations/{id}/conversation";
        var staffThread = $"/api/reservations/{id}/conversation";

        factory.Clock.UtcNow = startUtc.AddDays(-1);

        var first = await OkAsync(await diner.PostAsJsonAsync(
            $"{guestThread}/messages", new { text = "Is there parking?", clientCommandId = Guid.CreateVersion7() }));
        var conversationId = first.GetProperty("conversationId").GetGuid();

        Assert.Equal(1, (await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor"))
            .GetProperty("messages").GetProperty("unreadTotal").GetInt32());

        Assert.True((await ReadAsync(manager, staffThread)).GetProperty("canWrite").GetBoolean());
        Assert.True((await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/conversations")).GetProperty("waitersCanWrite").GetBoolean());

        await BlockAsync(factory, branch.BranchId, BranchFeature.GuestMessages);

        // Nobody writes.
        await AssertRefusedAsync(
            await diner.PostAsJsonAsync($"{guestThread}/messages", new { text = "Hello?", clientCommandId = Guid.CreateVersion7() }),
            BranchFeature.GuestMessages,
            branch.BranchId);

        foreach (var staff in new[] { waiter, manager })
        {
            await AssertRefusedAsync(
                await staff.PostAsJsonAsync($"{staffThread}/messages", new { text = "Yes.", clientCommandId = Guid.CreateVersion7() }),
                BranchFeature.GuestMessages,
                branch.BranchId);
        }

        // Both sides still read what was written, and are told they cannot write.
        var guestView = await ReadAsync(diner, guestThread);
        Assert.Single(guestView.GetProperty("messages").EnumerateArray());
        Assert.False(guestView.GetProperty("guestsCanWrite").GetBoolean());

        // Nobody on the floor is offered the composer - a manager, who may always write otherwise, included.
        foreach (var staff in new[] { waiter, manager })
        {
            var staffView = await ReadAsync(staff, staffThread);
            Assert.Single(staffView.GetProperty("messages").EnumerateArray());
            Assert.False(staffView.GetProperty("canWrite").GetBoolean());
        }

        var inbox = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/conversations");
        Assert.Contains(inbox.GetProperty("items").EnumerateArray(), row => row.GetProperty("conversationId").GetGuid() == conversationId);
        Assert.False(inbox.GetProperty("waitersCanWrite").GetBoolean());

        // The floor neither badges nor reminds.
        var floor = await ReadAsync(waiter, $"/api/branches/{branch.BranchId}/tables/floor");
        Assert.Equal(0, floor.GetProperty("messages").GetProperty("unreadTotal").GetInt32());
        Assert.False(floor.GetProperty("messages").TryGetProperty("oldestUnansweredUtc", out var oldest)
                     && oldest.ValueKind != System.Text.Json.JsonValueKind.Null);

        // The booking's own question and its answer run on - and the answer buzzes nobody.
        factory.Clock.UtcNow = startUtc.AddMinutes(-60);
        await OkAsync(await waiter.PostAsync($"/api/reservations/{id}/ask-on-my-way", null));
        await OkAsync(await diner.PostAsJsonAsync($"/api/reservations/{id}/on-my-way", new { }));

        await using var db = fixture.CreateContext(factory.Clock);

        var keys = (await db.ConversationMessages.AsNoTracking()
                .Where(m => m.ConversationId == conversationId && m.Author == Domain.Messages.MessageAuthor.Guest)
                .Select(m => m.Id)
                .ToListAsync())
            .Select(m => $"staff-push:message:{m}")
            .ToList();

        Assert.Equal(2, keys.Count);

        var pushed = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Type == OutboxMessageTypes.StaffPush && keys.Contains(m.IdempotencyKey))
            .CountAsync();

        Assert.Equal(1, pushed);
    }
}
