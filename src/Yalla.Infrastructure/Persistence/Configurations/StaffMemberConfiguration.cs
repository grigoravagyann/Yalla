using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Staff;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class StaffMemberConfiguration : EntityConfiguration<StaffMember>
{
    protected override void ConfigureEntity(EntityTypeBuilder<StaffMember> builder)
    {
        builder.ToTable("StaffMembers");

        builder.Property(s => s.FullName)
            .HasMaxLength(FieldLengths.PersonName)
            .IsRequired();

        builder.Property(s => s.Phone)
            .HasMaxLength(FieldLengths.Phone)
            .IsRequired();

        builder.Property(s => s.Role).IsRequired();
        builder.Property(s => s.IsActive).IsRequired();

        builder.Property(s => s.PinHash)
            .HasMaxLength(FieldLengths.PinHash)
            .IsRequired();

        builder.Property(s => s.PinFailedAttempts).IsRequired();

        // Null for the many staff who only ever tap a PIN: an email sign-in is for owners and
        // managers working in the admin panel.
        builder.Property(s => s.Email).HasMaxLength(FieldLengths.Email);

        builder.Property(s => s.PasswordHash).HasMaxLength(FieldLengths.PasswordHash);

        // One address, one account, across the whole system - the panel's sign-in form has no
        // venue field to disambiguate with. Filtered, because most rows have no email at all and
        // SQL Server would otherwise treat every null as a collision.
        builder.HasIndex(s => s.Email)
            .IsUnique()
            .HasFilter("[Email] IS NOT NULL")
            .HasDatabaseName(DatabaseIndexNames.StaffMemberEmail);

        builder.Ignore(s => s.IsPlatformAdmin);
        builder.Ignore(s => s.HasPasswordCredentials);

        // Null only for a platform admin, who is staff of no venue. Every other role requires one,
        // and the entity enforces that; the column is nullable so the one exception can exist.
        builder.HasOne(s => s.Venue)
            .WithMany(v => v.Staff)
            .HasForeignKey(s => s.VenueId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // Null means every branch of the venue, which is the normal case for an owner.
        builder.HasOne(s => s.Branch)
            .WithMany()
            .HasForeignKey(s => s.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => new { s.VenueId, s.IsActive });
    }
}
