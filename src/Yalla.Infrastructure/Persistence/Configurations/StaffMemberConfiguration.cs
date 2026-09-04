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

        builder.HasOne(s => s.Venue)
            .WithMany(v => v.Staff)
            .HasForeignKey(s => s.VenueId)
            .OnDelete(DeleteBehavior.Restrict);

        // Null means every branch of the venue, which is the normal case for an owner.
        builder.HasOne(s => s.Branch)
            .WithMany()
            .HasForeignKey(s => s.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => new { s.VenueId, s.IsActive });
    }
}
