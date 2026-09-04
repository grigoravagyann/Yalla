using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Yalla.Api.Errors;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;

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
        var mapped = ApiExceptionMapper.Map(new InvalidOperationException("This session is already closed."));

        Assert.Equal(StatusCodes.Status409Conflict, mapped.Status);
        Assert.Equal(ErrorCodes.ConflictingState, mapped.Code);
        Assert.Equal("This session is already closed.", mapped.Message);
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
