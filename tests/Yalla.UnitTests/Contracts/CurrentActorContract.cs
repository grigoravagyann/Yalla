using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Contracts;

/// <summary>
/// One identity, as a request for an <see cref="ICurrentActor"/> that stands for it.
/// </summary>
/// <remarks>
/// Deliberately describes <i>who</i> rather than which fields to populate. Each implementation is
/// asked "give me an actor for this person" and answers however it does that - the production one
/// by minting a real token and reading its claims, a double by construction - and the contract then
/// asserts the same shape of all of them. If the case carried the expected field values, a double
/// could satisfy it by echoing them back.
/// </remarks>
/// <param name="Name">What this case is, for the test name in the runner.</param>
/// <param name="Principal">Which of the four identity types this token is.</param>
/// <param name="StaffMemberId">The staff member, for the two staff principal types.</param>
/// <param name="Role">Their role.</param>
/// <param name="DinerUserId">The diner account, for a verified diner.</param>
/// <param name="ParticipantId">The tab participant row, for a phone at a table.</param>
/// <param name="TabId">The one tab a participant token may touch.</param>
/// <param name="BranchId">The branch a token is scoped to.</param>
/// <param name="VenueId">The venue a staff token belongs to.</param>
public sealed record ActorIdentity(
    string Name,
    PrincipalType Principal,
    Guid? StaffMemberId = null,
    StaffRole? Role = null,
    Guid? DinerUserId = null,
    Guid? ParticipantId = null,
    Guid? TabId = null,
    Guid? BranchId = null,
    Guid? VenueId = null)
{
    public override string ToString() => Name;
}

/// <summary>
/// The shape every <see cref="ICurrentActor"/> must produce for each of the four identity types.
/// </summary>
/// <remarks>
/// <para>
/// <b>This suite exists because a test double implemented a contract the production actor does
/// not.</b> <c>TestActor.Participant</c> put the participant id into <c>DinerUserId</c>;
/// <c>ClaimsCurrentActor</c> returns null there for a participant token, correctly, because a tab
/// participant has no account. Ordering read <c>DinerUserId</c> to find the participant, so with
/// real auth it refused every diner - and 522 green tests said the flow worked, because every one
/// of them ran against the fiction.
/// </para>
/// <para>
/// That is not a bug in one double. It is a hole in how the suite is built: nothing anywhere
/// forced a double and its production counterpart to agree. So the assertions live here, once, and
/// every implementation and every double is a subclass. A double that cannot satisfy the real
/// contract now fails the build instead of quietly certifying a feature that has never run.
/// </para>
/// <para>
/// The rules being pinned, in the order they matter:
/// </para>
/// <list type="number">
/// <item><b>A tab participant has a <c>ParticipantId</c> and no <c>DinerUserId</c>.</b> This is the
/// one that was wrong. Most people who order will never have an account.</item>
/// <item><b>A verified diner has a <c>DinerUserId</c> and no <c>ParticipantId</c>.</b> An account
/// holder sitting at a table still presents a <i>participant</i> token on that tab, so the two ids
/// never both appear on one request.</item>
/// <item><b>Staff carry a <c>StaffMemberId</c> and a <c>Role</c> and neither diner id.</b></item>
/// <item><b>Nobody carries a field belonging to another identity type.</b> Asserted as an explicit
/// null on every case rather than only on the populated ones, because the failure being guarded
/// against is a field that is set when it should not be.</item>
/// </list>
/// </remarks>
public abstract class CurrentActorContract
{
    /// <summary>
    /// The four identities every implementation is asked to stand for.
    /// </summary>
    /// <remarks>
    /// Fixed ids rather than fresh ones per case, so a failure message names a value that appears
    /// in the case and the assertion both, and an implementation that returns "some guid" cannot
    /// pass by accident.
    /// </remarks>
    public static readonly Guid ParticipantRowId = new("11111111-1111-1111-1111-111111111111");

    public static readonly Guid DinerAccountId = new("22222222-2222-2222-2222-222222222222");

    public static readonly Guid WaiterId = new("33333333-3333-3333-3333-333333333333");

    public static readonly Guid OwnerId = new("44444444-4444-4444-4444-444444444444");

    public static readonly Guid TabId = new("55555555-5555-5555-5555-555555555555");

    public static readonly Guid BranchId = new("66666666-6666-6666-6666-666666666666");

    public static readonly Guid VenueId = new("77777777-7777-7777-7777-777777777777");

