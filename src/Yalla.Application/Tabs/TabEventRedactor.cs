using System.Buffers;
using System.Text.Json;
using Yalla.Application.Ordering;
using Yalla.Domain.Enums;

namespace Yalla.Application.Tabs;

/// <summary>How much of a tab's event stream one participant may read.</summary>
/// <remarks>
/// Not a fourth set of flags. It is decided by the two <see cref="TabPermissions"/> functions the
/// participant's own <see cref="TabView"/> is decided by - see <see cref="TabEventRedactor.AudienceFor"/>
/// - so the stream and the bill cannot disagree about who may see the money.
/// </remarks>
public enum TabEventAudience
{
    /// <summary>Approved and allowed the table total: the stream exactly as it was written.</summary>
    Everything = 1,

    /// <summary>
    /// Approved, with the table total hidden: their own items and their own money, the rest of the
    /// table's items and money not at all, and everyone's names.
    /// </summary>
    WithoutTableMoney = 2,

    /// <summary>
    /// Still waiting for the host: what <see cref="WithoutTableMoney"/> gets, less every name and
    /// note but their own - the roster is not theirs to read yet.
    /// </summary>
    OnlyThemself = 3,
}

/// <summary>
/// Takes out of a tab event's payload what its reader may not see, at read time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> <see cref="TabProjection"/> hides the table total, and everyone else's
/// items, from a guest the host hid it from. The event stream handed that same guest every payload
/// as written: the price of every line anybody ordered, every payment with the balance left after
/// it, and the total the tab closed on. The hidden total could be added back up from the stream, so
/// hiding it on the bill hid nothing.
/// </para>
/// <para>
/// <b>Driven by the projection, not beside it.</b> The audience is
/// <see cref="TabPermissions.MaySeeTableTotal"/> and <see cref="TabPermissions.MaySeeRoster"/> on
/// the reader's own row - the functions behind <see cref="TabView.TableTotalVisible"/> and the
/// roster - and "my line" is a line on <see cref="TabView.MyLines"/> and "my adjustment" one on
/// <see cref="TabView.Adjustments"/>: the same answers <c>GET /api/tabs/{tabId}</c> gives that
/// reader. A second derivation here would be a second place to decide visibility, and the day the
/// two disagreed would be the leak.
/// </para>
/// <para>
/// <b>Every event stays; only payload members go.</b> A client keeps the highest sequence it has
/// seen and treats a hole as missed events, so dropping an event would make every phone refetch on
/// every poll. The type, the sequence, the actor and the time are never touched.
/// </para>
/// <para>
/// <b>A whitelist, so it fails closed.</b> Each type names the members a restricted reader may have;
/// anything else - a member a writer adds next year, a type this build does not know - is left out
/// until somebody decides about it here. A reader who may see everything gets the payload untouched,
/// so a new member reaches the host the day it is written. Members are left out rather than set to
/// null, for the projection's reason: absent beside a hidden total reads as "not shown to you", and a
/// null might be drawn as zero.
/// </para>
/// <para>
/// The stored stream stays complete. This is applied on the participant read only, and the rules are
/// written out in <c>docs/tabs.md</c>, section 7.4.
/// </para>
/// </remarks>
public sealed class TabEventRedactor
{
    /// <summary>Line member that names the line on <c>OrderPlaced</c>'s <c>lines</c>.</summary>
    private const string LineIdMember = "lineId";

    /// <summary>The members of the participant events. Who they are about is not money.</summary>
    private static readonly Rule ParticipantRule = new(
        Subject.Participant,
        "participantId",
        Public: ["participantId", "status", "canOrder", "canSeeTableTotal", "canPay", "left"])
    {
        Named = ["displayName"],
    };

