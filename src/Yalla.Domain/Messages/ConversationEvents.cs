using Yalla.Domain.Common;

namespace Yalla.Domain.Messages;

// What happens in a guest-message thread, as its handlers hear it (docs/domain-events.md, docs/messages.md). Each carries
// the thread and the message it gained: a handler reads both as the save sees them - the thread's subject, its account,
// whether it is new in this save; the message's id, words and instant. Read marks raise nothing: they tell nobody.

/// <summary>
/// The guest wrote in the thread - typed words, from the app. The floor hears of it (the <c>guestMessages</c> staff push).
/// </summary>
/// <param name="Thread">The thread, new in this save when this is its first message.</param>
/// <param name="Message">The guest's <see cref="MessageKind.Text"/>.</param>
/// <param name="OccurredAtUtc">When it was written.</param>
public sealed record GuestMessageWritten(Conversation Thread, ConversationMessage Message, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Somebody at the venue wrote in the thread. The guest is told, and the venue's History says who answered, and after how
/// long the guest had waited.
/// </summary>
/// <param name="Thread">The thread, new in this save when the venue writes first.</param>
/// <param name="Message">The venue's <see cref="MessageKind.Text"/>, with who wrote it.</param>
/// <param name="OccurredAtUtc">When it was written.</param>
public sealed record VenueMessageWritten(Conversation Thread, ConversationMessage Message, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Somebody at the venue asked the booking's guest "are you coming?". The guest is told, and the History says who asked.
/// </summary>
/// <param name="Thread">A booking's thread, new in this save when nobody had written yet.</param>
/// <param name="Ask">The venue's <see cref="MessageKind.AskComing"/>, with who asked.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record VenueAskedComing(Conversation Thread, ConversationMessage Ask, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>
/// The guest answered "are you coming?" - on my way, running late or not coming, from the app, the manage link or the
/// notification - and the booking changed: the answer and the system line after it. The floor hears of it (the
/// <c>guestMessages</c> staff push, for the answer).
/// </summary>
/// <param name="Thread">The booking's thread, new in this save when the answer opened it.</param>
/// <param name="Answer">The guest's <see cref="MessageKind.Answer"/>.</param>
/// <param name="Status">The <see cref="MessageKind.Status"/> line after it.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record GuestAnswered(
    Conversation Thread, ConversationMessage Answer, ConversationMessage Status, DateTime OccurredAtUtc) : IDomainEvent;
