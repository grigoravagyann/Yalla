using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Identity;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class DinerUserConfiguration : EntityConfiguration<DinerUser>
{
    protected override void ConfigureEntity(EntityTypeBuilder<DinerUser> builder)
    {
        builder.ToTable("DinerUsers");

        // Nullable for one reason: a deleted account keeps its row and gives the number back.
        builder.Property(d => d.PhoneE164)
            .HasMaxLength(FieldLengths.PhoneE164);

        builder.Property(d => d.DisplayName).HasMaxLength(FieldLengths.DisplayName);

        builder.Property(d => d.LocaleCode)
            .HasMaxLength(FieldLengths.LocaleCode)
            .IsRequired();

        builder.Property(d => d.IsActive).IsRequired();

        builder.Property(d => d.Username).HasMaxLength(FieldLengths.Username);
        builder.Property(d => d.Email).HasMaxLength(FieldLengths.Email);
        builder.Property(d => d.PasswordHash).HasMaxLength(FieldLengths.PasswordHash);
        builder.Property(d => d.PhoneVerifiedAtUtc);

        // Read on every diner request, through the authority check's cache. The migration gives
        // the column a database default of 0, so rows written by raw SQL start where EF's do.
        builder.Property(d => d.SessionGeneration).IsRequired();
        builder.Property(d => d.DeletedAtUtc);

        builder.Ignore(d => d.HasPassword);
        builder.Ignore(d => d.IsPhoneVerified);
        builder.Ignore(d => d.IsDeleted);

        // The phone number is the account. Unique among the rows that have one - every live account
        // does; a deleted one does not, which is what lets the number register again - and the
        // index the code sign-in reads.
        builder.HasIndex(d => d.PhoneE164)
            .IsUnique()
            .HasFilter("[PhoneE164] IS NOT NULL")
            .HasDatabaseName(DatabaseIndexNames.DinerUserPhone);

        // The other two sign-in keys, each filtered to the rows that have one: most accounts are
        // the code flow's and carry neither, and an unfiltered unique index would let exactly one
        // of them exist. Both are stored lowercased, so uniqueness does not lean on the collation.
        builder.HasIndex(d => d.Username)
            .IsUnique()
            .HasFilter("[Username] IS NOT NULL")
            .HasDatabaseName(DatabaseIndexNames.DinerUserUsername);

        builder.HasIndex(d => d.Email)
            .IsUnique()
            .HasFilter("[Email] IS NOT NULL")
            .HasDatabaseName(DatabaseIndexNames.DinerUserEmail);

        // The profile picture. SetNull rather than Restrict, unlike a branch's cover photo: a
        // picture of a person is nothing anybody else depends on, and a swept or removed one
        // should leave an account with no picture rather than an account that cannot be touched.
        // Configured explicitly so the navigation uses PhotoId rather than a shadow key beside it.
        builder.HasOne(d => d.Photo)
            .WithMany()
            .HasForeignKey(d => d.PhotoId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PhoneVerificationCodeConfiguration : EntityConfiguration<PhoneVerificationCode>
{
    protected override void ConfigureEntity(EntityTypeBuilder<PhoneVerificationCode> builder)
    {
        builder.ToTable("PhoneVerificationCodes");

        builder.Property(c => c.PhoneE164)
            .HasMaxLength(FieldLengths.PhoneE164)
            .IsRequired();

        builder.Property(c => c.CodeHash)
            .HasMaxLength(FieldLengths.PinHash)
            .IsRequired();

        builder.Property(c => c.ExpiresAtUtc).IsRequired();
        builder.Property(c => c.AttemptCount).IsRequired();
        builder.Property(c => c.RequestedFromAddress).HasMaxLength(FieldLengths.ClientAddress);

        // Verification reads the newest live code for one number. Filtered on unconsumed rows so
        // the index stays small however many codes a busy Friday issues.
        builder.HasIndex(c => new { c.PhoneE164, c.ExpiresAtUtc })
            .HasFilter("[ConsumedAtUtc] IS NULL");
    }
}

internal sealed class StaffDeviceConfiguration : EntityConfiguration<StaffDevice>
{
    protected override void ConfigureEntity(EntityTypeBuilder<StaffDevice> builder)
    {
        builder.ToTable("StaffDevices");

        builder.Property(d => d.Name)
            .HasMaxLength(FieldLengths.DeviceName)
            .IsRequired();

        builder.Property(d => d.ClientDeviceId)
            .HasMaxLength(FieldLengths.DeviceId)
            .IsRequired();

        // One live device per client identifier per branch. Filtered, so a browser that was revoked
        // and set up again can reuse the identifier it still has in storage - the alternative is a
        // device list that fills up with dead rows nobody can tell apart.
        builder.HasIndex(d => new { d.BranchId, d.ClientDeviceId })
            .IsUnique()
            .HasFilter("[RevokedAtUtc] IS NULL")
            .HasDatabaseName(DatabaseIndexNames.LiveDevicePerClientId);

        builder.HasOne(d => d.Branch)
            .WithMany()
            .HasForeignKey(d => d.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        // The admin panel lists a branch's tablets, and every staff request checks one for
        // revocation - so both of those reads are covered here.
        builder.HasIndex(d => new { d.BranchId, d.RevokedAtUtc });
    }
}

internal sealed class StaffEnrolmentCodeConfiguration : EntityConfiguration<StaffEnrolmentCode>
{
    protected override void ConfigureEntity(EntityTypeBuilder<StaffEnrolmentCode> builder)
    {
        builder.ToTable("StaffEnrolmentCodes");

        builder.Property(c => c.CodeHash)
            .HasMaxLength(FieldLengths.TokenHash)
            .IsRequired();

        builder.Property(c => c.ExpiresAtUtc).IsRequired();

        // Single use, enforced by the database rather than by a check-then-write.
        //
        // Making the redemption stamp a concurrency token puts "AND RedeemedAtUtc IS NULL" into
        // the UPDATE, so two tablets racing to redeem the same code cannot both win: the loser's
        // update matches no rows and its whole transaction - device row included - rolls back.
        builder.Property(c => c.RedeemedAtUtc).IsConcurrencyToken();

        builder.HasOne(c => c.Branch)
            .WithMany()
            .HasForeignKey(c => c.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => c.CodeHash)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.StaffEnrolmentCodeHash);
    }
}

internal sealed class StaffSessionConfiguration : EntityConfiguration<StaffSession>
{
    protected override void ConfigureEntity(EntityTypeBuilder<StaffSession> builder)
    {
        builder.ToTable("StaffSessions");

        builder.Property(s => s.RenewalTokenHash)
            .HasMaxLength(FieldLengths.TokenHash)
            .IsRequired();

        builder.Property(s => s.LastActivityAtUtc).IsRequired();
        builder.Property(s => s.AbsoluteExpiresAtUtc).IsRequired();

        builder.HasOne(s => s.StaffMember)
            .WithMany()
            .HasForeignKey(s => s.StaffMemberId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(s => s.StaffDevice)
            .WithMany()
            .HasForeignKey(s => s.StaffDeviceId)
            .OnDelete(DeleteBehavior.Restrict);

        // Renewal looks the session up by the handle it was given, so this is the hot path.
        builder.HasIndex(s => s.RenewalTokenHash)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.StaffSessionRenewalHash);

        // Signing in on a tablet ends whatever session was open on it.
        builder.HasIndex(s => new { s.StaffDeviceId, s.EndedAtUtc });
    }
}

internal sealed class RefreshTokenConfiguration : EntityConfiguration<RefreshToken>
{
    protected override void ConfigureEntity(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");

        builder.Property(t => t.SubjectType).IsRequired();
        builder.Property(t => t.ExpiresAtUtc).IsRequired();

        builder.Property(t => t.TokenHash)
            .HasMaxLength(FieldLengths.TokenHash)
            .IsRequired();

        builder.Property(t => t.RevokedReason).HasMaxLength(FieldLengths.Slug);

        builder.HasIndex(t => t.TokenHash)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.RefreshTokenHash);

        // Revoking a chain touches every token in it, which is the whole point of the column.
        builder.HasIndex(t => t.ChainId);

        // Resetting a password revokes everything the account holds.
        builder.HasIndex(t => new { t.SubjectType, t.SubjectId });
    }
}

internal sealed class PasswordResetTokenConfiguration : EntityConfiguration<PasswordResetToken>
{
    protected override void ConfigureEntity(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.ToTable("PasswordResetTokens");

        builder.Property(t => t.TokenHash)
            .HasMaxLength(FieldLengths.TokenHash)
            .IsRequired();

        builder.Property(t => t.ExpiresAtUtc).IsRequired();

        builder.HasOne(t => t.StaffMember)
            .WithMany()
            .HasForeignKey(t => t.StaffMemberId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => t.TokenHash)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.PasswordResetTokenHash);
    }
}