    /// <summary>One rule per type. <see cref="HasRule"/> is how a test makes a new type need one.</summary>
    private static readonly Dictionary<TabEventType, Rule> Rules = new()
    {
        // Never written today. Nothing to keep until somebody decides what it carries.
        [TabEventType.TabOpened] = new(Subject.None, null, Public: []),

        // Somebody landed pending. A participant event like the others, so a phone pending on the
        // wrong table's tab learns that somebody joined and not who.
        [TabEventType.ParticipantJoined] = ParticipantRule,
        [TabEventType.ParticipantApproved] = ParticipantRule,
        [TabEventType.ParticipantRejected] = ParticipantRule,
        [TabEventType.ParticipantRemoved] = ParticipantRule,
        [TabEventType.ParticipantPermissionsChanged] = ParticipantRule,
        [TabEventType.ParticipantRenamed] = new(Subject.Participant, "participantId", Public: ["participantId"])
        {
            Named = ["displayName"],
        },

        [TabEventType.HostReassigned] = new(
            Subject.None, null, Public: ["fromParticipantId", "toParticipantId"]),
        [TabEventType.SettlementModeChanged] = new(
            Subject.None, null, Public: ["settlementMode", "lockedAtUtc"]),

        // Who placed it and when it will be ready, for everyone; the lines, only those on the
        // reader's own lines (TabProjection.IsOwnLine). Any other line goes whole, name and quantity
        // with its price: the menu is public, so a name and a quantity are a price.
        [TabEventType.OrderPlaced] = new(
            Subject.None,
            null,
            Public: ["orderId", "placedByParticipantId", "placedByStaffId", "onBehalfOfParticipantId", "estimatedReadyAtUtc"])
        {
            OwnLinesMember = "lines",
        },

        [TabEventType.OrderStatusChanged] = new(
            Subject.None, null, Public: ["orderId", "fromStatus", "toStatus", "byStaffId"]),

        // That a line came off, for everyone - their total moved. Which dish and why, only on their
        // own line, for the same reason a foreign line is dropped from an order.
        [TabEventType.LineVoided] = new(Subject.Line, "lineId", Public: ["lineId", "orderId", "voidedByStaffId"])
        {
            Own = ["name", "reason"],
        },

        // That money came off, for everyone - their share may have moved. What, how much and why,
        // only for an adjustment their bill shows them: one on their own line. One off the whole
        // tab or somebody else's dish is not on their bill, so it is not on their stream either.
        [TabEventType.AdjustmentAdded] = new(
            Subject.Adjustment,
            "adjustmentId",
            Public: ["adjustmentId", "tabOrderLineId"])
        {
            Own = ["kind", "percent", "amountAmd", "reason"],
        },

        [TabEventType.AdjustmentVoided] = new(Subject.Adjustment, "adjustmentId", Public: ["adjustmentId"])
        {
            Own = ["reason"],
        },

        // A payment is theirs when it was theirs; what is left on the table never is.
        [TabEventType.PaymentRecorded] = new(
            Subject.Participant,
            "tabParticipantId",
            Public: ["paymentId", "method", "tabParticipantId", "byStaffId"])
        {
            Own = ["amountAmd", "tipAmd"],
            Table = ["remainingAmd"],
        },

        // No amounts since #36. The note is somebody's own words, and a pending reader is not shown
        // whose.
        [TabEventType.ServiceRequested] = new(
            Subject.Participant,
            "requestedByParticipantId",
            Public: ["serviceRequestId", "preset", "requestedByParticipantId", "billScope", "tipPercent"])
        {
            Named = ["note"],
        },

        [TabEventType.ServiceRequestAcknowledged] = new(
            Subject.None, null, Public: ["serviceRequestId", "byStaffId"]),
        [TabEventType.TabClosing] = new(Subject.None, null, Public: ["atUtc"]),
        [TabEventType.TabClosed] = new(Subject.None, null, Public: ["closedAtUtc", "tableSessionClosed"])
        {
            Table = ["totalAmd"],
        },
        [TabEventType.TabAbandoned] = new(Subject.None, null, Public: ["reason", "byStaffId"])
        {
            Table = ["writtenOffAmd"],
        },
    };

