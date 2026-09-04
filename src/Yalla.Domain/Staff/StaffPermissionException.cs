using Yalla.Domain.Enums;

namespace Yalla.Domain.Staff;

/// <summary>
/// Thrown when the acting staff member's role does not permit the operation.
/// </summary>
/// <remarks>
/// Note what is <i>not</i> here: none of the table transitions. A waiter may perform every one of
/// them, including taking a table out of service. A broken chair is a Friday-night fact, and
/// routing it through a manager makes the floor state go stale exactly when accuracy matters
/// most. The manager-only operations are the ones that move money or change configuration.
/// </remarks>
public sealed class StaffPermissionException : Exception
{
    public StaffPermissionException(string operation, StaffRole? actualRole, StaffRole requiredRole)
        : base($"{operation} requires the {requiredRole} role"
               + (actualRole is null ? " and the caller has no staff role." : $"; the caller is a {actualRole}."))
    {
        Operation = operation;
        ActualRole = actualRole;
        RequiredRole = requiredRole;
    }

    public string Operation { get; }

    public StaffRole? ActualRole { get; }

    public StaffRole RequiredRole { get; }
}
