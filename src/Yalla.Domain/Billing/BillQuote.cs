using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Billing;

/// <summary>
/// What a request for the bill asks the waiter to bring: the amount, and the tip on top of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Priced by the server, from the same <see cref="TabBill"/> the shares are.</b> The diner's
/// phone shows a number on the pay sheet and the waiter walks over with one; if each worked it out
/// for itself they would disagree by a dram the first time a discount and an odd service charge met,
/// and the person holding the notes is the one who finds out. So the request carries the figure, and
/// the figure comes out of the one arithmetic the rest of the bill uses.
/// </para>
/// <para>
/// <b>The tip stays outside the amount</b>, exactly as <c>Payment.TipAmd</c> stays outside the
/// balance: <see cref="AmountAmd"/> is what the scope leaves to pay with the service charge in and the
/// tip out, and <see cref="TipAmd"/> is beside it. It is a percentage of the <i>items</i> the scope
/// covers - the subtotal before service - because a tip on the service charge is a tip on a tip.
/// </para>
/// </remarks>
/// <param name="Scope">The whole table's balance, or the asker's own share.</param>
/// <param name="AmountAmd">
/// What the scope leaves to pay, in whole dram, service charge included and tip excluded. Always
/// greater than zero: a request for nothing is refused rather than priced.
/// </param>
/// <param name="TipPercent">The tip the diner chose, 0 to <see cref="MaxTipPercent"/>; null when none was asked.</param>
/// <param name="TipAmd">The tip in whole dram. Null exactly when <paramref name="TipPercent"/> is.</param>
public sealed record BillQuote(BillScope Scope, long AmountAmd, int? TipPercent, long? TipAmd)
{
    /// <summary>The largest tip the pay sheet offers, in percent. A bigger one is a typo, not generosity.</summary>
    public const int MaxTipPercent = 25;

    /// <summary>The refusal for a scope with nothing left in it. Wire text; the pay sheet shows it.</summary>
    public const string NothingLeftToPay = "There is nothing left to pay on this part of the bill.";

    /// <summary>
    /// Prices a request for the bill against the tab as it stands.
    /// </summary>
    /// <param name="bill">The tab, computed by <see cref="TabBilling.Compute"/> from its lines.</param>
    /// <param name="requesterParticipantId">Who is asking. Their share is what <see cref="BillScope.MyShare"/> means.</param>
    /// <param name="scope">Whose part of the bill.</param>
    /// <param name="tipPercent">The tip, 0 to <see cref="MaxTipPercent"/>, or null for none.</param>
    /// <remarks>
    /// <list type="bullet">
    /// <item>
    /// <see cref="BillScope.WholeBill"/>: the tab's remaining balance; the tip is on the tab subtotal.
    /// </item>
    /// <item>
    /// <see cref="BillScope.MyShare"/>: what the asker still owes of their own share, the same
    /// <c>shareAmd</c> less <c>paidAmd</c> the shares view reports - and never more than the tab
    /// still owes, because a waiter's payment taken against the table rather than a person lowers
    /// the balance without lowering anybody's share. The tip is on their part of the subtotal,
    /// <c>personalAmd</c>.
    /// </item>
    /// </list>
    /// </remarks>
    /// <exception cref="FieldValidationException">
    /// The scope leaves nothing to pay - field <c>billScope</c>.
    /// </exception>
    public static BillQuote For(TabBill bill, Guid requesterParticipantId, BillScope scope, int? tipPercent)
    {
        ArgumentNullException.ThrowIfNull(bill);

        if (tipPercent is < 0 or > MaxTipPercent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tipPercent), tipPercent, $"A tip is between 0 and {MaxTipPercent} percent.");
        }

        var (amountAmd, itemsAmd) = scope switch
        {
            BillScope.WholeBill => (bill.RemainingAmd, bill.SubtotalAmd),
            BillScope.MyShare => bill.Shares.FirstOrDefault(s => s.ParticipantId == requesterParticipantId)
                is { } share
                ? (Math.Min(share.RemainingAmd, bill.RemainingAmd), share.PersonalAmd)
                : (0L, 0L),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Not a defined bill scope."),
        };

        if (amountAmd <= 0L)
        {
            throw new FieldValidationException(new FieldViolation(
                "billScope", NothingLeftToPay, FieldBounds.Conflict, Value: (int)scope));
        }

        return new BillQuote(
            scope,
            amountAmd,
            tipPercent,
            tipPercent is { } percent ? Money.PercentOf(itemsAmd, percent) : null);
    }
}
