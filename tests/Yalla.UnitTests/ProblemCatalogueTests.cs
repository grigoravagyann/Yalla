using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yalla.Api.Errors;
using Yalla.Domain;
using Yalla.Domain.Identity;
using Yalla.Domain.Media;
using Yalla.Domain.Messages;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Takeaway;

namespace Yalla.UnitTests;

/// <summary>
/// Holds the codebase to <see cref="ProblemCatalogue"/>, the one declaration of every problem code.
/// </summary>
/// <remarks>
/// <para>
/// A code is declared once, on its <see cref="ErrorCodes"/> constant, with its status, title, meaning and
/// context. These tests are what make that the only place: a code thrown anywhere that is not declared
/// fails here, so does a declared code nothing sends, an exception the mapper answers with another status
/// or a context member the declaration does not list, and a <c>docs/error-contract.md</c> table that no
/// longer matches.
/// </para>
/// <para>
/// "Thrown anywhere" is read three ways, because codes reach the wire three ways: an
/// <see cref="ErrorCodes"/> constant named in the Api (the mapper, the challenge, the rate limiter); a
/// slug constant on an exception type (the booking rules, <c>ErrorCode</c> on each refusal); and a slug
/// handed to <see cref="AuthenticationFailedException"/>, as a literal or a constant, anywhere in
/// <c>src/</c>.
/// </para>
/// </remarks>
public sealed partial class ProblemCatalogueTests
{
    /// <summary>
    /// Declared for clients and never sent: a route refusing a missing token answers a bare 401 with no
    /// body. Kept because it is published, and a published slug is never withdrawn.
    /// </summary>
    private static readonly HashSet<string> DeclaredButNeverSent = new(StringComparer.Ordinal)
    {
        ErrorCodes.Unauthenticated,
    };

    /// <summary>Exception types whose slug constants are values of a context member, not codes.</summary>
    private static readonly Dictionary<Type, string> SlugsThatAreNotCodes = new()
    {
        [typeof(Yalla.Application.Activity.TransferTargetIneligibleException)] = "its context.reason values",
    };

    private static readonly Assembly[] YallaAssemblies =
    [
        typeof(ErrorCodes).Assembly,
        typeof(DomainStateException).Assembly,
        typeof(Yalla.Application.Abstractions.IClock).Assembly,
        typeof(Yalla.Infrastructure.Persistence.YallaDbContext).Assembly,
    ];

    /// <summary>The exceptions outside our assemblies the mapper has an arm for.</summary>
    private static readonly Type[] FrameworkExceptions =
    [
        typeof(Exception),
        typeof(ArgumentException),
        typeof(ArgumentNullException),
        typeof(ArgumentOutOfRangeException),
        typeof(KeyNotFoundException),
        typeof(UnauthorizedAccessException),
        typeof(InvalidOperationException),
        typeof(Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException),
        typeof(Microsoft.AspNetCore.Http.BadHttpRequestException),
    ];

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Every_ErrorCodes_constant_is_a_catalogued_code_but_the_family_prefix()
    {
        var undeclared = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name != nameof(ErrorCodes.ReservationRejectedPrefix))
            .Where(f => f.GetCustomAttribute<ProblemAttribute>() is null)
            .Select(f => f.Name)
            .ToList();

