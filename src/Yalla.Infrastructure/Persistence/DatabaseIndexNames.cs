namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// Names of the indexes the application reasons about by name.
/// </summary>
/// <remarks>
/// Two of these are load-bearing: the service catches their unique violations and turns them into
/// meaningful answers rather than a 500. Keeping the names here means the configuration that
/// creates them and the handler that catches them cannot drift.
/// </remarks>
internal static class DatabaseIndexNames
{
    /// <summary>
    /// Unique, filtered on open sessions: at most one party sitting at a table. A violation means
    /// somebody else seated this table first.
    /// </summary>
    public const string OpenSessionPerTable = "UX_TableSessions_OpenPerTable";

    /// <summary>
    /// Unique on the caller's command id. A violation means the same offline command is being
    /// replayed concurrently with its original.
    /// </summary>
    public const string TableStateChangeClientCommand = "IX_TableStateChanges_ClientCommandId";

    /// <summary>Unique on the verified phone number: the number <i>is</i> the diner's account.</summary>
    public const string DinerUserPhone = "UX_DinerUsers_PhoneE164";

    /// <summary>
    /// Unique on the hash of an enrolment code. Named because the redemption path looks a code up
    /// by hash rather than by id - there is no id to look it up by, only what someone typed.
    /// </summary>
    public const string StaffEnrolmentCodeHash = "UX_StaffEnrolmentCodes_CodeHash";

    /// <summary>Unique on the hash of a session renewal handle.</summary>
    public const string StaffSessionRenewalHash = "UX_StaffSessions_RenewalTokenHash";

    /// <summary>Unique on the hash of a refresh token, which is how a presented token is found.</summary>
    public const string RefreshTokenHash = "UX_RefreshTokens_TokenHash";

    /// <summary>Unique on the hash of a password-reset handle.</summary>
    public const string PasswordResetTokenHash = "UX_PasswordResetTokens_TokenHash";

    /// <summary>
    /// Unique on a venue's staff email addresses, filtered so the many staff with no email do not
    /// collide on null.
    /// </summary>
    public const string StaffMemberEmail = "UX_StaffMembers_Email";
}
