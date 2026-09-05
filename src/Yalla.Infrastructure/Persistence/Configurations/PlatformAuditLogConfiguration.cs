using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Audit;
using Yalla.Domain.Common;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class PlatformAuditLogConfiguration : EntityConfiguration<PlatformAuditLog>
{
    protected override void ConfigureEntity(EntityTypeBuilder<PlatformAuditLog> builder)
    {
        builder.ToTable("PlatformAuditLogs");

        builder.Property(l => l.ActorStaffMemberId).IsRequired();

        builder.Property(l => l.Action)
            .HasMaxLength(FieldLengths.AuditAction)
            .IsRequired();

        builder.Property(l => l.TargetType)
            .HasMaxLength(FieldLengths.AuditTargetType)
            .IsRequired();

        builder.Property(l => l.TargetId).IsRequired();

        // JSON, unbounded. A venue snapshot with its branches is a few kilobytes at most.
        builder.Property(l => l.ChangesJson)
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(l => l.AtUtc).IsRequired();

        // "What happened to this venue" and "what did this admin do", newest last.
        builder.HasIndex(l => new { l.TargetType, l.TargetId, l.AtUtc });
        builder.HasIndex(l => new { l.ActorStaffMemberId, l.AtUtc });
    }
}
