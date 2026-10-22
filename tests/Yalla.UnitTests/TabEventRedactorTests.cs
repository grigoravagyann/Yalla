using System.Text.Json;
using System.Text.Json.Nodes;
using Yalla.Application.Ordering;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests;

/// <summary>
/// What each tab event shows a participant who may not see the table total, one type at a time.
/// </summary>
/// <remarks>
/// <para>
/// The rules are <c>docs/tabs.md</c>, section 7. They are tested here as a pure function over the
/// reader's own <see cref="TabView"/>, built by <see cref="TabProjection.Project"/> from an
/// in-memory tab - the same view the reader gets from <c>GET /api/tabs/{tabId}</c>, which is the
/// point: the stream and the bill must agree about whose money is whose.
/// </para>
/// <para>
/// The table: the host (sees everything), Ani (approved, the host hid the total from her), and a
/// pending joiner. The host ordered a coffee and a bottle of wine shared with Ani, in one order; Ani
/// ordered tea; a waiter keyed in bread for the table before Ani was let on, a jug of water for the
/// table after, and soup on Ani's behalf. A manager comped the whole tab, the coffee, the tea, the
/// wine and the jug.
/// </para>
/// </remarks>
public class TabEventRedactorTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly DateTime Opened = new(2026, 10, 18, 18, 0, 0, DateTimeKind.Utc);

    private static readonly Guid HostId = Guid.CreateVersion7();
    private static readonly Guid AniId = Guid.CreateVersion7();
    private static readonly Guid PendingId = Guid.CreateVersion7();
    private static readonly Guid WaiterId = Guid.CreateVersion7();

    private static readonly Guid HostOrderId = Guid.CreateVersion7();
    private static readonly Guid AniOrderId = Guid.CreateVersion7();
    private static readonly Guid WaiterOrderId = Guid.CreateVersion7();
    private static readonly Guid ForAniOrderId = Guid.CreateVersion7();

    private static readonly Guid CoffeeLineId = Guid.CreateVersion7();
    private static readonly Guid WineLineId = Guid.CreateVersion7();
    private static readonly Guid TeaLineId = Guid.CreateVersion7();
    private static readonly Guid BreadLineId = Guid.CreateVersion7();
    private static readonly Guid JugLineId = Guid.CreateVersion7();
    private static readonly Guid SoupLineId = Guid.CreateVersion7();

    // A comp off the whole tab, one off the host's coffee, one off Ani's tea, one off the wine, and
    // one off the jug of water a waiter keyed in for the table.
    private static readonly Guid TabCompId = Guid.CreateVersion7();
    private static readonly Guid CoffeeCompId = Guid.CreateVersion7();
    private static readonly Guid TeaCompId = Guid.CreateVersion7();
    private static readonly Guid WineCompId = Guid.CreateVersion7();
    private static readonly Guid JugCompId = Guid.CreateVersion7();

    // ------------------------------------------------------------ who gets what

    [Fact]
    public void The_audience_is_read_off_the_readers_own_view_of_the_tab()
    {
        Assert.Equal(TabEventAudience.Everything, For(HostId).Audience);
        Assert.Equal(TabEventAudience.WithoutTableMoney, For(AniId).Audience);
        Assert.Equal(TabEventAudience.Everything, For(AniId, aniSeesTotal: true).Audience);
        Assert.Equal(TabEventAudience.OnlyThemself, For(PendingId).Audience);
    }

    /// <summary>
    /// The pending joiner on this tab carries <c>CanSeeTableTotal</c> - the table default when it
    /// was not hidden - and still gets nothing of the table's: the projection requires approval as
    /// well as the flag, and the redactor takes the projection's word for it.
    /// </summary>
    [Fact]
    public void A_pending_joiner_with_the_flag_set_is_still_only_shown_themself()
    {
        var tab = Tab() with
        {
            Participants = [.. Tab().Participants.Select(p => p.ParticipantId == PendingId ? p with { CanSeeTableTotal = true } : p)],
        };

        Assert.Equal(TabEventAudience.OnlyThemself, TabEventRedactor.For(TabProjection.Project(tab, PendingId)!).Audience);
    }

    [Fact]
    public void Every_event_type_has_a_rule()
    {
        // A type added to the enum without a decision here reaches a restricted reader as an empty
        // payload - safe, and almost certainly not what its author meant. This is where they find out.
        Assert.All(Enum.GetValues<TabEventType>(), type => Assert.True(TabEventRedactor.HasRule(type), $"{type} has no rule."));
    }

    [Theory]
    [MemberData(nameof(EveryWrittenPayload))]
    public void A_reader_allowed_the_total_gets_every_payload_exactly_as_written(TabEventType type, object payload)
    {
        var written = Event(type, payload);

        // Not a copy that happens to be equal: the very event, untouched.
        Assert.Same(written, For(HostId).Redact(written));
        Assert.Same(written, For(AniId, aniSeesTotal: true).Redact(written));
    }

    [Theory]
    [MemberData(nameof(EveryWrittenPayload))]
    public void Only_the_payload_changes(TabEventType type, object payload)
    {
        var written = Event(type, payload);

        foreach (var reader in new[] { AniId, PendingId })
        {
            var read = For(reader).Redact(written);

            Assert.Equal(written.Sequence, read.Sequence);
            Assert.Equal(written.Type, read.Type);
            Assert.Equal(written.ActorType, read.ActorType);
            Assert.Equal(written.ActorId, read.ActorId);
            Assert.Equal(written.AtUtc, read.AtUtc);
            Assert.Equal(JsonValueKind.Object, read.Payload.ValueKind);
        }
    }

    // ------------------------------------------------------------ 1 TabOpened, 2-7 the participants

    [Fact]
    public void TabOpened_is_never_written_and_would_reach_a_restricted_reader_empty()
    {
        var read = For(AniId).Redact(Event(TabEventType.TabOpened, new { tableLabel = "7", totalAmd = 9_900L }));

        Assert.Empty(Members(read));
    }

    [Theory]
    [InlineData(TabEventType.ParticipantJoined)]
    [InlineData(TabEventType.ParticipantApproved)]
    [InlineData(TabEventType.ParticipantRejected)]
    [InlineData(TabEventType.ParticipantRemoved)]
    [InlineData(TabEventType.ParticipantPermissionsChanged)]
    public void A_participant_event_carries_no_money_and_names_nobody_to_a_pending_reader(TabEventType type)
    {
        var aboutHost = Event(type, ParticipantPayload(HostId, "Aram"));

        // Ani may read the roster: everything, the name included.
        Assert.Equal(Members(aboutHost), Members(For(AniId).Redact(aboutHost)));

        // The pending joiner may not: who it is about, and nothing that says who they are.
        var pending = For(PendingId).Redact(aboutHost);
        Assert.Equal(["canOrder", "canPay", "canSeeTableTotal", "left", "participantId", "status"], Members(pending));
        Assert.Equal(HostId, pending.Payload.GetProperty("participantId").GetGuid());

        // Their own approval names them - it is their own name.
        var aboutThemself = For(PendingId).Redact(Event(type, ParticipantPayload(PendingId, "Guest 3")));
        Assert.Equal("Guest 3", aboutThemself.Payload.GetProperty("displayName").GetString());
    }

    [Fact]
    public void ParticipantRenamed_shows_the_new_name_to_the_roster_and_not_to_a_pending_reader()
    {
        var renamed = Event(TabEventType.ParticipantRenamed, new { participantId = AniId, displayName = "Ani K." });

        Assert.Equal("Ani K.", For(HostId).Redact(renamed).Payload.GetProperty("displayName").GetString());
        Assert.Equal("Ani K.", For(AniId).Redact(renamed).Payload.GetProperty("displayName").GetString());
        Assert.Equal(["participantId"], Members(For(PendingId).Redact(renamed)));
    }

    // ------------------------------------------------------------ the events with nothing to take out

    [Theory]
    [MemberData(nameof(PayloadsWithNoMoneyAndNoNames))]
    public void An_event_with_no_money_and_no_names_reaches_every_reader_whole(TabEventType type, object payload)
    {
        var written = Event(type, payload);

        foreach (var reader in new[] { AniId, PendingId })
        {
            Assert.True(
                JsonNode.DeepEquals(JsonNode.Parse(written.Payload.GetRawText()), JsonNode.Parse(For(reader).Redact(written).Payload.GetRawText())),
                $"{type} changed for {reader}.");
        }
    }

    // ------------------------------------------------------------ 10 OrderPlaced

    [Fact]
    public void OrderPlaced_keeps_only_the_readers_own_lines_out_of_somebody_elses_order()
    {
        // The host's order: their coffee, and the wine Ani shares.
        var read = For(AniId).Redact(Event(TabEventType.OrderPlaced, HostOrderPayload()));

        // Who placed it and when it will be ready - nothing to do with money.
        Assert.Equal(HostOrderId, read.Payload.GetProperty("orderId").GetGuid());
        Assert.Equal(HostId, read.Payload.GetProperty("placedByParticipantId").GetGuid());
        Assert.True(read.Payload.TryGetProperty("estimatedReadyAtUtc", out _));

        // The wine is hers too, price and all, exactly as her bill shows it.
        var line = Assert.Single(read.Payload.GetProperty("lines").EnumerateArray());
        Assert.Equal(WineLineId, line.GetProperty("lineId").GetGuid());
        Assert.Equal(9_500L, line.GetProperty("unitPriceAmd").GetInt64());

        // The coffee is gone whole - the name with the price, because the menu is public and a name
        // and a quantity are a price.
        var raw = read.Payload.GetRawText();
        Assert.DoesNotContain("Coffee", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(CoffeeLineId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2500", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderPlaced_of_the_readers_own_order_is_whole()
    {
        var written = Event(TabEventType.OrderPlaced, new
        {
            orderId = AniOrderId,
            placedByParticipantId = AniId,
            placedByStaffId = (Guid?)null,
            onBehalfOfParticipantId = (Guid?)null,
            estimatedReadyAtUtc = Opened.AddMinutes(10),
            lines = new[] { OrderLine(TeaLineId, "Tea", 2_400L) },
        });

        var read = For(AniId).Redact(written);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(written.Payload.GetRawText()), JsonNode.Parse(read.Payload.GetRawText())));
    }

    [Fact]
    public void OrderPlaced_by_a_waiter_for_the_table_itemises_nothing_for_a_reader_without_the_total()
    {
        // The bread went on before Ani was let on, so it splits without her; the jug after, so it
        // splits with her and /shares charges her a part. Neither is itemised for her: a waiter's
        // lines for nobody in particular can be the whole bill.
        var read = For(AniId).Redact(Event(TabEventType.OrderPlaced, new
        {
            orderId = WaiterOrderId,
            placedByParticipantId = (Guid?)null,
            placedByStaffId = WaiterId,
            onBehalfOfParticipantId = (Guid?)null,
            estimatedReadyAtUtc = Opened.AddMinutes(5),
            lines = new[]
            {
                OrderLine(BreadLineId, "Lavash", 800L) with { IsTableAttributed = true },
                OrderLine(JugLineId, "Water, jug", 600L) with { IsTableAttributed = true },
            },
        }));

        // The order is still named - the diner app's "added by staff" marker needs nothing else -
        // and the lines member is there, empty, rather than missing from an order.
        Assert.Equal(WaiterOrderId, read.Payload.GetProperty("orderId").GetGuid());
        Assert.Equal(WaiterId, read.Payload.GetProperty("placedByStaffId").GetGuid());
        Assert.Equal(0, read.Payload.GetProperty("lines").GetArrayLength());

        var stranger = For(PendingId).Redact(Event(TabEventType.OrderPlaced, new
        {
            orderId = WaiterOrderId,
            lines = new[] { OrderLine(JugLineId, "Water, jug", 600L) with { IsTableAttributed = true } },
        }));
        Assert.Equal(0, stranger.Payload.GetProperty("lines").GetArrayLength());
    }

    /// <summary>
    /// The stream asks the same two questions as the bill. For every status and flag, the audience
    /// read off the row is the one read off the view, and it gives everything exactly when the view
    /// shows the table total.
    /// </summary>
    [Theory]
    [InlineData(ParticipantStatus.PendingApproval, false)]
    [InlineData(ParticipantStatus.PendingApproval, true)]
    [InlineData(ParticipantStatus.Approved, false)]
    [InlineData(ParticipantStatus.Approved, true)]
    [InlineData(ParticipantStatus.Removed, false)]
    [InlineData(ParticipantStatus.Removed, true)]
    public void The_audience_off_the_row_is_the_audience_off_the_view(ParticipantStatus status, bool canSeeTableTotal)
    {
        var tab = Tab() with
        {
            Participants =
            [
                .. Tab().Participants.Select(p => p.ParticipantId == AniId
                    ? p with { Status = status, CanSeeTableTotal = canSeeTableTotal }
                    : p),
            ],
        };

        var view = TabProjection.Project(tab, AniId)!;
        var fromRow = TabEventRedactor.AudienceFor(status, canSeeTableTotal);

        Assert.Equal(fromRow, TabEventRedactor.For(view).Audience);
        Assert.Equal(view.TableTotalVisible, fromRow == TabEventAudience.Everything);
    }

    [Fact]
    public void OrderPlaced_by_a_waiter_on_the_readers_behalf_is_theirs_whole()
    {
        var written = Event(TabEventType.OrderPlaced, new
        {
            orderId = ForAniOrderId,
            placedByParticipantId = (Guid?)null,
            placedByStaffId = WaiterId,
            onBehalfOfParticipantId = AniId,
            estimatedReadyAtUtc = Opened.AddMinutes(15),
            lines = new[] { OrderLine(SoupLineId, "Spas", 1_800L) },
        });

        var read = For(AniId).Redact(written);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(written.Payload.GetRawText()), JsonNode.Parse(read.Payload.GetRawText())));
    }

    [Fact]
    public void OrderPlaced_shows_a_pending_reader_no_lines_at_all()
    {
        var read = For(PendingId).Redact(Event(TabEventType.OrderPlaced, HostOrderPayload()));

        Assert.Equal(0, read.Payload.GetProperty("lines").GetArrayLength());
    }

    /// <summary>
    /// A line the reader's view has never heard of is not theirs. The query reads the tab after the
    /// events so this does not happen to a reader's own new line; if it ever did, the answer is to
    /// show too little rather than too much.
    /// </summary>
    [Fact]
    public void OrderPlaced_drops_a_line_the_readers_view_does_not_have()
    {
        var read = For(AniId).Redact(Event(TabEventType.OrderPlaced, new
        {
            orderId = Guid.CreateVersion7(),
            placedByParticipantId = AniId,
            lines = new[] { OrderLine(Guid.CreateVersion7(), "Baklava", 1_800L) },
        }));

        Assert.Equal(0, read.Payload.GetProperty("lines").GetArrayLength());
    }

    // ------------------------------------------------------------ 12 LineVoided

    [Fact]
    public void LineVoided_says_which_dish_and_why_only_on_the_readers_own_line()
    {
        var theirs = For(AniId).Redact(Event(TabEventType.LineVoided, VoidPayload(CoffeeLineId, HostOrderId, "Coffee")));

        Assert.Equal(["lineId", "orderId", "voidedByStaffId"], Members(theirs));
        Assert.Equal(CoffeeLineId, theirs.Payload.GetProperty("lineId").GetGuid());

        var hers = For(AniId).Redact(Event(TabEventType.LineVoided, VoidPayload(TeaLineId, AniOrderId, "Tea")));

        Assert.Equal("Tea", hers.Payload.GetProperty("name").GetString());
        Assert.Equal("wrong order", hers.Payload.GetProperty("reason").GetString());
    }

    // ------------------------------------------------------------ 13 AdjustmentAdded, 14 AdjustmentVoided

    /// <summary>
    /// "Hers" is what her bill lists: <see cref="TabProjection"/> shows a reader without the total the
    /// adjustments on her own lines and no others, and the stream takes its word for which those are.
    /// </summary>
    [Fact]
    public void The_stream_and_the_bill_agree_on_whose_adjustment_it_is()
    {
        var bill = TabProjection.Project(Tab(), AniId)!;

        Assert.Equal(
            new[] { TeaCompId, WineCompId }.Order(),
            bill.Adjustments.Select(a => a.AdjustmentId).Order());
    }

    [Fact]
    public void AdjustmentAdded_says_what_how_much_and_why_only_when_it_is_off_the_readers_own_line()
    {
        // Off the whole tab, off the host's coffee and off the jug the waiter keyed in for the table:
        // that something came off, and nothing else. The flat amount is the table's money and a
        // percentage of the whole tab is the subtotal's shadow; the jug is not itemised for her, so
        // neither is a comp on it. None is on her bill, so none is on her stream.
        foreach (var (adjustmentId, lineId) in new[] { (TabCompId, (Guid?)null), (CoffeeCompId, CoffeeLineId), (JugCompId, JugLineId) })
        {
            var notHers = For(AniId).Redact(Event(TabEventType.AdjustmentAdded, AdjustmentPayload(adjustmentId, lineId, amountAmd: 1_000L)));

            Assert.Equal(["adjustmentId", "tabOrderLineId"], Members(notHers));
        }

        var offHers = For(AniId).Redact(Event(TabEventType.AdjustmentAdded, AdjustmentPayload(TeaCompId, TeaLineId, amountAmd: 400L)));
        Assert.Equal(["adjustmentId", "amountAmd", "kind", "percent", "reason", "tabOrderLineId"], Members(offHers));
        Assert.Equal(400L, offHers.Payload.GetProperty("amountAmd").GetInt64());
        Assert.Equal("sorry about the wait", offHers.Payload.GetProperty("reason").GetString());

        // The wine is hers too, so the comp on it is.
        var offTheWine = For(AniId).Redact(Event(TabEventType.AdjustmentAdded, AdjustmentPayload(WineCompId, WineLineId, amountAmd: 1_500L)));
        Assert.Equal(1_500L, offTheWine.Payload.GetProperty("amountAmd").GetInt64());
    }

    [Fact]
    public void AdjustmentAdded_by_a_percentage_off_the_whole_tab_does_not_say_the_percentage()
    {
        var read = For(AniId).Redact(Event(TabEventType.AdjustmentAdded, AdjustmentPayload(TabCompId, null, amountAmd: null, percent: 10m)));

        Assert.Equal(["adjustmentId", "tabOrderLineId"], Members(read));
    }

    [Fact]
    public void AdjustmentAdded_the_readers_view_has_never_heard_of_is_not_theirs()
    {
        var read = For(AniId).Redact(Event(TabEventType.AdjustmentAdded, AdjustmentPayload(Guid.CreateVersion7(), TeaLineId, amountAmd: 400L)));

        Assert.False(read.Payload.TryGetProperty("amountAmd", out _));
    }

    [Fact]
    public void AdjustmentVoided_says_why_only_of_the_readers_own_adjustment()
    {
        var hers = For(AniId).Redact(Event(TabEventType.AdjustmentVoided, new { adjustmentId = TeaCompId, reason = "applied twice" }));
        Assert.Equal("applied twice", hers.Payload.GetProperty("reason").GetString());

        var theTables = For(AniId).Redact(Event(TabEventType.AdjustmentVoided, new { adjustmentId = TabCompId, reason = "applied twice" }));
        Assert.Equal(["adjustmentId"], Members(theTables));

        var toAStranger = For(PendingId).Redact(Event(TabEventType.AdjustmentVoided, new { adjustmentId = TeaCompId, reason = "applied twice" }));
        Assert.Equal(["adjustmentId"], Members(toAStranger));
    }

    // ------------------------------------------------------------ 15 PaymentRecorded

    [Fact]
    public void PaymentRecorded_says_how_much_only_of_the_readers_own_payment_and_never_what_is_left()
    {
        var hers = For(AniId).Redact(Event(TabEventType.PaymentRecorded, PaymentPayload(AniId)));

        Assert.Equal(3_520L, hers.Payload.GetProperty("amountAmd").GetInt64());
        Assert.Equal(300L, hers.Payload.GetProperty("tipAmd").GetInt64());
        Assert.False(hers.Payload.TryGetProperty("remainingAmd", out _));

        var theHosts = For(AniId).Redact(Event(TabEventType.PaymentRecorded, PaymentPayload(HostId)));
        Assert.Equal(["byStaffId", "method", "paymentId", "tabParticipantId"], Members(theHosts));

        // Cash a waiter took for the table, from nobody in particular, is nobody's own.
        var theTables = For(AniId).Redact(Event(TabEventType.PaymentRecorded, PaymentPayload(null)));
        Assert.Equal(["byStaffId", "method", "paymentId", "tabParticipantId"], Members(theTables));
        Assert.Equal(JsonValueKind.Null, theTables.Payload.GetProperty("tabParticipantId").ValueKind);
    }

    [Fact]
    public void PaymentRecorded_shows_a_pending_reader_no_amounts()
    {
        var read = For(PendingId).Redact(Event(TabEventType.PaymentRecorded, PaymentPayload(AniId)));

        Assert.Equal(["byStaffId", "method", "paymentId", "tabParticipantId"], Members(read));
    }

    // ------------------------------------------------------------ 16 ServiceRequested, 17 acknowledged

    [Fact]
    public void ServiceRequested_keeps_somebody_elses_note_from_a_pending_reader_only()
    {
        var hosts = Event(TabEventType.ServiceRequested, ServiceRequestPayload(HostId, "sparkling, please"));

        // Ani sees the roster, so she may see who asked for what.
        Assert.Equal("sparkling, please", For(AniId).Redact(hosts).Payload.GetProperty("note").GetString());

        var pending = For(PendingId).Redact(hosts);
        Assert.Equal(["billScope", "preset", "requestedByParticipantId", "serviceRequestId", "tipPercent"], Members(pending));

        var theirOwn = For(PendingId).Redact(Event(TabEventType.ServiceRequested, ServiceRequestPayload(PendingId, "a high chair")));
        Assert.Equal("a high chair", theirOwn.Payload.GetProperty("note").GetString());
    }

    // ------------------------------------------------------------ 18-20 the end of the tab

    [Fact]
    public void TabClosed_never_says_what_the_table_came_to()
    {
        foreach (var reader in new[] { AniId, PendingId })
        {
            var read = For(reader).Redact(Event(TabEventType.TabClosed, new
            {
                closedAtUtc = Opened.AddHours(2),
                totalAmd = 15_510L,
                tableSessionClosed = true,
            }));

            Assert.Equal(["closedAtUtc", "tableSessionClosed"], Members(read));
        }
    }

    [Fact]
    public void TabAbandoned_never_says_how_much_was_written_off()
    {
        var read = For(AniId).Redact(Event(TabEventType.TabAbandoned, new
        {
            reason = "walked out",
            writtenOffAmd = 6_160L,
            byStaffId = WaiterId,
        }));

        Assert.Equal(["byStaffId", "reason"], Members(read));
    }

    // ------------------------------------------------------------ failing closed

    [Fact]
    public void A_member_nobody_has_decided_about_is_left_out_for_a_reader_without_the_total()
    {
        var written = Event(TabEventType.OrderStatusChanged, new
        {
            orderId = HostOrderId,
            fromStatus = 1,
            toStatus = 2,
            byStaffId = WaiterId,
            tableSubtotalAmd = 9_000L,
        });

        Assert.False(For(AniId).Redact(written).Payload.TryGetProperty("tableSubtotalAmd", out _));
        Assert.Equal(9_000L, For(HostId).Redact(written).Payload.GetProperty("tableSubtotalAmd").GetInt64());
    }

    [Fact]
    public void A_type_this_build_does_not_know_reaches_a_restricted_reader_empty()
    {
        var written = Event((TabEventType)99, new { somethingNew = 1, amountAmd = 5_000L });

        Assert.Empty(Members(For(AniId).Redact(written)));
        Assert.Equal(["amountAmd", "somethingNew"], Members(For(HostId).Redact(written)));
    }

    [Fact]
    public void A_payload_that_is_not_an_object_is_read_as_empty()
    {
        var written = new TabEventView(
            1L, TabEventType.PaymentRecorded, JsonSerializer.SerializeToElement(12_345L), ActorType.Staff, WaiterId, Opened);

        Assert.Empty(Members(For(AniId).Redact(written)));
    }

    // ------------------------------------------------------------ the data

    /// <summary>One payload per type, shaped as its writer shapes it.</summary>
    public static TheoryData<TabEventType, object> EveryWrittenPayload() => new()
    {
        { TabEventType.TabOpened, new { tableLabel = "7" } },
        { TabEventType.ParticipantJoined, ParticipantPayload(PendingId, "Guest 3") },
        { TabEventType.ParticipantApproved, ParticipantPayload(AniId, "Ani") },
        { TabEventType.ParticipantRejected, ParticipantPayload(PendingId, "Guest 3") },
        { TabEventType.ParticipantRemoved, ParticipantPayload(AniId, "Ani") },
        { TabEventType.ParticipantPermissionsChanged, ParticipantPayload(AniId, "Ani") },
        { TabEventType.ParticipantRenamed, new { participantId = AniId, displayName = "Ani K." } },
        { TabEventType.HostReassigned, new { fromParticipantId = HostId, toParticipantId = AniId } },
        { TabEventType.SettlementModeChanged, new { settlementMode = 2, lockedAtUtc = (DateTime?)null } },
        { TabEventType.OrderPlaced, HostOrderPayload() },
        { TabEventType.OrderStatusChanged, new { orderId = HostOrderId, fromStatus = 1, toStatus = 2, byStaffId = WaiterId } },
        { TabEventType.LineVoided, VoidPayload(CoffeeLineId, HostOrderId, "Coffee") },
        { TabEventType.AdjustmentAdded, AdjustmentPayload(TabCompId, null, amountAmd: 1_000L) },
        { TabEventType.AdjustmentVoided, new { adjustmentId = TabCompId, reason = "sorry about the wait" } },
        { TabEventType.PaymentRecorded, PaymentPayload(HostId) },
        { TabEventType.ServiceRequested, ServiceRequestPayload(HostId, "sparkling, please") },
        { TabEventType.ServiceRequestAcknowledged, new { serviceRequestId = Guid.CreateVersion7(), byStaffId = WaiterId } },
        { TabEventType.TabClosing, new { atUtc = Opened.AddHours(1) } },
        { TabEventType.TabClosed, new { closedAtUtc = Opened.AddHours(2), totalAmd = 15_510L, tableSessionClosed = true } },
        { TabEventType.TabAbandoned, new { reason = "walked out", writtenOffAmd = 6_160L, byStaffId = WaiterId } },
    };

    /// <summary>The five types whose payloads carry neither money nor anybody's words.</summary>
    public static TheoryData<TabEventType, object> PayloadsWithNoMoneyAndNoNames() => new()
    {
        { TabEventType.HostReassigned, new { fromParticipantId = HostId, toParticipantId = AniId } },
        { TabEventType.SettlementModeChanged, new { settlementMode = 2, lockedAtUtc = Opened.AddMinutes(40) } },
        { TabEventType.OrderStatusChanged, new { orderId = HostOrderId, fromStatus = 1, toStatus = 2, byStaffId = WaiterId } },
        { TabEventType.ServiceRequestAcknowledged, new { serviceRequestId = Guid.CreateVersion7(), byStaffId = WaiterId } },
        { TabEventType.TabClosing, new { atUtc = Opened.AddHours(1) } },
    };

    private static object ParticipantPayload(Guid participantId, string displayName) => new
    {
        participantId,
        displayName,
        status = (int)ParticipantStatus.Approved,
        canOrder = true,
        canSeeTableTotal = false,
        canPay = false,
        left = true,
    };

    private static object HostOrderPayload() => new
    {
        orderId = HostOrderId,
        placedByParticipantId = HostId,
        placedByStaffId = (Guid?)null,
        onBehalfOfParticipantId = (Guid?)null,
        estimatedReadyAtUtc = Opened.AddMinutes(12),
        lines = new[]
        {
            OrderLine(CoffeeLineId, "Coffee", 2_500L),
            OrderLine(WineLineId, "Areni red, bottle", 9_500L) with { IsShared = true },
        },
    };

    private static OrderLinePayload OrderLine(Guid lineId, string name, long unitPriceAmd) =>
        new(lineId, name, unitPriceAmd, Quantity: 1, IsShared: false, IsTableAttributed: false);

    private static object VoidPayload(Guid lineId, Guid orderId, string name) => new
    {
        lineId,
        orderId,
        name,
        reason = "wrong order",
        voidedByStaffId = WaiterId,
    };

    private static object AdjustmentPayload(Guid adjustmentId, Guid? tabOrderLineId, long? amountAmd, decimal? percent = null) => new
    {
        adjustmentId,
        tabOrderLineId,
        kind = (int)AdjustmentKind.Comp,
        percent,
        amountAmd,
        reason = "sorry about the wait",
    };

    private static object PaymentPayload(Guid? tabParticipantId) => new
    {
        paymentId = Guid.CreateVersion7(),
        amountAmd = 3_520L,
        tipAmd = 300L,
        method = (int)PaymentMethod.Cash,
        tabParticipantId,
        byStaffId = WaiterId,
        remainingAmd = 11_990L,
    };

    private static object ServiceRequestPayload(Guid requestedByParticipantId, string note) => new
    {
        serviceRequestId = Guid.CreateVersion7(),
        preset = (int)ServiceRequestPreset.Water,
        note,
        requestedByParticipantId,
        billScope = (int?)null,
        tipPercent = (int?)null,
    };

    private static TabEventView Event(TabEventType type, object payload) =>
        new(7L, type, JsonSerializer.SerializeToElement(payload, Web), ActorType.Staff, WaiterId, Opened.AddMinutes(30));

    /// <summary>The payload's member names, sorted, so a test can say exactly what is left.</summary>
    private static string[] Members(TabEventView tabEvent) =>
        [.. tabEvent.Payload.EnumerateObject().Select(m => m.Name).Order(StringComparer.Ordinal)];

    private static TabEventRedactor For(Guid reader, bool aniSeesTotal = false) =>
        TabEventRedactor.For(TabProjection.Project(Tab(aniSeesTotal), reader)!);

    private static TabSnapshot Tab(bool aniSeesTotal = false) =>
        new(
            TabId: Guid.CreateVersion7(),
            BranchId: Guid.CreateVersion7(),
            VenueName: "Ararat",
            BranchName: "Opera",
            DiningTableId: Guid.CreateVersion7(),
            TableLabel: "7",
            TimeZoneId: "Asia/Yerevan",
            Status: TabStatus.Open,
            SettlementMode: SettlementMode.EveryonePaysOwnItems,
            SettlementModeLockedAtUtc: null,
            HideTotalFromGuests: !aniSeesTotal,
            HostParticipantId: HostId,
            ServiceChargePercentSnapshot: 10m,
            OpenedAtUtc: Opened,
            ClosedAtUtc: null,
            SubtotalAmd: 15_200L,
            ServiceChargeAmd: 1_520L,
            TotalAmd: 16_720L,
            PaidAmd: 0L,
            RemainingAmd: 16_720L,
            MaxEventSequence: 9L,
            Participants:
            [
                new TabParticipantSnapshot(HostId, "Aram", ParticipantRole.Host, ParticipantStatus.Approved, true, true, true, Opened),
                new TabParticipantSnapshot(AniId, "Ani", ParticipantRole.Guest, ParticipantStatus.Approved, true, aniSeesTotal, false, Opened.AddMinutes(2)),
                new TabParticipantSnapshot(PendingId, "Guest 3", ParticipantRole.Guest, ParticipantStatus.PendingApproval, true, false, false, Opened.AddMinutes(5)),
            ],
            Lines:
            [
                Line(CoffeeLineId, HostOrderId, HostId, "Coffee", 2_500L),
                Line(WineLineId, HostOrderId, HostId, "Areni red, bottle", 9_500L) with
                {
                    IsShared = true,
                    SharedWithParticipantIds = [HostId, AniId],
                },
                Line(TeaLineId, AniOrderId, AniId, "Tea", 2_400L),
                Line(BreadLineId, WaiterOrderId, null, "Lavash", 800L) with
                {
                    IsTableAttributed = true,
                    SharedWithParticipantIds = [HostId],
                },
                Line(JugLineId, WaiterOrderId, null, "Water, jug", 600L) with
                {
                    IsTableAttributed = true,
                    SharedWithParticipantIds = [HostId, AniId],
                },
                Line(SoupLineId, ForAniOrderId, null, "Spas", 1_800L) with { OnBehalfOfParticipantId = AniId },
            ],
            Adjustments:
            [
                Adjustment(TabCompId, null, 1_000L),
                Adjustment(CoffeeCompId, CoffeeLineId, 500L),
                Adjustment(TeaCompId, TeaLineId, 400L),
                Adjustment(WineCompId, WineLineId, 1_500L),
                Adjustment(JugCompId, JugLineId, 100L),
            ]);

    private static TabAdjustmentSnapshot Adjustment(Guid adjustmentId, Guid? lineId, long amountAmd) =>
        new(
            adjustmentId,
            lineId,
            AdjustmentKind.Comp,
            Percent: null,
            AmountAmd: amountAmd,
            ReductionAmd: amountAmd,
            Reason: "sorry about the wait",
            CreatedAtUtc: Opened.AddMinutes(20),
            IsVoided: false);

    private static TabLineSnapshot Line(Guid lineId, Guid orderId, Guid? placedBy, string name, long unitPriceAmd) =>
        new(
            LineId: lineId,
            OrderId: orderId,
            MenuItemId: Guid.CreateVersion7(),
            PlacedByParticipantId: placedBy,
            Name: name,
            UnitPriceAmd: unitPriceAmd,
            Quantity: 1,
            Note: null,
            OrderStatus: TabOrderStatus.New,
            OrderedAtUtc: Opened.AddMinutes(8),
            IsShared: false,
            IsVoided: false,
            VoidedAtUtc: null,
            VoidReason: null,
            SharedWithParticipantIds: []);

    /// <summary>One line on an <c>OrderPlaced</c> payload, as <c>TabOrderService</c> writes it.</summary>
    private sealed record OrderLinePayload(
        Guid LineId,
        string Name,
        long UnitPriceAmd,
        int Quantity,
        bool IsShared,
        bool IsTableAttributed);
}
