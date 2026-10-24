using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yalla.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Clears the decline reason a cancelled booking kept from a change of it that was declined earlier - only on the
    /// rows that can be told apart from a real decline. No schema change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A confirmed booking whose change was declined goes back to its slot carrying why the change was declined
    /// (<c>KeepConfirmedSlot</c>). Until this fix a later cancellation left that on, so a booking the venue let go - or
    /// the no-show block cancelled - read as declined: <c>declinedBecause</c> on the booking and its link, and
    /// <c>declined</c> on the message history. From now on every cancellation clears it.
    /// </para>
    /// <para>
    /// A real decline happens only to a waiting booking (<c>Reject</c>, <c>DeclineForNoAnswer</c>), and a waiting booking
    /// never has <c>ConfirmedAtUtc</c> - a move back to waiting clears it, and only an approval sets it. So, of the
    /// <c>CancelledByVenue</c> rows with a reason, these are certainly not declines: the no-show block's
    /// (<c>CancelledBecause</c> 1); one with <c>ConfirmedAtUtc</c>; one whose reason is nobody answering or the guest
    /// before it (2, 3, 4) but with a cancellation text - those declines never write one; and one whose text is the
    /// release's own default. <c>CancelledByDiner</c> is never a decline, so its reason goes whatever it is.
    /// </para>
    /// <para>
    /// What is left - a staff-declined change of a booking confirmed on the spot, then released with a reason staff
    /// typed - cannot be told from a staff decline with the same text, and is left as it is.
    /// </para>
    /// </remarks>
    public partial class ClearStaleChangeDeclineReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE dbo.Reservations SET DeclineReason = NULL
                WHERE DeclineReason IS NOT NULL
                  AND (Status = 6
                       OR (Status = 7
                           AND (CancelledBecause = 1
                                OR ConfirmedAtUtc IS NOT NULL
                                OR (DeclineReason IN (2, 3, 4) AND CancellationReason IS NOT NULL)
                                OR CancellationReason = N'released: the venue let the booking go')));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to put back: the reasons cleared described a change, not how the booking ended, and the older code
            // reads a cancelled booking with none exactly as it reads one it cancelled itself.
        }
    }
}
