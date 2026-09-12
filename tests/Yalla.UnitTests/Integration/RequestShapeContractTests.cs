using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Every request body this suite posts is checked against the record it is posted to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this exists for passes its own test.</b> <c>VenueLifecycleTests</c> posted
/// <c>localDate</c> and <c>localTime</c> to a record declaring <c>Date</c> and <c>Time</c>. Those
/// names bind to nothing and were silently discarded, and the test still passed - because the venue
/// gate answered before either field was read. It proved its 409 for years while proving nothing at
/// all about the booking, and only surfaced when those two fields became guarded.
/// </para>
/// <para>
/// That is the same shape as the <c>setSettlementMode</c> bug a client's gateway check found: a
/// caller sending field names the server does not have, and nothing noticing. A test written that
/// way is worse than no test, because it reports confidence it has not earned.
/// </para>
/// <para>
/// <b>Why this is a test and not a script.</b> A step in <c>verify.sh</c> would run on a developer's
/// machine and nowhere else - the CI workflow does not call that script. A test runs in both, so
/// this actually gates the merge.
/// </para>
/// <para>
/// <b>Why it reads the real endpoint table rather than parsing source.</b> The first version of
/// this check was a script that parsed the request records out of the C# with a regular expression.
/// A <c>//</c> comment inside a record's parameter list broke the parser, the record came out with
/// no properties, and the check <i>silently skipped it</i> - so it reported zero problems while
/// being unable to see the one it was written to find. Routes and body types now come from
/// <see cref="EndpointDataSource"/> and reflection, which cannot drift from what the app actually
/// serves, and anything still unresolvable <b>fails</b> rather than being passed over.
/// </para>
/// </remarks>
public class RequestShapeContractTests
{
    /// <summary>
    /// Bodies that deliberately do not match, each with the reason it is deliberate.
    /// </summary>
    /// <remarks>
    /// Declared rather than tolerated. An intentional wrong shape and an accidental one look
    /// identical to this check, so the difference has to be written down by the person who knows.
    /// A reason is required, and an entry that no longer matches anything fails too - an allowlist
    /// nobody prunes is how a real mismatch eventually hides behind a stale line.
    /// <para>
    /// Keyed on file, URL and the offending keys rather than a line number, so it survives edits
    /// above it.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<DeliberateKey, string> Deliberate = new()
    {
        [new DeliberateKey(
            "PlatformEndpointTests.cs",
            "/api/platform/venues",
            "address,floorHeight,floorWidth,timeZoneId")] =
            "Asserts that the branch fields sent flat, rather than nested under firstBranch, are "
            + "refused. Sending the wrong shape is the whole point of that case.",

        // The scanner's own fixture, below. It is a sample of a badly-written call held in a raw
        // string literal, so the scanner reads it exactly as it would read a real one - which is
        // the proof that it works. Declared here rather than by excluding this file from the scan,
        // because an exclusion would also hide a genuine mistake made in it later.
        [new DeliberateKey(
            "RequestShapeContractTests.cs",
            "/api/reservations",
            "localDate,localTime,nested")] =
            "The fixture for The_scanner_reads_keys_out_of_a_body_written_the_way_these_tests_write_one. "
            + "It is the original VenueLifecycleTests body, kept verbatim so the scanner is proved "
            + "against the real bug rather than a tidied-up imitation of it.",
    };

    private sealed record DeliberateKey(string File, string Url, string Keys);

