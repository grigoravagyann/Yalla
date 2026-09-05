using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Audit;
using Yalla.Domain.Common;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class TableStateChangeConfiguration : EntityConfiguration<TableStateChange>
{
    protected override void ConfigureEntity(EntityTypeBuilder<TableStateChange> builder)
    {
        builder.ToTable("TableStateChanges");

        builder.Property(c => c.FromStatus).IsRequired();
        builder.Property(c => c.ToStatus).IsRequired();

        builder.Property(c => c.Reason)
            .HasMaxLength(FieldLengths.Reason)
            .IsRequired();

        builder.Property(c => c.ActorType).IsRequired();
        builder.Property(c => c.AtUtc).IsRequired();

        // Opaque references, not foreign keys: the actor may be a diner (identity is a later
        // module) or the system itself, and the reservation or tab is recorded for context.
        builder.Property(c => c.ActorId);
        builder.Property(c => c.ReservationId);
        builder.Property(c => c.TabId);
        builder.Property(c => c.TableSessionId);

        builder.Property(c => c.ClientCommandId)
            .IsRequired();

        builder.HasOne(c => c.Branch)
            .WithMany()
            .HasForeignKey(c => c.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.DiningTable)
            .WithMany()
            .HasForeignKey(c => c.DiningTableId)
            .OnDelete(DeleteBehavior.Restrict);

        // "What happened to table 7 last night", and the raw feed for turnover reporting.
        // The database assigns it. ValueGeneratedOnAdd alone would let EF think it owns the
        // value; UseIdentityColumn is what makes SQL Server hand out the monotonic run.
        builder.Property(c => c.Sequence)
            .ValueGeneratedOnAdd()
            .UseIdentityColumn();

        // The stream read: "everything at this branch after N, in order". Covering, so catching up
        // after a dropped connection is a range seek rather than a scan of the branch's history.
        //
        // It also serves the MAX(Sequence) the floor projection folds in, which runs on every floor
        // render against a table that grows without bound. Confirmed from the plan rather than from
        // the index list, because "there is an index" and "the query uses it" are different claims:
        //
        //   Stream Aggregate
        //     |--Top(TOP EXPRESSION:((1)))
        //          |--Index Seek(OBJECT:([TableStateChanges].[IX_TableStateChanges_BranchId_Sequence]),
        //                        SEEK:([t].[BranchId]=[@__branchId_0]) ORDERED BACKWARD)
        //
        // Top(1) over a backward-ordered seek: one row read, not an aggregate over the branch's
        // history, so the cost does not move as the log grows.
        builder.HasIndex(c => new { c.BranchId, c.Sequence })
            .HasDatabaseName("IX_TableStateChanges_BranchId_Sequence");

        builder.HasIndex(c => new { c.DiningTableId, c.AtUtc });

        builder.HasIndex(c => new { c.BranchId, c.AtUtc });

        // The idempotency guarantee. This unique index - not a check-then-insert in the service -
        // is what makes a replayed offline command a no-op: two simultaneous replays both try to
        // insert, one is rejected here, and the loser answers from the row that won.
        builder.HasIndex(c => c.ClientCommandId)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.TableStateChangeClientCommand);
    }
}
