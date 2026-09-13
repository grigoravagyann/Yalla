namespace Yalla.Domain.Identity;

/// <summary>
/// A diner account whose phone number has never been proved by a one-time code tried to do
/// something that lands on that number.
/// </summary>
/// <remarks>
/// <para>
/// Registration stores the number as typed. Until a code to it comes back, nothing says the person
/// holding the account owns it - and a booking sends its reminders to that number and counts its
/// no-show against it. Letting an unproved number book is letting anyone put a stranger's phone on
/// a booking they never made.
/// </para>
/// <para>
/// Its own type rather than an <see cref="UnauthorizedAccessException"/>, because the answer is
/// not "you may not" but "prove your number first", and the app needs a stable code to offer the
/// verify link instead of a dead end. Raised by the services from the stored row, never from the
/// token: the token says who, the row says whether the number is real.
/// </para>
/// </remarks>
public sealed class PhoneNotVerifiedException : Exception
{
    public PhoneNotVerifiedException(Guid dinerUserId, string operation)
        : base($"{operation} needs a verified phone number. Verify your number with a code first.")
    {
        DinerUserId = dinerUserId;
        Operation = operation;
    }

    /// <summary>The account that was refused. For the log; never put on the wire.</summary>
    public Guid DinerUserId { get; }

    /// <summary>What was being attempted, in words.</summary>
    public string Operation { get; }
}
