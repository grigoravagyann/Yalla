using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Application.Reservations;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The declared request contract, enforced - and the three places it deliberately is not.
/// </summary>
/// <remarks>
/// <para>
/// Forty <c>[Required]</c>, <c>[StringLength]</c>, <c>[Range]</c> and <c>[EmailAddress]</c>
/// declarations reached the OpenAPI schema and nothing enforced them. A client's gateway schema
/// check found that before the server did.
/// </para>
/// <para>
/// The cost was not a missing refusal but a refusal about the wrong thing: a missing required
/// string bound to null and travelled down to a lookup, which then answered about the row it could
/// not find. A diner opening a tab with no <c>qrToken</c> was told their QR code was not a table in
/// service.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class RequestValidationTests(SqlServerFixture fixture)
{
    /// <summary>
    /// A missing required string is a 422 that names it, not a 404 about a row nobody asked for.
    /// </summary>
    [SkippableFact]
    public async Task A_missing_required_field_names_itself_instead_of_answering_about_a_lookup()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            "/api/tabs/open", new { clientCommandId = Guid.CreateVersion7() });

        // Was 404 "That QR code does not belong to a table in service." - an answer about a QR code
        // the caller never sent.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("validation-failed", body.GetProperty("code").GetString());
        Assert.Equal("qrToken", body.GetProperty("context").GetProperty("field").GetString());

        // Both of them, not the first. The whole reason this is a 422 and not a 400.
        var fields = body.GetProperty("context").GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("field").GetString())
            .ToList();

        Assert.Contains("qrToken", fields);
        Assert.Contains("deviceId", fields);

        // The same document shape every other refusal uses - one error contract, not two.
        Assert.True(body.TryGetProperty("errors", out var errors));
        Assert.True(errors.TryGetProperty("qrToken", out _));
    }

    /// <summary>The join route had the same defect, with a different misleading answer.</summary>
    [SkippableFact]
    public async Task A_missing_join_token_is_a_422_and_not_an_expired_invitation()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/tabs/join", new { deviceId = "phone-1" });

        // Was 401 "That invitation is no longer valid. Ask the host for a new one." - which sends a
        // guest back to a host who did nothing wrong.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        Assert.Equal(
            "joinToken",
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("context").GetProperty("field").GetString());
    }

    /// <summary>
    /// A bound that is not <c>[Required]</c> is enforced too - it was equally decorative.
    /// </summary>
    [SkippableFact]
    public async Task A_value_outside_its_declared_range_is_refused()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            "/api/tabs/open",
            new
            {
                qrToken = "whatever",
                deviceId = "phone-1",
                clientCommandId = Guid.CreateVersion7(),
                partySize = 500,
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "partySize",
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("context").GetProperty("field").GetString());
    }

    /// <summary>
    /// <c>clientCommandId</c> keeps its own 400. Pinned so the filter cannot quietly absorb it.
    /// </summary>
    /// <remarks>
    /// <c>[Required]</c> on a non-nullable <c>Guid</c> can never fire - a missing one binds
    /// <c>Guid.Empty</c>, not null, and <c>RequiredAttribute</c> only rejects null - so this is
    /// still <c>ClientCommandIdFilter</c> answering, with a message about idempotency that no
    /// schema attribute could carry. The attribute is not useless: it is what makes the schema say
    /// the field is required, which is true and is what a gateway check reads.
    /// </remarks>
    [SkippableFact]
    public async Task A_missing_client_command_id_keeps_its_own_400_and_its_own_message()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            "/api/tabs/open", new { qrToken = "whatever", deviceId = "phone-1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("invalid-request", body.GetProperty("code").GetString());
        // Its own sentence, about replays - not "the clientCommandId field is required", which is
        // all an attribute could have said.
        Assert.Contains(
            "reuse it when retrying",
            body.GetProperty("detail").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The sign-in flows are outside the filter, and their uniform refusal is unchanged.
    /// </summary>
    /// <remarks>
    /// A credential endpoint answers the same way whether the email is unknown, the password is
    /// wrong or the body is malformed. That uniformity is the anti-enumeration posture, not an
    /// oversight, and switching on a filter must not restructure it as a side effect. Pinned
    /// because it would be easy to "tidy" the auth group into the validated one later.
    /// </remarks>
    [SkippableFact]
    public async Task The_sign_in_flows_keep_their_uniform_refusal()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        // No email at all, and a malformed one, both still 401 rather than a 422 that says which.
        foreach (var body in new object[]
                 {
                     new { password = "whatever12" },
                     new { email = "not-an-email", password = "whatever12" },
                 })
        {
            var response = await anonymous.PostAsJsonAsync("/api/auth/venue/sign-in", body);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // And the diner's phone refusal keeps the 400 it already answered correctly, named by the
        // domain guard rather than by an attribute.
        var phone = await anonymous.PostAsJsonAsync("/api/auth/diner/request-code", new { });

        Assert.Equal(HttpStatusCode.BadRequest, phone.StatusCode);
        Assert.Equal(
            "phoneE164",
            (await phone.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("context").GetProperty("field").GetString());
    }

    /// <summary>
    /// A well-formed request is untouched - the filter must not invent problems.
    /// </summary>
    /// <remarks>
    /// The cheap half of a validation change, and the half worth pinning: 650 passing tests already
    /// say the happy paths work, but they would say that just as loudly if the filter were never
    /// reached at all.
    /// </remarks>
    [SkippableFact]
    public async Task A_well_formed_request_passes_through_the_filter_untouched()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        string qrToken;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var branch = await AuthTestData.CreateBranchAsync(db);

            qrToken = await db.DiningTables.AsNoTracking()
                .Where(t => t.BranchId == branch.BranchId)
                .Select(t => t.QrToken)
                .FirstAsync();
        }

        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            "/api/tabs/open",
            new
            {
                qrToken,
                deviceId = "phone-1",
                clientCommandId = Guid.CreateVersion7(),
                displayName = "Ani",
                partySize = 2,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A release with no <c>outcome</c> is refused, rather than silently recorded as the wrong one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This was a silent wrong write, not a bad message.</b> <c>ReleaseOutcome</c> has no zero
    /// member, so an absent <c>outcome</c> bound to <c>0</c>, missed the <c>== NoShow</c> branch and
    /// fell through to <c>CancelByVenue</c> - answering <b>200</b> the whole way. Per the enum's own
    /// documentation that is the outcome which <i>does not</i> count against the diner, while the
    /// one the waiter meant does. A real no-show was recorded as the venue's own cancellation and
    /// the rolling no-show count, which decides whether that diner's next booking needs approval,
    /// never moved.
    /// </para>
    /// <para>
    /// The same shape as the settlement-mode bug that started this: a client sends the wrong field
    /// name and the server picks a branch rather than refusing. That one at least refused; this one
    /// wrote.
    /// </para>
    /// <para>
    /// Fixed by making the parameter <c>ReleaseOutcome?</c> so <see cref="RequiredAttribute"/> has
    /// a null to reject - the filter cannot see a missing non-nullable enum, because binding
    /// already replaced it with a real value.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_release_without_an_outcome_is_refused_rather_than_recorded_as_the_wrong_one()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();

        AuthBranch branch;
        Guid tableId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            tableId = branch.FirstTableId;
        }

        // A booking, made the way a diner makes one.
        using var diner = factory.CreateClientWithToken(await SignInDinerAsync(factory));

        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc), zone);

        var created = await diner.PostAsJsonAsync("/api/reservations", new
        {
            branchId = branch.BranchId,
            tableId,
            date = DateOnly.FromDateTime(localNow).AddDays(2).ToString("yyyy-MM-dd"),
            time = "18:00",
            partySize = 2,
            guestName = "Ani",
            guestPhone = "+37411223344",
            clientCommandId = Guid.CreateVersion7(),
        });

        var reservationId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var waiter = factory.CreateClientWithToken(
            await StaffAuthTests.SignInWaiterAsync(factory, branch));

        // outcome deliberately absent.
        var released = await waiter.PostAsJsonAsync(
            $"/api/reservations/{reservationId}/release",
            new { clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, released.StatusCode);

        Assert.Equal(
            "outcome",
            (await released.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("context").GetProperty("field").GetString());

        // And nothing was written. Before, this returned 200 and stored CancelledByVenue.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.Reservations.AsNoTracking().FirstAsync(r => r.Id == reservationId);

            Assert.Equal(ReservationStatus.Confirmed, row.Status);
        }

        // Supplied properly, the same request records the outcome the waiter actually chose.
        var noShow = await waiter.PostAsJsonAsync(
            $"/api/reservations/{reservationId}/release",
            new { outcome = ReleaseOutcome.NoShow, clientCommandId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.OK, noShow.StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.Reservations.AsNoTracking().FirstAsync(r => r.Id == reservationId);

            Assert.Equal(ReservationStatus.NoShow, row.Status);
        }
    }

    private static async Task<string> SignInDinerAsync(YallaApiFactory factory)
    {
        using var client = factory.CreateClient();
        var phone = $"+3741{Random.Shared.Next(1_000_000, 9_999_999)}";

        var requested = await client.PostAsJsonAsync("/api/auth/diner/request-code", new { phoneE164 = phone });
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("developmentCode").GetString();

        var verified = await client.PostAsJsonAsync(
            "/api/auth/diner/verify-code", new { phoneE164 = phone, code });

        verified.EnsureSuccessStatusCode();

        return (await verified.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString()!;
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
