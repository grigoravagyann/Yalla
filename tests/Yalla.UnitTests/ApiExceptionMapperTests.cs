using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Yalla.Api.Errors;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

/// <summary>
/// The mapper is the seam where the domain's refusals become HTTP answers, so these tests pin
/// the status code and slug each kind of failure produces.
/// </summary>
public class ApiExceptionMapperTests
{
    [Fact]
    public void A_domain_invariant_violation_becomes_a_400_with_the_reason_intact()
    {
        // The real refusal, thrown by the real entity: CanPay implies CanSeeTableTotal.
        var exception = Record.Exception(() => new TabParticipant(
            Guid.CreateVersion7(),
            "Ani",
            "device-1",
            ParticipantRole.Guest,
            ParticipantStatus.Approved,
            new DateTime(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc),
            canOrder: true,
            canSeeTableTotal: false,
            canPay: true));

        Assert.NotNull(exception);

        var mapped = ApiExceptionMapper.Map(exception);

        Assert.Equal(StatusCodes.Status400BadRequest, mapped.Status);
        Assert.Equal(ErrorCodes.InvalidRequest, mapped.Code);
        Assert.Contains("allowed to see the table total", mapped.Message);
        Assert.False(mapped.LogAsError);
    }

    [Fact]
    public void An_out_of_range_argument_becomes_a_400()
    {
        var mapped = ApiExceptionMapper.Map(new ArgumentOutOfRangeException("partySize", 0, "Too small."));

        Assert.Equal(StatusCodes.Status400BadRequest, mapped.Status);
        Assert.Equal(ErrorCodes.InvalidRequest, mapped.Code);
    }

    [Fact]
    public void An_illegal_state_transition_becomes_a_409()
    {
        // The real refusal, thrown by the real entity: a session closes once.
        var session = TableSession.SeatWalkIn(
            branchId: Guid.CreateVersion7(),
            diningTableId: Guid.CreateVersion7(),
            partySize: 2,
            seatedAtUtc: new DateTime(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc));

        session.Close(new DateTime(2026, 9, 4, 19, 30, 0, DateTimeKind.Utc));

        var exception = Record.Exception(() =>
            session.Close(new DateTime(2026, 9, 4, 20, 0, 0, DateTimeKind.Utc)));

        Assert.IsType<DomainStateException>(exception);

        var mapped = ApiExceptionMapper.Map(exception);

        Assert.Equal(StatusCodes.Status409Conflict, mapped.Status);
        Assert.Equal(ErrorCodes.ConflictingState, mapped.Code);
        Assert.Equal("This session is already closed.", mapped.Message);
        Assert.False(mapped.LogAsError);
    }

    /// <summary>
    /// The regression <see cref="DomainStateException"/> exists to prevent.
    /// </summary>
    /// <remarks>
    /// A bare <see cref="InvalidOperationException"/> is what .NET raises for an unresolved
    /// service, <c>First()</c> on an empty sequence, or EF's transient-failure wrapper. The mapper
    /// used to answer all of those with a 409 telling the caller they had a conflict, echo the
    /// internal message back, and log none of it - so genuine faults were invisible in the log and
    /// indistinguishable from two waiters racing for a table.
    /// </remarks>
    [Fact]
    public void A_framework_failure_is_a_logged_500_and_not_a_409()
    {
        const string internals =
            "Unable to resolve service for type 'Yalla.Application.Abstractions.ICurrentActor' "
            + "while attempting to activate 'Yalla.Infrastructure.Services.TableStateService'.";

        var mapped = ApiExceptionMapper.Map(new InvalidOperationException(internals));

        Assert.Equal(StatusCodes.Status500InternalServerError, mapped.Status);
        Assert.Equal(ErrorCodes.InternalError, mapped.Code);
        Assert.DoesNotContain("ICurrentActor", mapped.Message);
        Assert.True(mapped.LogAsError);
    }