    /// <summary>
    /// No test posts a field the endpoint's request record does not declare.
    /// </summary>
    [Fact]
    public void No_test_posts_a_field_its_endpoint_does_not_declare()
    {
        var endpoints = RealEndpoints();
        var calls = TestCalls().ToList();

        // A check that found nothing because it looked at nothing is the failure mode being
        // guarded against, so the sample size is asserted rather than assumed.
        Assert.True(
            calls.Count >= 100,
            $"Only {calls.Count} request bodies were found in the test suite. This check scans the "
            + "test sources; if that scan breaks it will report success while examining almost "
            + "nothing, which is exactly how the bug it exists for survived.");

        var problems = new List<string>();
        var matched = new HashSet<DeliberateKey>();

        foreach (var call in calls)
        {
            var body = Resolve(endpoints, call);

            if (body is null)
            {
                // Loudly, not silently. An unresolvable route means this check cannot see that
                // endpoint at all, which is indistinguishable from it being fine.
                problems.Add(
                    $"{call.File}:{call.Line} — could not resolve {call.Method} {call.Url} to an "
                    + "endpoint with a request body. Teach this check about it rather than leaving "
                    + "it unchecked.");

                continue;
            }

            var declared = body
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var unknown = call.Keys
                .Where(k => !declared.Contains(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            if (unknown.Length == 0)
            {
                continue;
            }

            var key = new DeliberateKey(call.File, call.Url, string.Join(",", unknown));

            if (Deliberate.TryGetValue(key, out var reason))
            {
                matched.Add(key);

                Assert.False(
                    string.IsNullOrWhiteSpace(reason),
                    $"The allowlist entry for {key.File} {key.Url} has no reason.");

                continue;
            }

            problems.Add(
                $"{call.File}:{call.Line} — {call.Method} {call.Url} posts [{string.Join(", ", unknown)}], "
                + $"which {body.Name} does not declare. It declares [{string.Join(", ", declared.Order())}]. "
                + "Those keys bind to nothing and are discarded, so the test may be passing for a "
                + "reason it does not state. If the wrong shape is deliberate, add it to Deliberate "
                + "with the reason.");
        }

        // A stale entry is a lie about the suite, so it fails like any other mismatch.
        foreach (var stale in Deliberate.Keys.Except(matched))
        {
            problems.Add(
                $"The allowlist entry for {stale.File} {stale.Url} [{stale.Keys}] no longer matches "
                + "any request body. Delete it - an allowlist nobody prunes is where a real "
                + "mismatch eventually hides.");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine + Environment.NewLine, problems));
    }

    /// <summary>
    /// The check can still detect the bug it was written for.
    /// </summary>
    /// <remarks>
    /// This is in the file on purpose. The first version of this check reported zero problems while
    /// being structurally incapable of finding any, and nothing said so. Feeding it the original
    /// <c>VenueLifecycleTests</c> body - <c>localDate</c>/<c>localTime</c> against a record
    /// declaring <c>Date</c>/<c>Time</c> - proves the comparison still bites, so the version that
    /// quietly reports success cannot be committed.
    /// </remarks>
    [Fact]
    public void The_check_still_detects_the_bug_it_was_written_for()
    {
        var declared = typeof(Yalla.Api.Endpoints.CreateReservationRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The record must be readable at all - the original failure was parsing it into nothing.
        Assert.Contains("date", declared);
        Assert.Contains("time", declared);
        Assert.True(declared.Count >= 8, "CreateReservationRequest resolved to too few properties.");

        // And the names the old test actually sent must be seen as unknown.
        Assert.DoesNotContain("localDate", declared);
        Assert.DoesNotContain("localTime", declared);
    }

    /// <summary>The scanner finds keys in a body written the way these tests write them.</summary>
    /// <remarks>
    /// The other half of the self-check. The comparison biting is no use if the scanner that feeds
    /// it returns an empty list - which is how a regex-based predecessor managed to pass.
    /// </remarks>
    [Fact]
    public void The_scanner_reads_keys_out_of_a_body_written_the_way_these_tests_write_one()
    {
        const string source = """
                var booking = await diner.PostAsJsonAsync("/api/reservations", new
                {
                    branchId = branch.BranchId,

                    // A comment, which broke the parser this replaced.
                    localDate = DateOnly.FromDateTime(local).ToString("yyyy-MM-dd"),
                    localTime = "19:00",
                    nested = new { inner = 1, other = 2 },
                });
            """;

        var calls = ScanSource("Sample.cs", source).ToList();

        var call = Assert.Single(calls);
        Assert.Equal("/api/reservations", call.Url);
        Assert.Equal(new[] { "branchId", "localDate", "localTime", "nested" }, call.Keys);
    }

    // ------------------------------------------------------------ the machinery

    private sealed record Call(string File, int Line, string Method, string Url, string[] Keys);

    private sealed record BodyEndpoint(string Method, string Pattern, Type Body);

    /// <summary>
    /// Every endpoint that takes a request body, read from the app's own routing table.
    /// </summary>
    private static IReadOnlyList<BodyEndpoint> RealEndpoints()
    {
        using var factory = new YallaApiFactory();

        // Forces the host to build without needing a database: the routing table is available as
        // soon as the app is composed.
        using var scope = factory.Services.CreateScope();

        var source = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();
        var found = new List<BodyEndpoint>();

        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>();
            var handler = endpoint.Metadata.OfType<MethodInfo>().FirstOrDefault();

            if (methods is null || handler is null)
            {
                continue;
            }

            var body = handler.GetParameters()
                .Select(p => p.ParameterType)
                .FirstOrDefault(IsRequestBody);

            if (body is null)
            {
                continue;
            }

            foreach (var verb in methods.HttpMethods)
            {
                found.Add(new BodyEndpoint(verb, endpoint.RoutePattern.RawText ?? string.Empty, body));
            }
        }

        return found;
    }

    /// <summary>
    /// Whether a handler parameter is the request body rather than a service or a route value.
    /// </summary>
    private static bool IsRequestBody(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type.IsInterface || type == typeof(string)
            || type == typeof(Guid) || type == typeof(CancellationToken))
        {
            return false;
        }

        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying.Namespace?.StartsWith("Yalla.", StringComparison.Ordinal) == true
               && underlying.IsClass;
    }

