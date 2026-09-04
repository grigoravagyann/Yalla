namespace Yalla.Domain.Common;

/// <summary>
/// Argument checks used by entity constructors to enforce invariants at the point of construction.
/// </summary>
internal static class Guard
{
    public static Guid NotEmpty(Guid value, string paramName) =>
        value == Guid.Empty
            ? throw new ArgumentException("Identifier must not be empty.", paramName)
            : value;

    public static string NotBlank(string? value, string paramName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be null or blank.", paramName);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new ArgumentException($"Value must be at most {maxLength} characters.", paramName)
            : trimmed;
    }

    public static string? OptionalText(string? value, string paramName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new ArgumentException($"Value must be at most {maxLength} characters.", paramName)
            : trimmed;
    }

    public static int Positive(int value, string paramName) =>
        value <= 0
            ? throw new ArgumentOutOfRangeException(paramName, value, "Value must be greater than zero.")
            : value;

    public static int NotNegative(int value, string paramName) =>
        value < 0
            ? throw new ArgumentOutOfRangeException(paramName, value, "Value must not be negative.")
            : value;

    public static long NotNegativeAmd(long value, string paramName) =>
        value < 0
            ? throw new ArgumentOutOfRangeException(paramName, value, "Dram amounts must not be negative.")
            : value;

    /// <summary>
    /// Rejects a local-time instant. Every stored instant is UTC, so a value carrying
    /// <see cref="DateTimeKind.Local"/> is a bug at the call site, not something to silently convert.
    /// </summary>
    public static DateTime NotLocalTime(DateTime value, string paramName) =>
        value.Kind == DateTimeKind.Local
            ? throw new ArgumentException("Instants must be UTC, not local time.", paramName)
            : value;

    public static TEnum Defined<TEnum>(TEnum value, string paramName) where TEnum : struct, Enum =>
        Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(paramName, value, $"Not a defined {typeof(TEnum).Name} value.");
}
