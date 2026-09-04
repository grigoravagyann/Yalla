namespace Yalla.Domain.Common;

/// <summary>
/// Maximum lengths for every string column, shared by the domain constructors that validate
/// them and the EF Core configurations that map them, so the two cannot drift apart.
/// </summary>
public static class FieldLengths
{
    public const int Slug = 120;
    public const int Name = 200;
    public const int PersonName = 200;
    public const int Phone = 32;
    public const int Address = 400;

    /// <summary>IANA time zone identifier, e.g. <c>Asia/Yerevan</c>.</summary>
    public const int TimeZoneId = 64;

    /// <summary>Table label as printed on the floor, e.g. 7 or T12.</summary>
    public const int TableLabel = 16;

    public const int QrToken = 64;
    public const int JoinToken = 128;
    public const int ReservationCode = 12;
    public const int DisplayName = 100;
    public const int DeviceId = 128;
    public const int Reason = 500;
    public const int Description = 2000;
    public const int Url = 2048;
    public const int Ingredients = 2000;
    public const int Allergens = 500;
    public const int PortionSize = 100;
    public const int ProviderReference = 200;
    public const int PinHash = 256;

    /// <summary>Password hash produced by <c>PasswordHasher&lt;T&gt;</c>, base64.</summary>
    public const int PasswordHash = 256;

    /// <summary>The practical maximum for an email address (RFC 3696 errata 1690).</summary>
    public const int Email = 320;

    /// <summary>
    /// Hex SHA-256 of an opaque high-entropy secret - a refresh handle, an enrolment code, a
    /// device secret. 64 characters today; the column is wider so a longer digest does not need a
    /// migration.
    /// </summary>
    public const int TokenHash = 128;

    /// <summary>A phone number in E.164, e.g. <c>+37411223344</c>.</summary>
    public const int PhoneE164 = 20;

    /// <summary>BCP 47 language tag, restricted here to <c>hy</c>, <c>ru</c> and <c>en</c>.</summary>
    public const int LocaleCode = 16;

    /// <summary>Name a manager gives an enrolled tablet, e.g. "Bar tablet".</summary>
    public const int DeviceName = 100;

    /// <summary>Client address recorded against a sign-in attempt, for diagnostics only.</summary>
    public const int ClientAddress = 64;
}
