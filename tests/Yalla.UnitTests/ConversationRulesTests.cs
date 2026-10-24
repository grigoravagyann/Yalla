using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Messages;
using Yalla.Domain.Takeaway;

namespace Yalla.UnitTests;

/// <summary>
/// The guest-message rules that need no database: a message's text, when a thread is open and readable, how fast a guest
/// may write, the venue's settings, and what a thread records when somebody writes, asks or answers. See
/// <c>docs/messages.md</c>.
/// </summary>
public class ConversationRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Text_is_trimmed_and_between_one_and_five_hundred_characters()
    {
        Assert.Equal("Hello", ConversationRules.RequireText("  Hello \n"));
        Assert.Equal(500, ConversationRules.RequireText(new string('a', 500)).Length);

        var blank = Assert.Throws<FieldValidationException>(() => ConversationRules.RequireText("   "));
        Assert.Equal("text", blank.Field);
        Assert.Equal(FieldBounds.Required, blank.Violations[0].Bound);

        var tooLong = Assert.Throws<FieldValidationException>(() => ConversationRules.RequireText(new string('a', 501)));
        Assert.Equal(FieldBounds.Max, tooLong.Violations[0].Bound);
        Assert.Throws<FieldValidationException>(() => ConversationRules.RequireText(null));
    }

    [Fact]
    public void A_preview_is_at_most_140_characters_with_an_ellipsis_for_the_cut()
    {
        Assert.Equal("Short", ConversationRules.Preview("Short"));

        var cut = ConversationRules.Preview(new string('a', 200));
        Assert.Equal(140, cut.Length);
        Assert.EndsWith("…", cut);
    }

    /// <summary>An emoji straddling the cut goes whole: a lone surrogate is a box on every phone.</summary>
    [Fact]
    public void A_preview_never_cuts_an_emoji_in_half()
    {
        // 138 letters, then an emoji whose two halves sit at 138 and 139 - the cut falls between them.
        var cut = ConversationRules.Preview(new string('a', 138) + "\U0001F600" + new string('b', 20));

        Assert.Equal(new string('a', 138) + "…", cut);
        Assert.DoesNotContain(cut, char.IsSurrogate);

        // An emoji wholly before the cut stays.
        var kept = ConversationRules.Preview(new string('a', 137) + "\U0001F600" + new string('b', 20));
        Assert.Equal(new string('a', 137) + "\U0001F600…", kept);
    }

    /// <summary>
    /// A booking's thread closes two hours after its end while it is live, and two hours after it ended otherwise - the
    /// cancellation, the decline, the no-show or the table given back. A guest reads it 30 days more.
    /// </summary>
    [Fact]
    public void A_booking_thread_closes_two_hours_after_the_booking_ends_or_ended()
    {
        var end = Now.AddHours(3);
        var cancelledAt = Now.AddDays(-1);

        Assert.Equal(end.AddHours(2), ConversationRules.ReservationClosesAtUtc(ReservationStatus.Confirmed, end, null));
        Assert.Equal(end.AddHours(2), ConversationRules.ReservationClosesAtUtc(ReservationStatus.Seated, end, cancelledAt));
        Assert.Equal(cancelledAt.AddHours(2), ConversationRules.ReservationClosesAtUtc(ReservationStatus.CancelledByDiner, end, cancelledAt));
        Assert.Equal(cancelledAt.AddHours(2), ConversationRules.ReservationClosesAtUtc(ReservationStatus.NoShow, end, cancelledAt));
        Assert.Equal(end.AddHours(2), ConversationRules.ReservationClosesAtUtc(ReservationStatus.Completed, end, null));

        var closes = cancelledAt.AddHours(2);
        Assert.True(ConversationRules.IsOpen(closes, closes.AddTicks(-1)));
        Assert.False(ConversationRules.IsOpen(closes, closes));
        Assert.True(ConversationRules.GuestMayRead(closes, closes.AddDays(30).AddTicks(-1)));
        Assert.False(ConversationRules.GuestMayRead(closes, closes.AddDays(30)));
    }

    [Fact]
    public void A_takeaway_thread_is_open_while_the_order_moves_and_two_hours_after_it_ends()
    {
        Assert.Null(ConversationRules.TakeawayClosesAtUtc(TakeawayStatus.Sent, null));
        Assert.Null(ConversationRules.TakeawayClosesAtUtc(TakeawayStatus.Ready, null));
        Assert.True(ConversationRules.IsOpen(null, Now));
        Assert.Equal(Now.AddHours(2), ConversationRules.TakeawayClosesAtUtc(TakeawayStatus.Collected, Now));
        Assert.Equal(Now.AddHours(2), ConversationRules.TakeawayClosesAtUtc(TakeawayStatus.Declined, Now));
    }

    /// <summary>"Are you coming?" is answered from two hours before the start until the end, on a confirmed booking.</summary>
    [Fact]
    public void An_answer_is_taken_from_two_hours_before_until_the_end_of_a_confirmed_booking()
    {
        var start = Now.AddHours(1);
        var end = start.AddHours(2);

        Assert.True(ConversationRules.AnswerAccepted(ReservationStatus.Confirmed, start, end, Now));
        Assert.False(ConversationRules.AnswerAccepted(ReservationStatus.Confirmed, Now.AddHours(3), Now.AddHours(5), Now));
        Assert.False(ConversationRules.AnswerAccepted(ReservationStatus.Confirmed, start, end, end));
        Assert.False(ConversationRules.AnswerAccepted(ReservationStatus.Seated, start, end, Now));
    }

    /// <summary>Five a minute: the sixth waits until the oldest of the five is a minute old; sixty in a thread, ever.</summary>
    [Fact]
    public void A_guest_writes_at_most_five_a_minute_and_sixty_a_thread()
    {
        Assert.Equal(Now, ConversationRules.NextGuestMessageAtUtc([], Now));

        var five = Enumerable.Range(0, 5).Select(i => Now.AddSeconds(-50 + (i * 10))).ToList();
        Assert.Equal(Now.AddSeconds(10), ConversationRules.NextGuestMessageAtUtc(five, Now));
        Assert.Equal(Now.AddSeconds(10), ConversationRules.NextGuestMessageAtUtc(five, Now.AddSeconds(10)));

        var four = five.Skip(1).ToList();
        Assert.Equal(Now, ConversationRules.NextGuestMessageAtUtc(four, Now));

        var sixty = Enumerable.Range(0, 60).Select(i => Now.AddHours(-i)).ToList();
        Assert.Null(ConversationRules.NextGuestMessageAtUtc(sixty, Now));
    }

    [Fact]
    public void The_settings_name_every_field_out_of_bounds()
    {
        Assert.Empty(ConversationRules.CheckSettings(null, 3));
        Assert.Empty(ConversationRules.CheckSettings(["OK, we'll wait"], 15));

        var violations = ConversationRules.CheckSettings(
            ["a", "", "c", "d", "e", "f", "g", "h", new string('x', 121)], 16);

        Assert.Equal(
            ["templates", "templates[1]", "templates[8]", "remindAfterMinutes"],
            violations.Select(v => v.Field).ToArray());
    }

    /// <summary>
    /// Writing marks the writer's side read; the ask is a booking's only; an answer is followed by its status line, with
    /// the time held until for a late one.
    /// </summary>
    [Fact]
    public void A_thread_records_who_wrote_and_marks_their_side_read()
    {
        var thread = Conversation.ForReservation(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        var guest = thread.WriteAsGuest("Hello", Guid.CreateVersion7(), Now);
        Assert.Equal(MessageAuthor.Guest, guest.Author);
        Assert.Equal(Now, thread.GuestLastReadUtc);
        Assert.Null(thread.VenueLastReadUtc);

        var staffId = Guid.CreateVersion7();
        var ask = thread.AskComing(staffId, "Aram", Now.AddMinutes(1));
        Assert.Equal(MessageKind.AskComing, ask.Kind);
        Assert.Null(ask.Body);
        Assert.Equal("Aram", ask.StaffName);
        Assert.Equal(Now.AddMinutes(1), thread.VenueLastReadUtc);
        Assert.Equal(Now.AddMinutes(1), thread.LastMessageUtc);

        var (answer, status) = thread.Answer(GuestAnswer.Late, 15, new TimeOnly(19, 45), Now.AddMinutes(2));
        Assert.Equal(MessageKind.Answer, answer.Kind);
        Assert.Equal(15, answer.LateMinutes);
        Assert.Equal(MessageAuthor.System, status.Author);
        Assert.Equal(new TimeOnly(19, 45), status.HeldUntilLocal);

        Assert.Throws<ArgumentException>(() => thread.Answer(GuestAnswer.Coming, 10, null, Now));

        // A read mark never moves back.
        thread.MarkReadByVenue(Now);
        Assert.Equal(Now.AddMinutes(1), thread.VenueLastReadUtc);

        var order = Conversation.ForTakeaway(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Now);
        Assert.Throws<DomainStateException>(() => order.AskComing(staffId, "Aram", Now));
    }

    [Fact]
    public void A_read_marks_up_to_what_was_shown_never_past_now_and_never_back()
    {
        Assert.Equal(Now, ConversationRules.ReadMarkAt(Now, null));
        Assert.Equal(Now.AddMinutes(-3), ConversationRules.ReadMarkAt(Now, Now.AddMinutes(-3)));
        Assert.Equal(Now, ConversationRules.ReadMarkAt(Now, Now.AddDays(1)));

        var thread = Conversation.ForReservation(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Now);
        thread.MarkReadByGuest(Now, Now.AddMinutes(-3));
        Assert.Equal(Now.AddMinutes(-3), thread.GuestLastReadUtc);
        thread.MarkReadByGuest(Now.AddMinutes(1), Now.AddMinutes(-10));
        Assert.Equal(Now.AddMinutes(-3), thread.GuestLastReadUtc);
        thread.MarkReadByGuest(Now.AddMinutes(2));
        Assert.Equal(Now.AddMinutes(2), thread.GuestLastReadUtc);

        thread.MarkReadByVenue(Now, Now.AddHours(1));
        Assert.Equal(Now, thread.VenueLastReadUtc);
        Assert.Throws<ArgumentException>(() => thread.MarkReadByVenue(Now, DateTime.SpecifyKind(Now, DateTimeKind.Local)));
    }
}
