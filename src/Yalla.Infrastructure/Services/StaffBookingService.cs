using Yalla.Application.Reservations;
using Yalla.Domain.Enums;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Staff-taken bookings - the contract's stub: the routes, records and problem codes are published first so the
/// clients generate their types; the logic lands in the next commits on the same branch (AGENTS.md, contract-first).
/// </summary>
internal sealed class StaffBookingService : IStaffBookingService
{
    public Task<ReservationView> CreateAsync(StaffCreateReservationCommand command, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Staff bookings: the booking itself lands in the next commit.");

    public Task<StaffDaySlots> GetDaySlotsAsync(StaffDaySlotsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(new StaffDaySlots
        {
            BranchId = request.BranchId,
            Date = request.LocalDate,
            PartySize = request.PartySize,
            BookingMode = BookingMode.ChooseTable,
            StepMinutes = DaySlotStep.Minutes,
            Slots = [],
        });
    }

    public Task<IReadOnlyList<BookingToCall>> ListToCallAsync(Guid branchId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BookingToCall>>([]);

    public Task<ReservationView> MarkGuestComingAsync(MarkGuestComingCommand command, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Staff bookings: the guest's answer lands in the next commit.");
}
