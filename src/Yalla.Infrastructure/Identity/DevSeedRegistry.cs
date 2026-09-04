namespace Yalla.Infrastructure.Identity;

/// <summary>
/// The ids the development seeder created (or found), published for the dev actor stub to use.
/// </summary>
/// <remarks>
/// Entity ids are UUIDv7 values generated inside the constructors, so they cannot be written into
/// configuration ahead of time. Rather than weaken the entities to accept caller-supplied keys
/// just for seeding, the seeder records what it made here and
/// <see cref="DevCurrentActor"/> reads it. Singleton, written once at startup.
/// </remarks>
public sealed class DevSeedRegistry
{
    public Guid? VenueId { get; private set; }

    public Guid? BranchId { get; private set; }

    public Guid? WaiterId { get; private set; }

    public Guid? ManagerId { get; private set; }

    public bool IsSeeded => BranchId is not null;

    public void Publish(Guid venueId, Guid branchId, Guid waiterId, Guid managerId)
    {
        VenueId = venueId;
        BranchId = branchId;
        WaiterId = waiterId;
        ManagerId = managerId;
    }
}
