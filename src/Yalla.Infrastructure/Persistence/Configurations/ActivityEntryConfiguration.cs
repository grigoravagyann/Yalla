using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Audit;
using Yalla.Domain.Common;

namespace Yalla.Infrastructure.Persistence.Configurations;

/// <summary>
/// The activity log. No foreign keys at all - the log is the record and outlives what it names, as
/// <see cref="PlatformAuditLogConfiguration"/>'s does - and three indexes, one per way it is read.
/// </summary>
internal sealed class ActivityEntryConfiguration : EntityConfiguration<ActivityEntry>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ActivityEntry> builder)
    {
        builder.ToTable("ActivityEntries");

        builder.Property(e => e.VenueId).IsRequired();
        builder.Property(e => e.BranchId);
        builder.Property(e => e.OccurredAtUtc).IsRequired();
        builder.Property(e => e.ActorKind).IsRequired();
        builder.Property(e => e.StaffMemberId);
        builder.Property(e => e.ActorName).HasMaxLength(FieldLengths.PersonName);
        builder.Property(e => e.DeviceId);
        builder.Property(e => e.DeviceLabel).HasMaxLength(FieldLengths.DeviceName);
        builder.Property(e => e.DeviceKind);
        builder.Property(e => e.DeviceOwnerName).HasMaxLength(FieldLengths.PersonName);
        builder.Property(e => e.Category).IsRequired();

        builder.Property(e => e.Action)
            .HasMaxLength(FieldLengths.ActivityAction)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(e => e.SubjectType)
            .HasMaxLength(FieldLengths.ActivitySubjectType)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(e => e.SubjectId).IsRequired();
        builder.Property(e => e.TableSessionId);
        builder.Property(e => e.SubjectCode).HasMaxLength(FieldLengths.ActivitySubjectCode);
        builder.Property(e => e.TableLabel).HasMaxLength(FieldLengths.TableLabel);
        builder.Property(e => e.GuestName).HasMaxLength(FieldLengths.PersonName);
        builder.Property(e => e.ItemNames).HasMaxLength(FieldLengths.ActivityItemNames);
        builder.Property(e => e.AmountAmd);
        builder.Property(e => e.IsMoney).IsRequired();
        builder.Property(e => e.BeforeJson).HasMaxLength(ActivityEntry.MaxSnapshotLength);
        builder.Property(e => e.AfterJson).HasMaxLength(ActivityEntry.MaxSnapshotLength);
        builder.Property(e => e.Reason).HasMaxLength(FieldLengths.Reason);

        builder.Property(e => e.SummaryJson)
            .HasMaxLength(ActivityEntry.MaxSummaryLength)
            .IsRequired();

        // The History page: one branch's window, newest first - the id breaks a tie of instants for the cursor.
        builder.HasIndex(e => new { e.BranchId, e.OccurredAtUtc, e.Id })
            .HasDatabaseName("IX_ActivityEntries_BranchId_OccurredAtUtc");

        // A person's page and their own results: one staff member's entries in a window.
        builder.HasIndex(e => new { e.StaffMemberId, e.OccurredAtUtc })
            .HasDatabaseName("IX_ActivityEntries_StaffMemberId_OccurredAtUtc")
            .HasFilter("[StaffMemberId] IS NOT NULL");

        // A bill's or a booking's timeline.
        builder.HasIndex(e => new { e.SubjectType, e.SubjectId, e.OccurredAtUtc })
            .HasDatabaseName("IX_ActivityEntries_Subject");

        // A bill's timeline: everything that happened in one sitting.
        builder.HasIndex(e => new { e.TableSessionId, e.OccurredAtUtc })
            .HasDatabaseName("IX_ActivityEntries_TableSessionId")
            .HasFilter("[TableSessionId] IS NOT NULL");

        // Venue-wide entries (no branch) for the owner's feed - only those: filtered, so the read seeks the few
        // branch-less rows instead of every branch's entries of the venue, looked up one by one for their BranchId.
        builder.HasIndex(e => new { e.VenueId, e.OccurredAtUtc, e.Id })
            .HasDatabaseName("IX_ActivityEntries_VenueId_OccurredAtUtc")
            .HasFilter("[BranchId] IS NULL");

        // The purge's range by age.
        builder.HasIndex(e => new { e.IsMoney, e.OccurredAtUtc })
            .HasDatabaseName("IX_ActivityEntries_Retention");
    }
}