    /// <summary>A phone at a table. No account, and none is ever needed.</summary>
    public static readonly ActorIdentity TabParticipant = new(
        "tab participant",
        PrincipalType.TabParticipant,
        ParticipantId: ParticipantRowId,
        TabId: TabId,
        BranchId: BranchId);

    /// <summary>Somebody who verified a phone number, so a no-show can be counted against them.</summary>
    public static readonly ActorIdentity VerifiedDiner = new(
        "verified diner",
        PrincipalType.Diner,
        DinerUserId: DinerAccountId);

    /// <summary>A waiter on an enrolled tablet.</summary>
    public static readonly ActorIdentity StaffSession = new(
        "staff session",
        PrincipalType.StaffSession,
        StaffMemberId: WaiterId,
        Role: StaffRole.Waiter,
        BranchId: BranchId,
        VenueId: VenueId);

    /// <summary>An owner in the admin panel. Venue-wide, so no branch claim.</summary>
    public static readonly ActorIdentity VenueUser = new(
        "venue user (owner)",
        PrincipalType.VenueUser,
        StaffMemberId: OwnerId,
        Role: StaffRole.Owner,
        VenueId: VenueId);

    public static TheoryData<ActorIdentity> Identities() =>
        new() { TabParticipant, VerifiedDiner, StaffSession, VenueUser };

    /// <summary>
    /// Produces an actor standing for this identity, however this implementation does that.
    /// </summary>
    protected abstract ICurrentActor ActorFor(ActorIdentity identity);

    [Theory]
    [MemberData(nameof(Identities))]
    public void The_actor_reports_the_expected_type(ActorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var expected = identity.Principal switch
        {
            PrincipalType.TabParticipant or PrincipalType.Diner => ActorType.Diner,
            _ => ActorType.Staff,
        };

        Assert.Equal(expected, ActorFor(identity).Type);
    }

    /// <summary>
    /// <b>The assertion the whole suite exists for.</b> A tab participant is identified by their
    /// participant row and never by an account, because they have not got one.
    /// </summary>
    [Fact]
    public void A_tab_participant_has_a_participant_id_and_no_diner_account()
    {
        var actor = ActorFor(TabParticipant);

        Assert.Equal(ActorType.Diner, actor.Type);
        Assert.Equal(ParticipantRowId, actor.ParticipantId);

        // The line that was wrong. Ordering resolved the participant from here, so a double that
        // populates it hides the fact that production never can.
        Assert.Null(actor.DinerUserId);

        Assert.Null(actor.StaffMemberId);
        Assert.Null(actor.Role);
    }

    [Fact]
    public void A_verified_diner_has_an_account_and_no_participant_row()
    {
        var actor = ActorFor(VerifiedDiner);

        Assert.Equal(ActorType.Diner, actor.Type);
        Assert.Equal(DinerAccountId, actor.DinerUserId);

        // An account holder sitting at a table presents a participant token on that tab, so the
        // two ids never appear on one request. A diner token addresses bookings, not tabs.
        Assert.Null(actor.ParticipantId);

        Assert.Null(actor.StaffMemberId);
        Assert.Null(actor.Role);
    }

    [Theory]
    [MemberData(nameof(StaffIdentities))]
    public void Staff_carry_a_staff_member_and_a_role_and_neither_diner_id(ActorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var actor = ActorFor(identity);

        Assert.Equal(ActorType.Staff, actor.Type);
        Assert.Equal(identity.StaffMemberId, actor.StaffMemberId);
        Assert.Equal(identity.Role, actor.Role);
        Assert.Null(actor.DinerUserId);
        Assert.Null(actor.ParticipantId);
    }

    public static TheoryData<ActorIdentity> StaffIdentities() => new() { StaffSession, VenueUser };

    /// <summary>
    /// Reading an actor twice gives the same answer.
    /// </summary>
    /// <remarks>
    /// Every property on this interface is a getter that some implementations compute on each
    /// access - <c>ClaimsCurrentActor</c> re-reads the principal every time - so a service that
    /// reads <c>ParticipantId</c> twice must not be able to get two different people.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Identities))]
    public void The_actor_is_stable_across_reads(ActorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var actor = ActorFor(identity);

        Assert.Equal(actor.Type, actor.Type);
        Assert.Equal(actor.StaffMemberId, actor.StaffMemberId);
        Assert.Equal(actor.DinerUserId, actor.DinerUserId);
        Assert.Equal(actor.ParticipantId, actor.ParticipantId);
        Assert.Equal(actor.Role, actor.Role);
    }
}