    private static Type? Resolve(IReadOnlyList<BodyEndpoint> endpoints, Call call)
    {
        var url = Normalise(call.Url);

        foreach (var endpoint in endpoints)
        {
            if (!string.Equals(endpoint.Method, call.Method, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Regex.IsMatch(url, Template(endpoint.Pattern)))
            {
                return endpoint.Body;
            }
        }

        return null;
    }

    /// <summary>A route pattern as a regex, with every parameter segment made a wildcard.</summary>
    private static string Template(string pattern)
    {
        var normalised = Normalise(pattern);
        var escaped = Regex.Escape(normalised).Replace("\\*", "[^/]+", StringComparison.Ordinal);

        return $"^{escaped}$";
    }

    /// <summary>
    /// A URL or route pattern reduced to a comparable shape: no query, parameters as <c>*</c>.
    /// </summary>
    private static string Normalise(string path)
    {
        var value = path.Split('?')[0];
        value = Regex.Replace(value, @"\{[^}]*\}", "*");

        return "/" + value.Trim('/').ToLowerInvariant();
    }

    /// <summary>Every JSON body this suite posts, with the keys it sets.</summary>
    private static IEnumerable<Call> TestCalls()
    {
        var root = TestSourceRoot();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            if (relative.StartsWith("bin/", StringComparison.Ordinal)
                || relative.StartsWith("obj/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var call in ScanSource(Path.GetFileName(file), File.ReadAllText(file)))
            {
                yield return call;
            }
        }
    }

    private static readonly Regex CallStart = new(
        @"\.(PostAsJsonAsync|PutAsJsonAsync|PatchAsJsonAsync)\(\s*\$?""(/api[^""]*)""\s*,\s*new\s*\{",
        RegexOptions.Compiled);

    /// <summary>
    /// Pulls the posted URL and the body's top-level keys out of one source file.
    /// </summary>
    private static IEnumerable<Call> ScanSource(string file, string source)
    {
        foreach (Match match in CallStart.Matches(source))
        {
            var verb = match.Groups[1].Value switch
            {
                "PostAsJsonAsync" => "POST",
                "PutAsJsonAsync" => "PUT",
                _ => "PATCH",
            };

            var open = source.IndexOf('{', match.Index + match.Length - 1);
            var depth = 0;
            var close = open;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                {
                    close = i;
                    break;
                }
            }

            var body = StripComments(source[(open + 1)..close]);
            var keys = TopLevelKeys(body);

            if (keys.Length == 0)
            {
                continue;
            }

            yield return new Call(
                file,
                source[..match.Index].Count(c => c == '\n') + 1,
                verb,
                match.Groups[2].Value,
                keys);
        }
    }

    /// <summary>Comments are stripped first: one inside a body broke the predecessor.</summary>
    private static string StripComments(string source) =>
        Regex.Replace(
            Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"//[^\n]*",
            string.Empty);

    private static string[] TopLevelKeys(string body)
    {
        var keys = new List<string>();
        var depth = 0;
        var current = new System.Text.StringBuilder();

        void Take()
        {
            var match = Regex.Match(current.ToString(), @"^\s*(\w+)\s*=");

            if (match.Success)
            {
                keys.Add(match.Groups[1].Value);
            }

            current.Clear();
        }

        foreach (var c in body)
        {
            if (c is '{' or '(' or '[') depth++;
            else if (c is '}' or ')' or ']') depth--;

            if (c == ',' && depth == 0)
            {
                Take();
            }
            else
            {
                current.Append(c);
            }
        }

        Take();

        return [.. keys];
    }

    /// <summary>
    /// The test project's source directory, found by walking up to the solution.
    /// </summary>
    private static string TestSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yalla.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException(
                "Could not find Yalla.sln above the test output directory, so the test sources "
                + "cannot be located.")
            : Path.Combine(directory.FullName, "tests", "Yalla.UnitTests");
    }
}
