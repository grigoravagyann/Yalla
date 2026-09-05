using System.Text.Json;
using System.Text.Json.Serialization;
using Yalla.Domain.Billing;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests;

/// <summary>
/// Emits <c>docs/billing-vectors.json</c>: the arithmetic's answers, in a file another
/// implementation can be tested against.
/// </summary>
/// <remarks>
/// <para>
/// The mobile app has its own copy of this arithmetic, so its offline bill matches the one the
/// server will send. A property test on that copy proves it is self-consistent, which is not the
/// same as proving it agrees with us - two implementations can both be internally coherent, both
/// green, and quietly disagree about what three people owe.
/// </para>
/// <para>
/// So the side that owns the arithmetic publishes its answers. These vectors are produced by
/// <see cref="TabBilling.Compute"/> itself and committed, and the client asserts against them. The
/// numbers in the file are not maintained by hand and must never be edited by hand.
/// </para>
/// <para>
/// <b>The file is a contract, so this test compares rather than overwrites.</b> A change to the
/// arithmetic fails here with the vector that moved, and regenerating is a deliberate act - set
/// <c>YALLA_WRITE_BILLING_VECTORS=1</c> - followed by reading the diff and telling the client team.
/// A test that silently rewrote the file would turn a breaking change into a quiet commit.
/// </para>
/// </remarks>
public sealed class GoldenBillingVectorTests
{
    /// <summary>Set this to rewrite the committed file after a deliberate change.</summary>
    public const string RegenerateVariable = "YALLA_WRITE_BILLING_VECTORS";