        Assert.True(undeclared.Count == 0, "No [Problem] on: " + string.Join(", ", undeclared));
        Assert.Null(ProblemCatalogue.Find(ErrorCodes.ReservationRejectedPrefix));
    }

    [Fact]
    public void Every_code_is_a_kebab_case_slug_with_a_known_status_and_unique_context_members()
    {
        foreach (var definition in ProblemCatalogue.All)
        {
            Assert.Matches(CodeShape(), definition.Code);
            Assert.InRange(definition.Status, 400, 599);
            Assert.False(string.IsNullOrWhiteSpace(definition.Meaning), definition.Code);
            Assert.DoesNotContain('\n', definition.Meaning);
            Assert.Equal(definition.Title, ErrorCodes.TitleFor(definition.Code));

            var names = definition.Context.Select(c => c.Name).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void Every_code_thrown_anywhere_is_in_the_catalogue()
    {
        var missing = SentCodes()
            .Where(sent => ProblemCatalogue.Find(sent.Code) is null)
            .Select(sent => $"{sent.Code} ({sent.Where})")
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Declare these in ErrorCodes with [Problem] (status, meaning, context):\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_catalogue_code_reaches_the_wire()
    {
        var sent = SentCodes().Select(s => s.Code).ToHashSet(StringComparer.Ordinal);

        var unsent = ProblemCatalogue.All
            .Select(d => d.Code)
            .Where(code => !sent.Contains(code) && !DeclaredButNeverSent.Contains(code))
            .ToList();

        Assert.True(
            unsent.Count == 0,
            "Catalogued but nothing sends them - map them, or list them in DeclaredButNeverSent with a reason:\n"
            + string.Join("\n", unsent));

        Assert.All(DeclaredButNeverSent, code => Assert.DoesNotContain(code, sent));
    }

    [Fact]
    public void Each_exception_maps_to_its_codes_catalogued_status_and_declared_context()
    {
        var failures = new List<string>();

        foreach (var (label, exception) in Samples())
        {
            var mapped = ApiExceptionMapper.Map(exception);

            // A family exception made without its constructor carries no code; its codes are
            // checked against the family's status below.
            if (mapped.Code is null)
            {
                continue;
            }

            if (ProblemCatalogue.Find(mapped.Code) is not { } definition)
            {
                failures.Add($"{label}: {mapped.Code} is not catalogued");
                continue;
            }

            if (definition.Status != mapped.Status)
            {
                failures.Add($"{label}: {mapped.Code} answered {mapped.Status}, catalogued {definition.Status}");
            }

            if (mapped.Context is { } context)
            {
                failures.AddRange(ContextProblems(label, definition, context));
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void A_code_its_exception_mints_has_its_familys_status()
    {
        var failures = new List<string>();

        // Slug constants on exception types: a family's codes (null code when made bare) share the
        // family's status; a refusal with its own arm answers with its own constant.
        foreach (var type in ExceptionTypes().Where(t => !t.IsAbstract))
        {
            var codes = SlugConstantsOn(type).ToList();

            if (codes.Count == 0)
            {
                continue;
            }

            MappedError bare;

            try
            {
                bare = ApiExceptionMapper.Map((Exception)RuntimeHelpers.GetUninitializedObject(type));
            }
            catch (Exception) when (type.GetConstructors().Length > 0)
            {
                // An arm that reads a collection the bare object lacks; its hand-made sample in
                // Samples() is checked by the mapping test instead.
                continue;
            }

            foreach (var code in codes)
            {
                var definition = ProblemCatalogue.Find(code);

                if (bare.Code is null)
                {
                    if (definition?.Status != bare.Status)
                    {
                        failures.Add($"{type.Name}.{code}: catalogued {definition?.Status}, the family answers {bare.Status}");
                    }
                }
                else if (codes.Count == 1 && bare.Code != code)
                {
                    failures.Add($"{type.Name} declares {code} but is answered with {bare.Code}");
                }
            }
        }

        // Reasons handed to AuthenticationFailedException by a throw: the general arm's status.
        var signInStatus = ApiExceptionMapper.Map(
            (Exception)RuntimeHelpers.GetUninitializedObject(typeof(AuthenticationFailedException))).Status;

        foreach (var sent in AuthenticationReasons().Where(r => r.Thrown))
        {
            if (ProblemCatalogue.Find(sent.Code)?.Status != signInStatus)
            {
                failures.Add($"{sent.Code} ({sent.Where}) is thrown as a sign-in failure ({signInStatus})");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void The_error_contract_carries_the_catalogue_table()
    {
        const string begin = "<!-- problem-codes:begin -->";
        const string end = "<!-- problem-codes:end -->";

        var path = Path.Combine(RepositoryRoot(), "docs", "error-contract.md");
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var from = text.IndexOf(begin, StringComparison.Ordinal);
        var to = text.IndexOf(end, StringComparison.Ordinal);

        Assert.True(from >= 0 && to > from, $"docs/error-contract.md has lost its {begin} ... {end} markers.");

        var current = text[(from + begin.Length)..to];
        var expected = "\n" + CatalogueTable();

        if (current == expected)
        {
            return;
        }

        if (Environment.GetEnvironmentVariable("YALLA_UPDATE_DOCS") == "1")
        {
            File.WriteAllText(path, text[..(from + begin.Length)] + expected + text[to..]);
            return;
        }

        Assert.Fail(
            "docs/error-contract.md's catalogue table is stale. Regenerate it: "
            + "YALLA_UPDATE_DOCS=1 dotnet test tests/Yalla.UnitTests --filter \"FullyQualifiedName~ProblemCatalogueTests\"");
    }

    /// <summary>The generated table, as it sits between the markers.</summary>
    internal static string CatalogueTable()
    {
        var table = new StringBuilder();
        table.Append("| Code | Status | Title | Meaning | `context` |\n");
        table.Append("|---|---|---|---|---|\n");

        foreach (var definition in ProblemCatalogue.All)
        {
            var context = definition.Context.Count == 0
                ? "-"
                : string.Join(", ", definition.Context.Select(field =>
                    $"`{field.Name}` {ProblemCatalogue.TypeLabel(field.Type)}"
                    + (field.Required ? string.Empty : " (optional)")
                    + (field.Nullable ? " (nullable)" : string.Empty)));

            table.Append(
                $"| `{definition.Code}` | {definition.Status} | {Cell(definition.Title)} | {Cell(definition.Meaning)} | {context} |\n");
        }

        return table.ToString();

        static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // What reaches the wire
    // -----------------------------------------------------------------------------------------

    private sealed record SentCode(string Code, string Where, bool Thrown = false);

    private static IEnumerable<SentCode> SentCodes()
    {
        // 1. ErrorCodes constants the Api names anywhere but in ErrorCodes itself.
        var api = Path.Combine(RepositoryRoot(), "src", "Yalla.Api");

        foreach (var file in Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) is "ErrorCodes.cs" || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in ErrorCodesReference().Matches(File.ReadAllText(file)))
            {
                if (typeof(ErrorCodes).GetField(match.Groups["name"].Value) is { IsLiteral: true } field
                    && field.GetRawConstantValue() is string code
                    && field.Name != nameof(ErrorCodes.ReservationRejectedPrefix))
                {
                    yield return new SentCode(code, Path.GetFileName(file));
                }
            }
        }

        // 2. Slug constants on exception types.
        foreach (var type in ExceptionTypes())
        {
            foreach (var code in SlugConstantsOn(type))
            {
                yield return new SentCode(code, type.Name);
            }
        }

        // 3. Reasons handed to AuthenticationFailedException.
        foreach (var reason in AuthenticationReasons())
        {
            yield return reason;
        }
    }

    private static IEnumerable<SentCode> AuthenticationReasons()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var constants = YallaAssemblies.SelectMany(TypesOf)
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (Key: $"{t.Name}.{f.Name}", Value: (string)f.GetRawConstantValue()!)))
            .GroupBy(c => c.Key)
            .Where(g => g.Select(c => c.Value).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Value);

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);

            foreach (var (regex, thrown) in new[] { (ThrownReason(), true), (BaseReason(), false) })
            {
                foreach (Match match in regex.Matches(text))
                {
                    var argument = match.Groups["arg"].Value;
                    var where = $"{Path.GetFileName(file)}:{text[..match.Index].Count(c => c == '\n') + 1}";

                    if (argument.StartsWith('"'))
                    {
                        yield return new SentCode(argument.Trim('"'), where, thrown);
                    }
                    else if (constants.TryGetValue(argument, out var value))
                    {
                        yield return new SentCode(value, where, thrown);
                    }
                    else
                    {
                        // A reason this test cannot read is a reason nobody can check. Pass a literal or
                        // a constant.
                        throw new InvalidOperationException(
                            $"{where}: cannot tell which code AuthenticationFailedException({argument}, ...) sends.");
                    }
                }
            }
        }
    }

    private static IEnumerable<string> SlugConstantsOn(Type type) =>
        SlugsThatAreNotCodes.ContainsKey(type)
            ? []
            : type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(value => Slug().IsMatch(value));

    private static IEnumerable<Type> ExceptionTypes() =>
        YallaAssemblies.SelectMany(TypesOf)
            .Where(t => typeof(Exception).IsAssignableFrom(t) && !t.ContainsGenericParameters)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

    // -----------------------------------------------------------------------------------------
    // Mapping samples and the context check
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Every exception type, made without its constructor (the default path of each arm), plus real
    /// ones for the arms whose context changes with the facts.
    /// </summary>
    private static IEnumerable<(string Label, Exception Exception)> Samples()
    {
        var now = new DateTime(2026, 10, 25, 18, 0, 0, DateTimeKind.Utc);

        var hand = new List<(string, Exception)>
        {
            ("FieldValidationException", new FieldValidationException(
                [new FieldViolation("turnTimeMinutes", "Too short.", FieldBounds.Min, 15, 360, 5)])),
            ("PreOrderItemsUnavailableException", new PreOrderItemsUnavailableException(
                [(Guid.NewGuid(), PreOrderItemRefusal.SoldOut)])),
            ("TakeawayItemsUnavailableException", new TakeawayItemsUnavailableException(
                [(Guid.NewGuid(), TakeawayItemRefusal.NotForTakeaway)])),
            ("TakeawayClosedException (opens again)", new TakeawayClosedException(Guid.NewGuid(), now, "10:00")),
            ("GuestAlreadyAnsweredException (late)", new GuestAlreadyAnsweredException(Guid.NewGuid(), now, 15)),
            ("MessagesTooFastException (wait)", new MessagesTooFastException(Guid.NewGuid(), now.AddSeconds(30), now)),
            ("TooManyAttemptsException (wait)", new TooManyAttemptsException("Wait.", TimeSpan.FromSeconds(42))),
            ("UnsupportedImageException (format)", new UnsupportedImageException("Not a photo.", "gif")),
            ("PhoneHasActiveBookingException (held)", new PhoneHasActiveBookingException(
                new ActiveBookingFacts(Guid.NewGuid(), "K7Q2LM", now, new DateOnly(2026, 10, 25), new TimeOnly(19, 30), "Aram", "Centre"),
                3)),
            ("ArgumentOutOfRangeException (field)", new ArgumentOutOfRangeException("partySize", 0, "Too few.")),
            ("ArgumentException (field)", new ArgumentException("Missing.", "clientCommandId")),
        };

        var handled = hand.Select(h => h.Item1).ToHashSet(StringComparer.Ordinal);

        foreach (var type in ExceptionTypes().Concat(FrameworkExceptions).Distinct().Where(t => !t.IsAbstract))
        {
            if (handled.Contains(type.Name))
            {
                continue;
            }

            yield return (type.Name, (Exception)RuntimeHelpers.GetUninitializedObject(type));
        }

        foreach (var sample in hand)
        {
            yield return sample;
        }
    }

    private static IEnumerable<string> ContextProblems(
        string label,
        ProblemDefinition definition,
        IReadOnlyDictionary<string, object?> context)
    {
        var declared = definition.Context.ToDictionary(c => c.Name, StringComparer.Ordinal);

        foreach (var key in context.Keys.Where(k => !declared.ContainsKey(k)))
        {
            yield return $"{label}: {definition.Code} sends context.{key}, which ErrorCodes.{definition.Name} does not declare";
        }

        foreach (var field in definition.Context.Where(f => f.Required && !context.ContainsKey(f.Name)))
        {
            yield return $"{label}: {definition.Code} leaves out context.{field.Name}, declared required";
        }

        foreach (var (key, value) in context)
        {
            if (!declared.TryGetValue(key, out var field))
            {
                continue;
            }

            if (value is null)
            {
                // Made without a constructor, a reference member is null whatever it is declared as;
                // a value type is null only when it really can be.
                if (field.Type.IsValueType && !field.Nullable)
                {
                    yield return $"{label}: {definition.Code} sends context.{key} = null, declared not nullable";
                }

                continue;
            }

            var element = JsonSerializer.SerializeToElement(value, Wire);

            if (ExpectedKind(field.Type) is { } kind && element.ValueKind != kind
                && !(kind == JsonValueKind.True && element.ValueKind == JsonValueKind.False))
            {
                yield return $"{label}: {definition.Code} sends context.{key} as {element.ValueKind}, declared {ProblemCatalogue.TypeLabel(field.Type)}";
            }

            // Items of an array of records carry only the record's members.
            if (element.ValueKind == JsonValueKind.Array
                && ProblemCatalogue.ElementTypeOf(field.Type) is { IsClass: true } item
                && item != typeof(string) && item != typeof(object))
            {
                var members = item.GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToHashSet();

                foreach (var name in element.EnumerateArray()
                             .Where(e => e.ValueKind == JsonValueKind.Object)
                             .SelectMany(e => e.EnumerateObject().Select(p => p.Name))
                             .Distinct()
                             .Where(name => !members.Contains(name)))
                {
                    yield return $"{label}: {definition.Code} sends context.{key}[].{name}, which {item.Name} does not declare";
                }
            }
        }
    }

    /// <summary>The JSON kind a member of this type is written as; null for any.</summary>
    private static JsonValueKind? ExpectedKind(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(object))
        {
            return null;
        }

        if (underlying == typeof(string) || underlying == typeof(Guid) || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset) || underlying == typeof(DateOnly) || underlying == typeof(TimeOnly))
        {
            return JsonValueKind.String;
        }

        if (underlying == typeof(bool))
        {
            return JsonValueKind.True;
        }

        if (underlying.IsEnum || underlying == typeof(int) || underlying == typeof(long))
        {
            return JsonValueKind.Number;
        }

        return ProblemCatalogue.ElementTypeOf(underlying) is not null ? JsonValueKind.Array : JsonValueKind.Object;
    }

    // -----------------------------------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------------------------------

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }

    /// <summary>The folder holding Yalla.sln, found by walking up from the test output.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yalla.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Yalla.sln not found above the test output.");
    }

    /// <summary>A code: kebab-case words.</summary>
    [GeneratedRegex("^[a-z]+(-[a-z]+)*$")]
    private static partial Regex CodeShape();

    /// <summary>A constant that looks like a code: two words or more (one word is too often something else).</summary>
    [GeneratedRegex("^[a-z]+(-[a-z]+)+$")]
    private static partial Regex Slug();

    [GeneratedRegex(@"\bErrorCodes\.(?<name>[A-Z]\w*)")]
    private static partial Regex ErrorCodesReference();

    /// <summary><c>new AuthenticationFailedException(arg</c>, or a factory <c>AuthenticationFailedException X() =&gt; new(arg</c>.</summary>
    [GeneratedRegex(@"(?:\bnew\s+AuthenticationFailedException|\bAuthenticationFailedException\s+\w+\s*(?:\(\s*\))?\s*(?:=>|=)\s*new)\s*\(\s*(?<arg>""[^""]*""|[A-Za-z_][\w.]*)")]
    private static partial Regex ThrownReason();

    /// <summary>A subclass's base call, <c>: AuthenticationFailedException(arg</c>.</summary>
    [GeneratedRegex(@":\s*AuthenticationFailedException\s*\(\s*(?<arg>""[^""]*""|[A-Za-z_][\w.]*)")]
    private static partial Regex BaseReason();
}