    private readonly Guid viewerParticipantId;
    private readonly HashSet<Guid> ownLineIds;
    private readonly HashSet<Guid> ownAdjustmentIds;

    private TabEventRedactor(
        TabEventAudience audience,
        Guid viewerParticipantId,
        HashSet<Guid> ownLineIds,
        HashSet<Guid> ownAdjustmentIds)
    {
        Audience = audience;
        this.viewerParticipantId = viewerParticipantId;
        this.ownLineIds = ownLineIds;
        this.ownAdjustmentIds = ownAdjustmentIds;
    }

    /// <summary>How much of the stream this reader gets.</summary>
    public TabEventAudience Audience { get; }

    /// <summary>
    /// How much of the stream a participant with this status and flag gets.
    /// </summary>
    /// <remarks>
    /// Cheap enough to ask before anything else is read: the reader's own row says whether they get
    /// <see cref="TabEventAudience.Everything"/>, in which case nothing more is needed. The same two
    /// <see cref="TabPermissions"/> functions decide <see cref="TabView.TableTotalVisible"/> and the
    /// roster on the tab read, so the stream cannot reach a different answer from the bill.
    /// </remarks>
    public static TabEventAudience AudienceFor(ParticipantStatus status, bool canSeeTableTotal) =>
        (TabPermissions.MaySeeTableTotal(status, canSeeTableTotal), TabPermissions.MaySeeRoster(status)) switch
        {
            (true, true) => TabEventAudience.Everything,
            (_, true) => TabEventAudience.WithoutTableMoney,
            _ => TabEventAudience.OnlyThemself,
        };

    /// <summary>
    /// The redactor for whoever <paramref name="viewer"/> is - their own projection of the tab.
    /// </summary>
    /// <remarks>
    /// Needed only for a reader <see cref="AudienceFor"/> restricts. Pass the view
    /// <see cref="TabProjection.Project"/> built for them from a snapshot read <b>after</b> the
    /// events: every line and adjustment an event names then exists on it, so a reader's own order
    /// placed a moment ago is never mistaken for somebody else's.
    /// </remarks>
    public static TabEventRedactor For(TabView viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        // "On the view" is "theirs": the projection lists a restricted reader's own lines in MyLines
        // and, of the comps, only those on them.
        return new TabEventRedactor(
            AudienceFor(viewer.Me.Status, viewer.Me.CanSeeTableTotal),
            viewer.Me.ParticipantId,
            [.. viewer.MyLines.Select(line => line.LineId)],
            [.. viewer.Adjustments.Select(adjustment => adjustment.AdjustmentId)]);
    }

    /// <summary>Whether <paramref name="type"/> has a rule here. A type without one is read as empty.</summary>
    public static bool HasRule(TabEventType type) => Rules.ContainsKey(type);

    /// <summary>
    /// Every top-level payload member the rule for <paramref name="type"/> has decided about, kept or
    /// not. A member a writer emits that is missing here is dropped for a restricted reader by
    /// default; the HTTP test uses this to make a new member need a decision.
    /// </summary>
    public static IReadOnlySet<string> ClassifiedMembers(TabEventType type) =>
        Rules.TryGetValue(type, out var rule)
            ? new HashSet<string>(
                [.. rule.Public, .. rule.Own, .. rule.Named, .. rule.Table, .. OptionalMember(rule.OwnLinesMember)],
                StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static string[] OptionalMember(string? name) => name is null ? [] : [name];

    /// <summary>The event as this reader may see it. Everything but the payload is untouched.</summary>
    public TabEventView Redact(TabEventView tabEvent)
    {
        ArgumentNullException.ThrowIfNull(tabEvent);

        return Audience == TabEventAudience.Everything
            ? tabEvent
            : tabEvent with { Payload = RedactPayload(tabEvent.Type, tabEvent.Payload) };
    }

    private JsonElement RedactPayload(TabEventType type, JsonElement payload)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            // A type with no rule, or a payload that is not an object, is read as an empty object:
            // there is nothing here that has been decided safe to show.
            if (payload.ValueKind == JsonValueKind.Object && Rules.TryGetValue(type, out var rule))
            {
                var isOwn = IsOwn(rule, payload);

                foreach (var member in payload.EnumerateObject())
                {
                    if (rule.OwnLinesMember is { } linesMember && member.NameEquals(linesMember))
                    {
                        WriteOwnLines(writer, member);
                    }
                    else if (Keeps(rule, member.Name, isOwn))
                    {
                        member.WriteTo(writer);
                    }
                }
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);

        return document.RootElement.Clone();
    }

