using Yalla.Domain.Enums;

namespace Yalla.Application.Abstractions;

/// <summary>
/// Who is making the current request.
/// </summary>
/// <remarks>
/// <para>
/// Every service that changes state takes this by injection and writes it into the audit log, so
/// there is no code path that mutates a table without recording who did it.
/// </para>
/// <para>
/// <b>There are four identity types and this reports all of them, which it did not always do.</b>
/// A tab participant has a <see cref="ParticipantId"/> and no <see cref="DinerUserId"/> - that is
/// the whole point of Prompt 3, a phone at a table needs no account - and until Prompt 11 this
/// interface had nowhere to put one. Ordering read <see cref="DinerUserId"/> to find the
/// participant, which is null for every real participant token, so the feature the product is
/// built around had never once worked outside a test.
/// </para>
/// <para>
/// The shape each identity produces is pinned by a contract test that every implementation and
/// every test double must pass - see <c>CurrentActorContract</c>. A double that reports a shape
/// production cannot produce is worse than no double, because the suite then proves the opposite
/// of what it claims.
/// </para>
/// </remarks>
public interface ICurrentActor
{
    /// <summary>Whether a diner, a staff member, or the system itself is acting.</summary>
    /// <remarks>
    /// A tab participant is <see cref="ActorType.Diner"/>, like an account-holding diner. They
    /// differ in which id is populated, not in what kind of person they are.
    /// </remarks>
    ActorType Type { get; }

    /// <summary>The acting staff member, when <see cref="Type"/> is <see cref="ActorType.Staff"/>.</summary>
    Guid? StaffMemberId { get; }

    /// <summary>
    /// The acting diner's <b>account</b>, when there is one.
    /// </summary>
    /// <remarks>
    /// <b>Null for a tab participant</b>, always, by design: a participant is a row on a tab, not a
    /// user, and most people who order will never have an account. Anything that requires this is
    /// requiring an account, which is a real requirement for booking - a no-show has to be counted
    /// against somebody - and a bug on any path a participant can reach.
    /// </remarks>
    Guid? DinerUserId { get; }

    /// <summary>
    /// The acting <c>TabParticipant</c> row, when the caller holds a participant token.
    /// </summary>
    /// <remarks>
    /// This is what identifies a phone at a table. It is read from the token and never from a
    /// request body, so one phone cannot order in another's name.
    /// </remarks>
    Guid? ParticipantId { get; }

    /// <summary>The acting staff member's role, used for the manager-only operations.</summary>
    StaffRole? Role { get; }
}
