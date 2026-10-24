using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Application.Messages;

namespace Yalla.Api.Endpoints;

/// <summary>
/// Guest messages: a venue and a guest write to each other inside one booking or one takeaway order. The diner app's
/// thread, the floor tablet's inbox and thread, the console's settings.
/// </summary>
/// <remarks>Rules and their reasons: <c>docs/messages.md</c>.</remarks>
public static class MessageEndpoints
{
    private const string MessageShape =
        "Each message: `author` (`guest`, `venue` or `system`), `kind` (`text` - has `text`, plain and never to be "
        + "linkified; `ask-coming` - the venue's \"are you coming?\"; `answer` - has `answer` `{ kind: coming | late | "
        + "not-coming, lateMinutes }`; `status` - the system line after an answer, has `status` `{ answer, heldUntilLocal, "
        + "cancelled }`), `createdAtUtc`. Oldest first. Write the words of the last three in the reader's language.";

    private const string OpenRule =
        "`open`: a booking's thread takes messages until two hours after the booking ends - or after it was cancelled, "
        + "declined, missed or completed; a takeaway order's until two hours after it was collected, declined, cancelled "
        + "or not collected. `closesAtUtc` says when (absent while an order is still moving).";

    private const string ReadBody =
        "Optional JSON body `{ upToUtc }`: the `createdAtUtc` of the newest message the screen has shown - the read mark "
        + "moves to it, so a message that arrived between the last poll and this call stays unread. Never past now (a "
        + "later `upToUtc` counts as now) and never backwards (an earlier one than the mark changes nothing). No body, or "
        + "no `upToUtc`: now.";

    private const string SendRefusals =
        "`text` is 1 to 500 characters after trimming (422 `validation-failed` naming `text`). **Idempotent on "
        + "`clientCommandId`**: a retry answers the message the first try wrote, `wasReplay: true`; the same id with other "
        + "words (or from another member of staff) is **409 `client-command-id-in-use`**. A closed thread: "
        + "**409 `conversation-closed`** (`context.subjectKind`, `subjectId`, `closedAtUtc`).";

    public static IEndpointRouteBuilder MapMessageEndpoints(this IEndpointRouteBuilder app)
    {
        MapDiner(app);
        MapStaff(app);
        MapSettings(app);

        return app;
    }

    // ---------------------------------------------------------------- the diner

    private static void MapDiner(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/diner")
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner);

        group.AddEndpointFilter<ClientCommandIdFilter>();