    /// <summary>Whether this event is about the reader's own line or the reader themself.</summary>
    private bool IsOwn(Rule rule, JsonElement payload)
    {
        if (rule.SubjectMember is not { } subjectMember
            || !payload.TryGetProperty(subjectMember, out var subject)
            || subject.ValueKind != JsonValueKind.String
            || !subject.TryGetGuid(out var id))
        {
            return false;
        }

        return rule.Subject switch
        {
            Subject.Line => ownLineIds.Contains(id),
            Subject.Adjustment => ownAdjustmentIds.Contains(id),
            Subject.Participant => id == viewerParticipantId,
            _ => false,
        };
    }

    private bool Keeps(Rule rule, string name, bool isOwn) =>
        rule.Public.Contains(name, StringComparer.Ordinal)
        || (isOwn && rule.Own.Contains(name, StringComparer.Ordinal))
        || (rule.Named.Contains(name, StringComparer.Ordinal)
            && (isOwn || Audience != TabEventAudience.OnlyThemself));

    /// <summary>The order's lines that are the reader's own, each whole; an empty array otherwise.</summary>
    private void WriteOwnLines(Utf8JsonWriter writer, JsonProperty lines)
    {
        writer.WritePropertyName(lines.Name);
        writer.WriteStartArray();

        if (lines.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in lines.Value.EnumerateArray())
            {
                if (line.ValueKind == JsonValueKind.Object
                    && line.TryGetProperty(LineIdMember, out var lineId)
                    && lineId.ValueKind == JsonValueKind.String
                    && lineId.TryGetGuid(out var id)
                    && ownLineIds.Contains(id))
                {
                    line.WriteTo(writer);
                }
            }
        }

        writer.WriteEndArray();
    }

    /// <summary>What an event is about, for deciding whether it is the reader's own.</summary>
    private enum Subject
    {
        /// <summary>Nobody in particular: only the public members matter.</summary>
        None,

        /// <summary>An order line, named by the subject member; own when it is on the reader's <c>myLines</c>.</summary>
        Line,

        /// <summary>A comp or discount, named by the subject member; own when the reader's view lists it.</summary>
        Adjustment,

        /// <summary>A participant, named by the subject member; own when it is the reader.</summary>
        Participant,
    }

    /// <summary>What one event type's payload may show a reader without the table total.</summary>
    /// <param name="Subject">What the event is about.</param>
    /// <param name="SubjectMember">The member naming it, read as a GUID.</param>
    /// <param name="Public">Kept for every reader.</param>
    private sealed record Rule(Subject Subject, string? SubjectMember, string[] Public)
    {
        /// <summary>Kept only when the event is about the reader's own line or themself.</summary>
        public string[] Own { get; init; } = [];

        /// <summary>Names and notes: kept unless the reader is pending and it is not about them.</summary>
        public string[] Named { get; init; } = [];

        /// <summary>
        /// Table-wide money, never kept for a restricted reader. Listed so the decision is written
        /// down; anything unlisted is dropped all the same.
        /// </summary>
        public string[] Table { get; init; } = [];

        /// <summary>An array of lines, filtered to the reader's own.</summary>
        public string? OwnLinesMember { get; init; }
    }
}