    [Fact]
    public void An_empty_sequence_is_a_logged_500_and_not_a_409()
    {
        // Exactly what First() throws, and a bug every time it reaches the pipeline.
        var exception = Record.Exception(() => Array.Empty<int>().First());

        var mapped = ApiExceptionMapper.Map(exception!);

        Assert.Equal(StatusCodes.Status500InternalServerError, mapped.Status);
        Assert.True(mapped.LogAsError);
    }

    /// <summary>
    /// A null where an object is required is our bug, not bad input: a Venue or ReservationPolicy
    /// is built internally and cannot arrive over HTTP. It must not become a 400 quoting an
    /// internal parameter name.
    /// </summary>
    [Fact]
    public void A_null_argument_is_a_logged_500_and_not_a_400()
    {
        var mapped = ApiExceptionMapper.Map(new ArgumentNullException("reservationPolicy"));

        Assert.Equal(StatusCodes.Status500InternalServerError, mapped.Status);
        Assert.Equal(ErrorCodes.InternalError, mapped.Code);
        Assert.DoesNotContain("reservationPolicy", mapped.Message);
        Assert.True(mapped.LogAsError);
    }

    /// <summary>
    /// Guard's refusals stay 400s - it throws ArgumentException deliberately, with messages
    /// written to be read, and ArgumentNullException sits above it in the switch.
    /// </summary>
    [Fact]
    public void A_guard_refusal_still_becomes_a_400_with_its_message()
    {
        var mapped = ApiExceptionMapper.Map(
            new ArgumentException("Identifier must not be empty.", "branchId"));

        Assert.Equal(StatusCodes.Status400BadRequest, mapped.Status);
        Assert.Equal(ErrorCodes.InvalidRequest, mapped.Code);
        Assert.Contains("must not be empty", mapped.Message);
        Assert.False(mapped.LogAsError);
    }

    /// <summary>
    /// An impossible transition stays a 422 and keeps its details, even though the type now
    /// derives from <see cref="DomainStateException"/> - which the 409 arm also matches, so the
    /// order of the two arms is load-bearing.
    /// </summary>
    [Fact]
    public void An_impossible_transition_is_still_a_422_and_not_a_409()
    {
        var mapped = ApiExceptionMapper.Map(new InvalidTableTransitionException(
            Guid.CreateVersion7(), "7", TableStatus.Occupied, TableStatus.OutOfService));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, mapped.Status);
        Assert.Equal(ErrorCodes.InvalidTableTransition, mapped.Code);
        Assert.NotNull(mapped.Context);
        Assert.Equal("7", mapped.Context!["tableLabel"]);

        // Enum facts travel as their integer values, matching the schema and every other
        // response. A client comparing this against its generated TableStatus must not have to
        // know that this one place spelled it out in English.
        Assert.Equal((int)TableStatus.Occupied, mapped.Context["fromStatus"]);
        Assert.Equal((int)TableStatus.OutOfService, mapped.Context["attemptedToStatus"]);
    }

    [Fact]
    public void A_lost_concurrency_race_becomes_a_409_that_is_not_logged_as_an_error()
    {
        var mapped = ApiExceptionMapper.Map(new DbUpdateConcurrencyException());

        Assert.Equal(StatusCodes.Status409Conflict, mapped.Status);
        Assert.Equal(ErrorCodes.ConcurrentUpdate, mapped.Code);

        // Two waiters tapping the same table is normal operation, not a fault.
        Assert.False(mapped.LogAsError);
    }

    [Fact]
    public void An_unexpected_failure_becomes_a_500_that_leaks_nothing()
    {
        const string secret = "Login failed for user 'sa'. Server=db-prod-01";

        var mapped = ApiExceptionMapper.Map(new InvalidCastException(secret));

        Assert.Equal(StatusCodes.Status500InternalServerError, mapped.Status);
        Assert.Equal(ErrorCodes.InternalError, mapped.Code);
        Assert.DoesNotContain(secret, mapped.Message);
        Assert.True(mapped.LogAsError);
    }
}