        foreach (var (kind, prefix, noun, name) in Subjects)
        {
            group.MapGet($"{prefix}/conversation", (Guid id, IGuestMessageService messages, CancellationToken ct) =>
                    GetForGuestAsync(new ConversationSubject(kind, id), messages, ct))
                .WithName($"get{name}Conversation")
                .WithSummary($"The {noun}'s thread with the venue")
                .WithDescription(
                    $"The caller's own {noun} only - anybody else's is 404, as is a thread that closed over 30 days ago. "
                    + "Nobody has written yet: `conversationId` absent and `messages` empty - the composer still shows "
                    + "while `open` and `guestsCanWrite`. The venue's side is the venue: no staff names here. "
                    + "`pendingAsk`: the venue asked \"are you coming?\", nobody answered since and the answer is still "
                    + "taken - show the answer bar (Coming / Late 10-15-30 / Not coming), which calls `confirmOnMyWay`, "
                    + "`reportRunningLate` and the booking's cancel. `venueLastReadUtc`: a guest message not after it is "
                    + "\"seen\". `unreadCount`: venue messages since the guest last read - mark read on open "
                    + "(`.../conversation/read`). Poll every 10 s while the screen is open.\n\n"
                    + OpenRule + "\n\n" + MessageShape)
                .Produces<DinerConversationView>()
                .ProducesProblemDetails(StatusCodes.Status404NotFound, $"No such {noun} of yours, or its thread closed over 30 days ago.");

            group.MapPost($"{prefix}/conversation/messages", (
                        Guid id, SendMessageRequest request, IGuestMessageService messages, CancellationToken ct) =>
                    WriteAsGuestAsync(new ConversationSubject(kind, id), request, messages, ct))
                .RequireRateLimiting(RateLimitingExtensions.DinerWritePolicy)
                .WithName($"send{name}Message")
                .WithSummary($"Write to the venue about the {noun}")
                .WithDescription(
                    "The first message makes the thread. " + SendRefusals + " The venue switched typed messages off: "
                    + "**403 `guests-cannot-write`** (`context.branchId`) - the answer buttons still work. At most 5 "
                    + "messages a minute and 60 in a thread: **429 `messages-too-fast`** (`context.conversationId`, "
                    + "`perMinute`, `perConversation`, and `retryAfterUtc` when waiting helps). The venue is not pushed - "
                    + "the tablet polls.")
                .Produces<DinerMessageResult>()
                .ProducesProblemDetails(StatusCodes.Status403Forbidden, "`guests-cannot-write`.")
                .ProducesProblemDetails(StatusCodes.Status404NotFound, $"No such {noun} of yours.")
                .ProducesProblemDetails(StatusCodes.Status409Conflict, "`conversation-closed` or `client-command-id-in-use`.")
                .ProducesProblem<Errors.ValidationFailedProblem>(StatusCodes.Status422UnprocessableEntity, "`text` blank or over 500 characters.")
                .ProducesProblemDetails(StatusCodes.Status429TooManyRequests, "`messages-too-fast`.");

            group.MapPost($"{prefix}/conversation/read", (
                        Guid id,
                        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] MarkConversationReadRequest? request,
                        IGuestMessageService messages,
                        CancellationToken ct) =>
                    MarkReadByGuestAsync(new ConversationSubject(kind, id), request, messages, ct))
                .WithName($"mark{name}ConversationRead")
                .WithSummary("The guest read the thread")
                .WithDescription(
                    "What the venue wrote up to the mark is read - the unread count and the nav dot go. Call it on open "
                    + "and when new messages arrive while open. No thread yet: nothing to mark, still 204. " + ReadBody)
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblemDetails(StatusCodes.Status404NotFound, $"No such {noun} of yours.");
        }

        group.MapGet("/messages/unread", GetUnreadAsync)
            .WithName("getUnreadMessages")
            .WithSummary("The guest's threads that are open or have something unread")
            .WithDescription(
                "For the Bookings tab's dot (`total` above zero) and the booking cards' message rows. Each item: "
                + "`conversationId`, `subjectKind` (`reservation` or `takeaway`) and `subjectId` (the thread's route), "
                + "`venueName`, `unread` (venue messages since the guest last read), the last message (`lastAuthor`, "
                + "`lastKind`, `lastText` - at most 140 characters, a `text` only - and `lastAtUtc`), `open`. Newest first.")
            .Produces<DinerUnreadMessages>();
    }

    // ---------------------------------------------------------------- the floor tablet

    private static void MapStaff(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/branches/{branchId:guid}/conversations", ListForBranchAsync)
            .WithTags(EndpointConventions.StaffTag)
            .RequireAuthorization(YallaPolicies.WaiterOrAbove, YallaPolicies.BranchScoped)
            .WithName("listBranchConversations")
            .WithSummary("The Messages tab: the branch's threads with guests")
            .WithDescription(
                "Threads that are open or had a message in the last 24 hours. A guest's typed message (`text`) the venue "
                + "has not answered since - with a text or an ask - in a thread still open, puts its thread first "
                + "(`oldestUnansweredGuestMessageUtc` - the longest waiting first; absent once the thread closed, as nobody "
                + "can answer it then); the rest newest first. A guest's answer to \"are you coming?\" (coming, late, not "
                + "coming) asks nothing back: it never waits and never rings, but is unread (`unreadFromGuest`) until "
                + "somebody opens the thread. Each: the subject (`kind`, `id`, `label` - the table or the `T-` code, `guestName`, "
                + "`localTime`, and `localDate`/`partySize` or `itemCount`/`totalAmd`), `lastMessage` (the last that is "
                + "not a system status line - the guest's words or answer, or the venue's - with `answer` on an answer and "
                + "`staffName` on a venue message), `latestStatus` (the newest status line's `heldUntilLocal` / "
                + "`cancelled`), `unreadFromGuest`, `awaitingGuestAnswer` (the "
                + "last is the venue's \"are you coming?\"), `open`. `unreadTotal` is the tab's badge; ring when a guest "
                + "message has waited `remindAfterMinutes`. `templates`: the venue's quick replies, absent for the app's "
                + "default four. `waitersCanWrite`: whether a waiter's composer shows. The floor (`getBranchFloor`) carries "
                + "the badge too - poll this when the tab is open. Waiter or above at the branch.")
            .Produces<BranchConversations>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Not floor staff at this branch.")
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        foreach (var (kind, prefix, noun, name) in Subjects)
        {
            // Not BranchScoped: the route names a booking or an order. The service loads it and refuses a caller not at its branch.
            var group = app.MapGroup($"/api{prefix[..prefix.IndexOf('/', 1)]}")
                .WithTags(EndpointConventions.StaffTag)
                .RequireAuthorization(YallaPolicies.WaiterOrAbove);

            group.AddEndpointFilter<ClientCommandIdFilter>();

            var route = prefix[prefix.IndexOf('/', 1)..];

            group.MapGet($"{route}/conversation", (Guid id, IGuestMessageService messages, CancellationToken ct) =>
                    GetForStaffAsync(new ConversationSubject(kind, id), messages, ct))
                .WithName($"getStaff{name}Conversation")
                .WithSummary($"The {noun}'s thread with its guest, as the venue reads it")
                .WithDescription(
                    "The guest's view plus `staffName` on each venue message (who wrote it, as they were named then), "
                    + "`guestName`, `guestPhone` (the number the booking or order holds, for the Call button - empty for a "
                    + "platform admin), `guestLastReadUtc` (a venue message not after it is \"seen\") and "
                    + "`canWrite` - open, and the caller a manager, the general manager or the owner, or a waiter while the "
                    + "venue lets waiters write; never a platform admin. `unreadCount`: guest messages since anybody at the "
                    + "venue last read it. Waiter or above at the subject's branch.\n\n" + OpenRule + "\n\n" + MessageShape)
                .Produces<StaffConversationView>()
                .ProducesProblemDetails(StatusCodes.Status403Forbidden, $"Not floor staff at this {noun}'s branch.")
                .ProducesProblemDetails(StatusCodes.Status404NotFound, $"No such {noun}.");

            group.MapPost($"{route}/conversation/messages", (
                        Guid id, SendMessageRequest request, IGuestMessageService messages, CancellationToken ct) =>
                    WriteAsStaffAsync(new ConversationSubject(kind, id), request, messages, ct))
                .WithName($"sendStaff{name}Message")
                .WithSummary($"Write to the {noun}'s guest")
                .WithDescription(
                    "The guest is told at once: push `message.to-guest` (data `kind` `venue-message`, the venue's name over "
                    + "the text; `categoryId` `venue-message` - a Reply action - or `venue-message-readonly` - no actions - "
                    + "while the venue has `guestsCanWrite` off) and feed `venue-message`. The message keeps the "
                    + "writer's name for staff; the guest sees the venue's. " + SendRefusals + " A platform admin reads "
                    + "only, the kitchen never writes: 403 `forbidden`. A waiter where the venue keeps messages to "
                    + "managers: **403 `waiters-cannot-write`** (`context.branchId`). \"Ask: are you coming?\" is "
                    + "`askOnMyWay` - it writes its own message.")
                .Produces<StaffMessageResult>()
                .ProducesProblemDetails(StatusCodes.Status403Forbidden, "`forbidden` or `waiters-cannot-write`.")
                .ProducesProblemDetails(StatusCodes.Status404NotFound, $"No such {noun}.")
                .ProducesProblemDetails(StatusCodes.Status409Conflict, "`conversation-closed` or `client-command-id-in-use`.")
                .ProducesProblem<Errors.ValidationFailedProblem>(StatusCodes.Status422UnprocessableEntity, "`text` blank or over 500 characters.");

            group.MapPost($"{route}/conversation/read", (
                        Guid id,
                        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] MarkConversationReadRequest? request,
                        IGuestMessageService messages,
                        CancellationToken ct) =>
                    MarkReadByVenueAsync(new ConversationSubject(kind, id), request, messages, ct))
                .WithName($"markStaff{name}ConversationRead")
                .WithSummary("Somebody at the venue read the thread")
                .WithDescription(
                    "What the guest wrote up to the mark is read for the whole venue - any member of staff reading counts. "
                    + "A platform admin's read changes nothing. No thread yet: still 204. " + ReadBody)
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblemDetails(StatusCodes.Status403Forbidden, $"Not floor staff at this {noun}'s branch.")
                .ProducesProblemDetails(StatusCodes.Status404NotFound, $"No such {noun}.");
        }
    }

    // ---------------------------------------------------------------- the console

    private static void MapSettings(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/branches/{branchId:guid}/message-settings")
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.ManagerOrAbove)
            .RequireAuthorization(YallaPolicies.BranchScoped);

        group.MapGet("/", GetSettingsAsync)
            .WithName("getMessageSettings")
            .WithSummary("The branch's settings for messages with guests, with their bounds")
            .Produces<MessageSettingsView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.");

        group.MapPut("/", PutSettingsAsync)
            .WithName("putMessageSettings")
            .WithSummary("Save the branch's settings for messages with guests")
            .WithDescription(
                "`guestsCanWrite` (default true; off, guests answer with the buttons only), `waitersCanWrite` (default "
                + "true; managers, the general manager and the owner always may), `remindAfterMinutes` (1 to 15, default "
                + "3) - all three required - and `templates`: the quick replies, at most 8 of 1 to 120 characters after "
                + "trimming, or null (or left out) for the app's default set. Every bad field is named in one 422 "
                + "`validation-failed`: `guestsCanWrite`, `waitersCanWrite`, `remindAfterMinutes`, `templates`, "
                + "`templates[i]`.")
            .Produces<MessageSettingsView>()
            .ProducesProblemDetails(StatusCodes.Status404NotFound, "No such branch.")
            .ProducesProblem<Errors.ValidationFailedProblem>(StatusCodes.Status422UnprocessableEntity, "`context.fields` names each.");
    }

    // ---------------------------------------------------------------- handlers

    /// <summary>The two subjects: a booking and a takeaway order, their route prefix, the word for them, the id's name.</summary>
    private static readonly (ConversationSubjectKind Kind, string Prefix, string Noun, string Name)[] Subjects =
    [
        (ConversationSubjectKind.Reservation, "/reservations/{id:guid}", "booking", "Reservation"),
        (ConversationSubjectKind.Takeaway, "/takeaway-orders/{id:guid}", "takeaway order", "Takeaway"),
    ];

    private static async Task<IResult> GetForGuestAsync(
        ConversationSubject subject, IGuestMessageService messages, CancellationToken cancellationToken) =>
        Results.Ok(await messages.GetForGuestAsync(subject, cancellationToken));

    private static async Task<IResult> WriteAsGuestAsync(
        ConversationSubject subject, SendMessageRequest request, IGuestMessageService messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await messages.WriteAsGuestAsync(subject, request.Text, request.ClientCommandId, cancellationToken));
    }

    private static async Task<IResult> MarkReadByGuestAsync(
        ConversationSubject subject, MarkConversationReadRequest? request, IGuestMessageService messages, CancellationToken cancellationToken)
    {
        await messages.MarkReadByGuestAsync(subject, request?.UpToUtc, cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> GetUnreadAsync(IGuestMessageService messages, CancellationToken cancellationToken) =>
        Results.Ok(await messages.GetUnreadForGuestAsync(cancellationToken));

    private static async Task<IResult> ListForBranchAsync(
        Guid branchId, IGuestMessageService messages, CancellationToken cancellationToken) =>
        Results.Ok(await messages.ListForBranchAsync(branchId, cancellationToken));

    private static async Task<IResult> GetForStaffAsync(
        ConversationSubject subject, IGuestMessageService messages, CancellationToken cancellationToken) =>
        Results.Ok(await messages.GetForStaffAsync(subject, cancellationToken));

    private static async Task<IResult> WriteAsStaffAsync(
        ConversationSubject subject, SendMessageRequest request, IGuestMessageService messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await messages.WriteAsStaffAsync(subject, request.Text, request.ClientCommandId, cancellationToken));
    }

    private static async Task<IResult> MarkReadByVenueAsync(
        ConversationSubject subject, MarkConversationReadRequest? request, IGuestMessageService messages, CancellationToken cancellationToken)
    {
        await messages.MarkReadByVenueAsync(subject, request?.UpToUtc, cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> GetSettingsAsync(Guid branchId, IGuestMessageService messages, CancellationToken cancellationToken) =>
        Results.Ok(await messages.GetSettingsAsync(branchId, cancellationToken));

    private static async Task<IResult> PutSettingsAsync(
        Guid branchId, MessageSettingsRequest request, IGuestMessageService messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Results.Ok(await messages.SaveSettingsAsync(
            branchId,
            new MessageSettings(
                request.GuestsCanWrite!.Value,
                request.WaitersCanWrite!.Value,
                request.Templates,
                request.RemindAfterMinutes!.Value),
            cancellationToken));
    }
}

/// <summary>Body of a <c>.../conversation/messages</c> send - the guest's and the venue's.</summary>
/// <param name="Text">1 to 500 characters after trimming. Plain text.</param>
/// <param name="ClientCommandId">Idempotency key; reuse it only to retry this same message.</param>
public sealed record SendMessageRequest(
    [Required] string Text,
    [Required] Guid ClientCommandId) : IClientCommandRequest;

/// <summary>Optional body of a <c>.../conversation/read</c> - the guest's and the venue's.</summary>
/// <param name="UpToUtc">
/// The <c>createdAtUtc</c> of the newest message the screen has shown. The read mark moves to it - never past now, never
/// backwards. Absent: now.
/// </param>
public sealed record MarkConversationReadRequest(DateTime? UpToUtc);

/// <summary>Body of <c>PUT /api/branches/{branchId}/message-settings</c>.</summary>
/// <param name="GuestsCanWrite">Guests may type messages. Required.</param>
/// <param name="WaitersCanWrite">Waiters may write. Required.</param>
/// <param name="Templates">The quick replies, at most 8 of 1 to 120 characters; null or absent for the app's default set.</param>
/// <param name="RemindAfterMinutes">1 to 15. Required.</param>
public sealed record MessageSettingsRequest(
    // Nullable so [Required] can fire: a missing bool binds false and would silently switch guests' messages off.
    [Required] bool? GuestsCanWrite,
    [Required] bool? WaitersCanWrite,
    IReadOnlyList<string?>? Templates,
    [Required] int? RemindAfterMinutes);
