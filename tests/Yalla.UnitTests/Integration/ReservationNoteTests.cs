using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// K9: the note a diner writes when booking reaches the venue, and every other view of the booking.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ReservationNoteTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_note_reaches_the_venues_approval_queue_the_diners_bookings_and_the_manage_link()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);

            // The branch approves every booking by hand, so this one lands in the approval queue.
            await ReviewTestData.SavePolicyAsync(db, branch.BranchId, factory.Clock.UtcNow, autoConfirm: false);
        }

        using var diner = factory.CreateClientWithToken((await ReviewIntegrityTests.SignInDinerAsync(factory)).AccessToken);

        const string note = "Window table, please";
        Assert.Equal(20, note.Length);

        var created = await diner.PostAsJsonAsync(
            "/api/reservations",
            ReviewTestData.Booking(factory, branch, branch.FirstTableId, ReservationChannel.Unknown, $"  {note} "));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var booking = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = booking.GetProperty("id").GetGuid();
        Assert.Equal(note, booking.GetProperty("note").GetString());
        Assert.Equal((int)ReservationStatus.PendingApproval, booking.GetProperty("status").GetInt32());

        // Where the venue reads it: the approval queue.
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        var queue = await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{branch.BranchId}/reservations?status=1");
        Assert.Equal(note, queue.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id).GetProperty("note").GetString());

        // The diner's own list.
        var mine = await diner.GetFromJsonAsync<JsonElement>("/api/reservations/mine");
        Assert.Equal(
            note,
            mine.GetProperty("upcoming").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id).GetProperty("note").GetString());

        // And the manage link.
        using var anyone = factory.CreateClient();
        var link = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/bookings/{booking.GetProperty("manageToken").GetString()}");
        Assert.Equal(note, link.GetProperty("note").GetString());
    }

    [SkippableFact]
    public async Task A_blank_note_is_no_note_and_one_over_500_characters_is_refused_naming_note()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        var (token, dinerUserId) = await ReviewIntegrityTests.SignInDinerAsync(factory);
        using var diner = factory.CreateClientWithToken(token);

        var blank = await diner.PostAsJsonAsync(
            "/api/reservations", ReviewTestData.Booking(factory, branch, branch.TableIds[0], ReservationChannel.Unknown, "   "));

        Assert.Equal(HttpStatusCode.Created, blank.StatusCode);
        Assert.False((await blank.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("note", out _), "a blank note is no note");

        var tooLong = await ReviewIntegrityTests.AssertProblemAsync(
            await diner.PostAsJsonAsync(
                "/api/reservations",
                ReviewTestData.Booking(factory, branch, branch.TableIds[1], ReservationChannel.Unknown, new string('n', 501))),
            HttpStatusCode.UnprocessableEntity,
            "validation-failed",
            "note");

        var violation = Assert.Single(tooLong.GetProperty("context").GetProperty("fields").EnumerateArray());
        Assert.Equal("max", violation.GetProperty("bound").GetString());
        Assert.Equal(500, violation.GetProperty("max").GetInt32());

        await using var verify = fixture.CreateContext(factory.Clock);
        Assert.Equal(1, await verify.Reservations.CountAsync(r => r.DinerUserId == dinerUserId));
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
