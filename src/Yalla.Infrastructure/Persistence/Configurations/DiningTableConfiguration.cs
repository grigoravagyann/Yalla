using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class DiningTableConfiguration : EntityConfiguration<DiningTable>
{
    protected override void ConfigureEntity(EntityTypeBuilder<DiningTable> builder)
    {
        builder.ToTable("DiningTables");

        builder.Property(t => t.Label)
            .HasMaxLength(FieldLengths.TableLabel)
            .IsRequired();

        builder.Property(t => t.Seats)
            .IsRequired();

        // The pin: which room photo, and where on it, 0-1 each way. All three null for a table with no
        // pin, which staff still seat and diners are not offered.
        builder.Property(t => t.PhotoX);
        builder.Property(t => t.PhotoY);

        builder.Property(t => t.IsBookable).IsRequired();
        builder.Property(t => t.IsActive).IsRequired();
        builder.Property(t => t.Status).IsRequired();

        // Compared exactly, by a collation chosen here rather than whatever the database defaults
        // to. The lookup lower-cases what was scanned and every stored token is lower-case (see
        // DiningTable.NormaliseQrToken), so a scan matches by the server's own rule. It used to work
        // only because the default collation ignored case, which hid the diner app upper-casing
        // every scan. Binary is also the cheapest comparison for the lookup every walk-in makes.
        builder.Property(t => t.QrToken)
            .HasMaxLength(FieldLengths.QrToken)
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired();

        // A plain column, not a foreign key: see the remarks on the property. TableSession
        // already points at the table, and an opposing key would make the pair circular.
        builder.Property(t => t.CurrentSessionId);

        builder.Property(t => t.RowVersion)
            .IsRowVersion();

        builder.HasOne(t => t.Branch)
            .WithMany(b => b.DiningTables)
            .HasForeignKey(t => t.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.FloorArea)
            .WithMany(a => a.DiningTables)
            .HasForeignKey(t => t.FloorAreaId)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict, like every other photo reference: a picture a table is pinned on must not be
        // deletable out from under the pin. Removing the photo from its room takes the pin off first.
        builder.HasOne(t => t.Photo)
            .WithMany()
            .HasForeignKey(t => t.PhotoId)
            .OnDelete(DeleteBehavior.Restrict);

        // Table 7 exists once per branch.
        builder.HasIndex(t => new { t.BranchId, t.Label })
            .IsUnique();

        // The QR token is the lookup key for every walk-in who scans a table.
        builder.HasIndex(t => t.QrToken)
            .IsUnique();

        // Renders the floor plan: every table in a branch, with its cached occupancy.
        builder.HasIndex(t => new { t.BranchId, t.Status });
    }
}
