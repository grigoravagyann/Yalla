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
}