    /// <summary>
    /// Bump when the shape of the file changes, so a client can refuse a file it cannot read.
    /// </summary>
    /// <remarks>
    /// The <i>numbers</i> changing is not a schema change - it is a bug fix or a rule change, and the
    /// client finds out by its own tests going red, which is the entire point.
    /// </remarks>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // Fixed ids, because the file is committed and a diff of it should show arithmetic that moved
    // and nothing else. Real ones are UUIDv7; only their relative order carries meaning.
    private static Guid Person(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static Guid Line(int n) => new($"11111111-1111-1111-1111-{n:D12}");

    [Fact]
    public void The_committed_vectors_match_what_the_arithmetic_produces_today()
    {
        var vectors = Vectors().Select(Solve).ToList();

        var document = new VectorFile(
            SchemaVersion,
            "Produced by Yalla.UnitTests.GoldenBillingVectorTests. Do not edit by hand - see that "
            + "test for how to regenerate. Every amount is whole Armenian dram.",
            "TabBilling.Compute(lines, adjustments, participants, serviceChargePercent, paidAmd)",
            vectors);

        var produced = JsonSerializer.Serialize(document, Json).ReplaceLineEndings("\n");
        var path = VectorPath();

        if (!File.Exists(path) || Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            File.WriteAllText(path, produced + "\n");

            return;
        }

        var committed = File.ReadAllText(path).ReplaceLineEndings("\n").TrimEnd('\n');

        if (string.Equals(committed, produced, StringComparison.Ordinal))
        {
            return;
        }

        // Name the vector that moved rather than dumping two files at whoever reads the failure.
        var mine = JsonSerializer.Deserialize<VectorFile>(committed, Json);
        var moved = mine is null
            ? "the file could not be parsed"
            : string.Join(
                ", ",
                vectors.Where(v => Differs(v, mine.Vectors)).Select(v => v.Name).DefaultIfEmpty("none"));

        Assert.Fail(
            $"docs/billing-vectors.json no longer matches TabBilling.Compute. Vectors that moved: {moved}. "
            + "If the change is intended, regenerate with "
            + $"{RegenerateVariable}=1, read the diff, and tell whoever ships the client - their bill "
            + "arithmetic is a port of this one and will now disagree with the server.");
    }

    private static bool Differs(SolvedVector produced, IReadOnlyList<SolvedVector> committed)
    {
        var match = committed.FirstOrDefault(v => v.Name == produced.Name);

        return match is null
               || JsonSerializer.Serialize(match.Expected, Json)
                  != JsonSerializer.Serialize(produced.Expected, Json);
    }

    // ------------------------------------------------------------ the cases

    /// <summary>
    /// The cases worth publishing.
    /// </summary>
    /// <remarks>
    /// Chosen for the disagreements a re-implementation actually has: where rounding lands, whether
    /// a comp takes its service charge with it, what happens to a removed guest's food, and whether
    /// a pending joiner is on the bill. Even splits of round numbers agree by accident.
    /// </remarks>
    private static IEnumerable<Vector> Vectors()
    {
        var host = Person(1);
        var guestA = Person(2);
        var guestB = Person(3);

        yield return new Vector(
            "one-diner-round-numbers",
            "The simplest bill there is. If this one disagrees, nothing else is worth reading.",
            [Own(1, host, 3_200, 2)],
            [],
            [Host(host)],
            10m,
            0L);

        yield return new Vector(
            "three-way-shared-bottle",
            "A bottle ordered for the table, split across everyone who was sitting at it.",
            [
                Own(1, host, 3_200, 1),
                Own(2, guestA, 4_500, 1),
                Own(3, guestB, 2_800, 1),
                Shared(4, 9_000, 1, [host, guestA, guestB]),
            ],
            [],
            [Host(host), Guest(guestA), Guest(guestB)],
            10m,
            0L);

        yield return new Vector(
            "residue-does-not-divide-by-three",
            "A shared line of 1,000 across three people. 333 each leaves one dram, and largest "
            + "remainder decides who gets it - not 'give it to the host', which can hand somebody "
            + "who ordered a coffee a share larger than their food.",
            [Shared(1, 1_000, 1, [host, guestA, guestB])],
            [],
            [Host(host), Guest(guestA), Guest(guestB)],
            0m,
            0L);

        yield return new Vector(
            "service-charge-rounds-once-at-the-end",
            "7.5% on 12,345. Rounding each participant's slice and summing gives a different "
            + "answer from summing and rounding, and only one of the two matches the printed bill.",
            [
                Own(1, host, 4_115, 1),
                Own(2, guestA, 4_115, 1),
                Own(3, guestB, 4_115, 1),
            ],
            [],
            [Host(host), Guest(guestA), Guest(guestB)],
            7.5m,
            0L);

        yield return new Vector(
            "voided-line-counts-zero",
            "A voided line stays on the bill and contributes nothing. It is still listed, because "
            + "a diner who watched it be removed should see that it was.",
            [
                Own(1, host, 3_200, 1),
                Voided(2, guestA, 9_500, 1),
            ],
            [],
            [Host(host), Guest(guestA)],
            10m,
            0L);

        yield return new Vector(
            "comping-a-dish-comps-its-service-charge",
            "A 100% line adjustment. The service charge is computed on what is left, so comping a "
            + "dish comps the service on it too - which is what a manager means by comping a dish.",
            [
                Own(1, host, 3_200, 1),
                Own(2, guestA, 5_000, 1),
            ],
            [new BillingAdjustment(Line(2), 100m, null)],
            [Host(host), Guest(guestA)],
            10m,
            0L);

        yield return new Vector(
            "tab-wide-percentage-discount",
            "10% off the whole tab, then service charge on the reduced subtotal.",
            [
                Own(1, host, 6_000, 1),
                Own(2, guestA, 4_000, 1),
            ],
            [new BillingAdjustment(null, 10m, null)],
            [Host(host), Guest(guestA)],
            10m,
            0L);

        yield return new Vector(
            "flat-discount-is-capped-at-the-line",
            "A 5,000 discount on a 3,200 line takes 3,200, not 5,000. A bill can reach zero and "
            + "never goes below it.",
            [Own(1, host, 3_200, 1)],
            [new BillingAdjustment(Line(1), null, 5_000L)],
            [Host(host)],
            10m,
            0L);

        yield return new Vector(
            "removed-guest-is-absorbed-by-the-host",
            "Somebody was removed from the tab. What they ate does not vanish - it falls to the "
            + "host, and is reported separately rather than folded in silently.",
            [
                Own(1, host, 3_000, 1),
                Own(2, guestA, 4_400, 1),
            ],
            [],
            [Host(host), new BillingParticipant(guestA, false, ParticipantStatus.Removed)],
            10m,
            0L);

        yield return new Vector(
            "pending-guest-still-owes-what-they-ordered",
            "A guest waiting for the host's approval has already eaten. Approval governs ordering, "
            + "not whether the food was consumed.",
            [
                Own(1, host, 3_000, 1),
                Own(2, guestA, 2_500, 1),
            ],
            [],
            [Host(host), new BillingParticipant(guestA, false, ParticipantStatus.PendingApproval)],
            10m,
            0L);

        yield return new Vector(
            "table-attributed-line-has-no-owner",
            "A waiter keyed in a spoken order and could not say who asked for it, so it splits "
            + "across the table.",
            [
                Own(1, host, 2_000, 1),
                Shared(2, 3_500, 1, [host, guestA]),
            ],
            [],
            [Host(host), Guest(guestA)],
            10m,
            0L);

        yield return new Vector(
            "no-service-charge",
            "A branch that charges none. The service charge line is zero rather than absent.",
            [
                Own(1, host, 2_500, 1),
                Own(2, guestA, 2_500, 1),
            ],
            [],
            [Host(host), Guest(guestA)],
            0m,
            0L);

        yield return new Vector(
            "partly-paid",
            "One person has settled. Payment is reported against them and never netted off their "
            + "share, so the split still says what each person owed.",
            [
                Own(1, host, 5_000, 1),
                Own(2, guestA, 5_000, 1),
            ],
            [],
            [Host(host), new BillingParticipant(guestA, false, ParticipantStatus.Approved, 5_500L)],
            10m,
            5_500L);

        yield return new Vector(
            "everything-voided",
            "A zero bill. The tab exists, the lines are on it, and nobody owes anything.",
            [
                Voided(1, host, 3_200, 1),
                Voided(2, guestA, 4_500, 1),
            ],
            [],
            [Host(host), Guest(guestA)],
            10m,
            0L);
    }

    // ------------------------------------------------------------ building and solving

    private static SolvedVector Solve(Vector vector)
    {
        var bill = TabBilling.Compute(
            vector.Lines, vector.Adjustments, vector.Participants, vector.ServiceChargePercent, vector.PaidAmd);

        var input = new VectorInput(
            vector.ServiceChargePercent,
            vector.PaidAmd,
            [.. vector.Lines.Select(l => new VectorLine(
                l.LineId, l.OwnerParticipantId, l.UnitPriceAmd, l.Quantity, l.IsVoided,
                l.IsSplitAcrossParticipants, l.ShareParticipantIds))],
            [.. vector.Adjustments.Select(a => new VectorAdjustment(a.LineId, a.Percent, a.AmountAmd))],
            [.. vector.Participants.Select(p => new VectorParticipant(
                p.ParticipantId, p.IsHost, p.Status.ToString(), (int)p.Status, p.PaidAmd))]);

        var expected = new VectorExpectation(
            bill.SubtotalAmd,
            bill.ServiceChargeAmd,
            bill.TotalAmd,
            bill.PaidAmd,
            bill.RemainingAmd,
            bill.AbsorbedFromRemovedAmd,
            [.. bill.Shares.Select(s => new VectorShare(
                s.ParticipantId, s.OwnItemsAmd, s.SharedItemsAmd, s.AbsorbedFromRemovedAmd,
                s.PersonalAmd, s.ServiceChargeAmd, s.ShareAmd, s.PaidAmd))]);

        // The invariant the whole file exists to pin down. Asserted here as well as published, so a
        // regeneration cannot commit a set of shares that do not add up.
        Assert.Equal(expected.TotalAmd, expected.Shares.Sum(s => s.ShareAmd));

        return new SolvedVector(vector.Name, vector.Description, input, expected);
    }

    private static BillingLine Own(int line, Guid owner, long price, int quantity) =>
        new(Line(line), owner, price, quantity, false, false, []);

    private static BillingLine Voided(int line, Guid owner, long price, int quantity) =>
        new(Line(line), owner, price, quantity, true, false, []);

    private static BillingLine Shared(int line, long price, int quantity, IReadOnlyList<Guid> across) =>
        new(Line(line), null, price, quantity, false, true, across);

    private static BillingParticipant Host(Guid id) => new(id, true, ParticipantStatus.Approved);

    private static BillingParticipant Guest(Guid id) => new(id, false, ParticipantStatus.Approved);

    /// <summary>
    /// <c>docs/billing-vectors.json</c>, found by walking up to the solution file.
    /// </summary>
    /// <remarks>
    /// The file belongs beside <c>docs/billing.md</c>, which is where somebody looking for the rules
    /// would go. Writing it into the test output directory would make it a build artefact, and the
    /// point is that it is committed.
    /// </remarks>
    private static string VectorPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yalla.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                "Could not find Yalla.sln above the test output directory, so there is nowhere to "
                + "write the billing vectors.");
        }

        var docs = Path.Combine(directory.FullName, "docs");

        Directory.CreateDirectory(docs);

        return Path.Combine(docs, "billing-vectors.json");
    }

    // ------------------------------------------------------------ the shape on disk

    private sealed record Vector(
        string Name,
        string Description,
        IReadOnlyList<BillingLine> Lines,
        IReadOnlyList<BillingAdjustment> Adjustments,
        IReadOnlyList<BillingParticipant> Participants,
        decimal ServiceChargePercent,
        long PaidAmd);

    private sealed record VectorFile(
        int SchemaVersion,
        string Note,
        string Function,
        IReadOnlyList<SolvedVector> Vectors);

    private sealed record SolvedVector(
        string Name,
        string Description,
        VectorInput Input,
        VectorExpectation Expected);

    private sealed record VectorInput(
        decimal ServiceChargePercent,
        long PaidAmd,
        IReadOnlyList<VectorLine> Lines,
        IReadOnlyList<VectorAdjustment> Adjustments,
        IReadOnlyList<VectorParticipant> Participants);

    private sealed record VectorLine(
        Guid LineId,
        Guid? OwnerParticipantId,
        long UnitPriceAmd,
        int Quantity,
        bool IsVoided,
        bool IsSplitAcrossParticipants,
        IReadOnlyList<Guid> ShareParticipantIds);

    private sealed record VectorAdjustment(Guid? LineId, decimal? Percent, long? AmountAmd);

    /// <summary>The status is published as both name and number, so a client can match on either.</summary>
    private sealed record VectorParticipant(
        Guid ParticipantId,
        bool IsHost,
        string Status,
        int StatusValue,
        long PaidAmd);

    private sealed record VectorExpectation(
        long SubtotalAmd,
        long ServiceChargeAmd,
        long TotalAmd,
        long PaidAmd,
        long RemainingAmd,
        long AbsorbedFromRemovedAmd,
        IReadOnlyList<VectorShare> Shares);

    private sealed record VectorShare(
        Guid ParticipantId,
        long OwnItemsAmd,
        long SharedItemsAmd,
        long AbsorbedFromRemovedAmd,
        long PersonalAmd,
        long ServiceChargeAmd,
        long ShareAmd,
        long PaidAmd);
}
