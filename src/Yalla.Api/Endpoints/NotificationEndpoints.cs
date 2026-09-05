using System.ComponentModel.DataAnnotations;
using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Authorization;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Domain.Identity;

namespace Yalla.Api.Endpoints;

/// <summary>Body of <c>POST /api/diner/devices</c>.</summary>
/// <param name="PushToken">The Expo push token this phone reported.</param>
/// <param name="Platform">1 iOS, 2 Android.</param>
/// <param name="Locale">
/// BCP-47. <b>The person's language, not the venue's</b> - a Russian-speaking regular at an
/// Armenian venue is written to in Russian. Anything unrecognised falls back to Armenian.
/// </param>
public sealed record RegisterDeviceRequest(
    [Required][StringLength(256, MinimumLength = 8)] string PushToken,
    DevicePlatform Platform,
    [StringLength(16)] string Locale = "hy");

/// <summary>Registering a phone for push, and the platform's view of the outbox.</summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/diner/devices", RegisterAsync)
            .WithTags(EndpointConventions.DinerTag)
            .RequireAuthorization(YallaPolicies.VerifiedDiner)
            .WithName("registerDinerDevice")
            .WithSummary("Register this phone for notifications")
            .WithDescription(
                "Call it on every launch. **Idempotent on the push token** - a row per launch would "
                + "mean one diner accumulating a hundred devices and a hundred copies of every "
                + "message.\n\n"
                + "Re-registering a token that was revoked brings it back, because that is what a "
                + "reinstall looks like. A token that turns up under a different diner is moved: the "
                + "phone belongs to whoever is holding it, and the previous owner stops receiving "
                + "their bookings on it.")
            .Produces<object>(StatusCodes.Status200OK)
            .ProducesProblemDetails(StatusCodes.Status401Unauthorized, "Needs a verified diner.");

        app.MapGet("/api/platform/outbox", GetOutboxHealthAsync)
            .WithTags(EndpointConventions.AdminTag)
            .RequireAuthorization(YallaPolicies.PlatformAdminOnly)
            .WithName("getOutboxHealth")
            .WithSummary("Pending, failed and dead-lettered message counts")
            .WithDescription(
                "**The first place to look when notifications quietly stop working.** Without it the "
                + "symptom is silence, and silence is indistinguishable from nothing having happened.\n\n"
                + "`deadLettered` is the number that needs a person: those have been given up on and "
                + "will never be retried. Each carries its last error and the key that names what "
                + "caused it, so a dead reminder can be traced back to its booking.\n\n"
                + "A `sentLastDay` of zero on a live venue means the channel is broken, whatever the "
                + "other counts say.")
            .Produces<OutboxHealthView>()
            .ProducesProblemDetails(StatusCodes.Status403Forbidden, "Platform admin only.");

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterDeviceRequest request,
        IDinerDeviceService devices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deviceId = await devices.RegisterAsync(
            request.PushToken, request.Platform, request.Locale, cancellationToken);

        return Results.Ok(new { deviceId });
    }

    private static async Task<IResult> GetOutboxHealthAsync(
        IOutbox outbox,
        CancellationToken cancellationToken,
        int deadLetterLimit = 50) =>
        Results.Ok(await outbox.GetHealthAsync(deadLetterLimit, cancellationToken));
}
