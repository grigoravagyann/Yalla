using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// Marks every instant read from the database as UTC.
/// </summary>
/// <remarks>
/// <para>
/// <c>datetime2</c> stores no offset, so SQL Server hands every value back as
/// <see cref="DateTimeKind.Unspecified"/>. Nothing in this system stores anything but UTC, but the
/// missing kind leaks: <c>System.Text.Json</c> writes an unspecified <see cref="DateTime"/> with no
/// trailing <c>Z</c>, and a client then parses it as its own local time - four hours out in
/// Yerevan, and indistinguishable by eye from a correctly stamped one sitting next to it in the
/// same payload.
/// </para>
/// <para>
/// The write side is deliberately an identity. Callers are already stopped from handing the domain
/// a local-time instant by <c>Guard.NotLocalTime</c>, so converting on write would either be a
/// no-op or would paper over that check.
/// </para>
/// </remarks>
internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    instant => instant,
    stored => DateTime.SpecifyKind(stored, DateTimeKind.Utc));
