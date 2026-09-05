using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// The first platform admin, from the <c>PlatformAdmin</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// Read from user secrets in Development and from environment variables elsewhere - a password
/// is a secret and never sits in an appsettings file. In Development the host <b>fails startup</b>
/// when the section is missing: silently creating <c>admin / admin</c> is how a default account
/// ends up in production.
/// </para>
/// <para>
/// <see cref="SeedOnStartup"/> is what the test host switches off. Nothing else about seeding is
/// configurable, and the row is created only when no account has the address yet - an existing
/// admin's password is never reset from configuration.
/// </para>
/// </remarks>
public sealed class PlatformAdminOptions
{
    public const string SectionName = "PlatformAdmin";

    public string? Email { get; set; }

    public string? Password { get; set; }

    public string FullName { get; set; } = "Platform Administrator";

    /// <summary>A phone is required on every staff row; the admin has no tablet, so this is a placeholder.</summary>
    public string Phone { get; set; } = "+37400000000";

    public bool SeedOnStartup { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);
}

/// <summary>Creates the configured platform admin if no account holds that address.</summary>
internal sealed class PlatformAdminSeeder(
    YallaDbContext db,
    SecretHasher hasher,
    IOptions<PlatformAdminOptions> options,
    ILogger<PlatformAdminSeeder> logger)
{
    private readonly PlatformAdminOptions _options = options.Value;

    public async Task<Guid?> SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            return null;
        }

        var email = _options.Email!.Trim().ToLowerInvariant();

        var existing = await db.StaffMembers.FirstOrDefaultAsync(s => s.Email == email, cancellationToken);

        if (existing is not null)
        {
            if (existing.Role != StaffRole.PlatformAdmin)
            {
                throw new InvalidOperationException(
                    $"PlatformAdmin:Email '{email}' already belongs to a venue staff account. "
                    + "Choose a different address for the platform admin.");
            }

            return existing.Id;
        }

        var admin = StaffMember.PlatformAdmin(_options.FullName, _options.Phone, email, hasher.Hash(_options.Password!));
        db.StaffMembers.Add(admin);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Platform admin {StaffMemberId} seeded for {Email}.", admin.Id, email);

        return admin.Id;
    }
}
