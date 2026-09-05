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

    /// <summary>
    /// Unique on the booking caller's command id. A violation means a diner's retry raced its own
    /// original, and the loser answers with the booking that won rather than taking a second table.
    /// </summary>
    public const string ReservationClientCommand = "IX_Reservations_ClientCommandId";

    /// <summary>
    /// Unique on the short code the diner quotes at the door. A violation is a genuine random
    /// collision, and the only sane response is to roll another code.
    /// </summary>
    public const string ReservationCode = "IX_Reservations_Code";

    /// <summary>
    /// Unique on a tab's session: one bill per seating. A violation means another phone at the
    /// same table attached a tab to this session first, and the loser joins that tab instead.
    /// </summary>
    public const string TabPerSession = "UX_Tabs_TableSessionId";

    /// <summary>
    /// Unique on the command that opened a tab. A violation means the same scan is being replayed
    /// concurrently with its original, and the loser answers with the tab that won.
    /// </summary>
    public const string TabClientCommand = "UX_Tabs_ClientCommandId";

    /// <summary>Unique on a venue's slug. A violation means the platform admin picked a taken slug.</summary>
    public const string VenueSlug = "IX_Venues_Slug";

    /// <summary>Unique on (branch, label). A violation means a floor plan reused a table label.</summary>
    public const string TableLabelPerBranch = "IX_DiningTables_BranchId_Label";

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
