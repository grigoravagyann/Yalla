using Yalla.Domain.Messages;

namespace Yalla.Api.Errors;

internal static partial class ApiExceptionMapper
{
    /// <summary>Guest message threads.</summary>
    private static MappedError? MapMessages(Exception exception) => exception switch
    {
        // Guest messages. Each its own code, so the app and the tablet say what is true. The closed thread must stay
        // ABOVE DomainStateException; the other three derive from nothing more general than Exception.
        ConversationClosedException e => Refuse(
            ErrorCodes.ConversationClosed,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["subjectKind"] = e.SubjectKind,
                ["subjectId"] = e.SubjectId,
                ["closedAtUtc"] = e.ClosedAtUtc,
            }),

        GuestsCannotWriteException e => Refuse(
            ErrorCodes.GuestsCannotWrite,
            e.Message,
            context: new Dictionary<string, object?> { ["branchId"] = e.BranchId }),

        WaitersCannotWriteException e => Refuse(
            ErrorCodes.WaitersCannotWrite,
            e.Message,
            context: new Dictionary<string, object?> { ["branchId"] = e.BranchId }),

        // 429 rather than 409, as the table's waiter calls are: the guest should wait, not change what they send.
        MessagesTooFastException e => Refuse(
            ErrorCodes.MessagesTooFast,
            e.Message,
            context: e.RetryAfterUtc is { } retryAfterUtc
                ? new Dictionary<string, object?>
                {
                    ["conversationId"] = e.ConversationId,
                    ["perMinute"] = Domain.Messages.ConversationRules.GuestMessagesPerMinute,
                    ["perConversation"] = Domain.Messages.ConversationRules.GuestMessagesPerConversation,
                    ["retryAfterUtc"] = retryAfterUtc,
                }
                : new Dictionary<string, object?>
                {
                    ["conversationId"] = e.ConversationId,
                    ["perMinute"] = Domain.Messages.ConversationRules.GuestMessagesPerMinute,
                    ["perConversation"] = Domain.Messages.ConversationRules.GuestMessagesPerConversation,
                },
            retryAfterSeconds: e.RetryAfter is { } wait ? RetryAfterSeconds(wait) : null),

        _ => null,
    };
}
