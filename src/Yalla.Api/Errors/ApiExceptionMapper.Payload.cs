using Yalla.Domain;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Takeaway;
using Yalla.Domain.Venues;

namespace Yalla.Api.Errors;

internal static partial class ApiExceptionMapper
{
    /// <summary>Refusals about the request itself: fields, items, binding, the domain's guards.</summary>
    private static MappedError? MapPayload(Exception exception) => exception switch
    {
        // A null where the domain requires an object - a Venue, a ReservationPolicy. Those are
        // built internally and never arrive over HTTP, so this is a bug in our code, not bad
        // input, and no caller can act on it. Must stay ABOVE ArgumentException, which it derives
        // from, or it would be answered as a 400 quoting an internal parameter name.
        ArgumentNullException => Fault(),

        // Dishes a pre-order cannot take, every one named with why, so the app can mark them all. Must stay ABOVE
        // ArgumentException, which it derives from.
        PreOrderItemsUnavailableException e => Refuse(
            ErrorCodes.PreOrderItemsUnavailable,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["items"] = e.Items.Select(i => new { menuItemId = i.MenuItemId, reason = (int)i.Reason }).ToList(),
            }),

        // Dishes a takeaway order cannot take, every one named with why. Must stay ABOVE ArgumentException.
        TakeawayItemsUnavailableException e => Refuse(
            ErrorCodes.TakeawayItemsUnavailable,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["items"] = e.Items.Select(i => new { menuItemId = i.MenuItemId, reason = (int)i.Reason }).ToList(),
            }),

        // A refusal that names its fields. 422 rather than 400: the request was understood and is
        // semantically wrong, which is what the console needs in order to put each message against
        // the input that caused it instead of at the top of the form.
        //
        // Must stay ABOVE ArgumentException, which it derives from, or a bounds refusal would go
        // back as a 400 with prose and nothing else - which is the whole gap being closed.
        FieldValidationException e => Refuse(
            ErrorCodes.ValidationFailed,
            e.Message,
            context: new Dictionary<string, object?>
            {
                // The first field, for a form that can only highlight one thing.
                ["field"] = e.Field,

                // And all of them, because a request that breaks six bounds reports six. A form
                // that surfaces one at a time makes an owner submit six times.
                ["fields"] = e.Violations
                    .Select(v => new
                    {
                        field = v.Field,
                        message = v.Message,
                        bound = v.Bound,
                        min = v.Min,
                        max = v.Max,
                        value = v.Value,
                    })
                    .ToArray(),
            },
            errors: e.AsErrorMap()),

        // The domain's own refusals. Guard and the entity constructors throw these with messages
        // written to be read, so they are safe and useful to pass back.
        // A floor plan that cannot be applied, with the offending tables named so the editor can
        // highlight them. Must stay ABOVE ArgumentException, which it derives from.
        FloorPlanInvalidException e => Refuse(
            ErrorCodes.FloorPlanInvalid,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["errors"] = e.Errors,
                ["duplicateLabels"] = e.DuplicateLabels,
            }),

        // A query string or route value the binder could not turn into what the handler asked for -
        // a missing required parameter, a date that is not a date. Answered as 400 rather than
        // falling through to the 500 catch-all: the request never reached any of our code, and
        // reporting a client's typo as a server fault sends somebody looking in the wrong place
        // and buries a real error in the log behind it.
        //
        // The message is the binder's own and names the parameter, which is what the caller needs.
        BadHttpRequestException e => Refuse(ErrorCodes.InvalidRequest, e.Message),

        // A report range nobody meant to ask for. Must stay ABOVE ArgumentOutOfRangeException,
        // which it derives from, or it would come back as a generic bad request and the client
        // would have no way to say what the limit is.
        Yalla.Application.Reports.ReportRangeTooLongException e => Refuse(
            ErrorCodes.ReportRangeTooLong,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["field"] = "range",
                ["requestedDays"] = e.RequestedDays,
                ["maxDays"] = e.MaxDays,
            }),

        // Everything else the domain guards refuse. These carry a ParamName that is already the
        // field's name - Guard and every entity constructor pass nameof(theParameter) - so the
        // sweep is to stop throwing it away, not to invent it. See ArgumentContext.
        ArgumentOutOfRangeException e => Refuse(
            ErrorCodes.InvalidRequest,
            e.Message,
            context: ArgumentContext(e.ParamName, e.ActualValue)),

        ArgumentException e => Refuse(
            ErrorCodes.InvalidRequest,
            e.Message,
            context: ArgumentContext(e.ParamName, actualValue: null)),

        _ => null,
    };

    /// <summary>
    /// Parameter names that are the request rather than a field of it, and so name nothing useful.
    /// </summary>
    /// <remarks>
    /// A guard that throws <c>nameof(command)</c> is saying "something about this payload is
    /// wrong", which is a form-level message and correct as one. Putting <c>field: "command"</c>
    /// on it would send a console looking for an input called "command" and find none, which is
    /// worse than saying nothing - so these are dropped and the refusal stays form-level.
    /// </remarks>
    private static readonly HashSet<string> NotFieldNames =
        new(StringComparer.Ordinal) { "command", "request", "blocks", "value", "obj", "source" };

    /// <summary>
    /// Turns an argument refusal's <c>ParamName</c> into the field the client should highlight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The domain has always named its fields - every <c>Guard</c> call and every entity
    /// constructor passes <c>nameof</c> of a camelCase parameter, which is exactly the casing the
    /// OpenAPI schema uses - and the mapper simply dropped it, so an owner typing a latitude of 200
    /// got a sentence and no way to tell which input it was about. This is the sweep: one place,
    /// and every endpoint whose refusals come out of a guard gains <c>context.field</c> at once.
    /// </para>
    /// <para>
    /// A name that is not camelCase is a label rather than a field - the reservation policy bounds
    /// used to throw "Turn time" - and is dropped rather than passed on, because a form keyed on
    /// English server prose is the thing this whole change exists to delete.
    /// </para>
    /// </remarks>
    private static Dictionary<string, object?>? ArgumentContext(string? paramName, object? actualValue)
    {
        if (string.IsNullOrEmpty(paramName)
            || NotFieldNames.Contains(paramName)
            || !char.IsLower(paramName[0]))
        {
            return null;
        }

        var context = new Dictionary<string, object?> { ["field"] = paramName };

        // Only when there is one. DefaultIgnoreCondition does not reach inside a dictionary, so an
        // unconditional entry would put a bare "value": null on the wire to trip a client up.
        if (actualValue is not null)
        {
            context["value"] = actualValue;
        }

        return context;
    }
}
