using System.Net;
using System.Text.Json;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Whether Swagger is reachable, and whether the document it serves is fit for generating a
/// TypeScript client from.
/// </summary>
/// <remarks>
/// These need no database: nothing in the request path for <c>/swagger</c> touches one.
/// </remarks>
public class SwaggerExposureTests
{
    private const string UiPath = "/swagger/index.html";
    private const string DocumentPath = "/swagger/v1/swagger.json";

    /// <summary>
    /// The environment here is Development - where Swagger is normally on - so this also proves
    /// the gate is the setting and not the environment name.
    /// </summary>
    [Fact]
    public async Task Swagger_is_404_when_the_setting_is_off()
    {
        await using var factory = new YallaApiFactory().WithSwagger(enabled: false);
        using var client = factory.CreateClient();

        var ui = await client.GetAsync(UiPath);
        var document = await client.GetAsync(DocumentPath);

        // 404, not 401 and not a redirect. Either of those would confirm Swagger is there, which
        // is the one thing a probe against a production ordering API must not learn.
        Assert.Equal(HttpStatusCode.NotFound, ui.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
    }

    [Fact]
    public async Task Swagger_is_served_when_the_setting_is_on()
    {
        await using var factory = new YallaApiFactory().WithSwagger(enabled: true);
        using var client = factory.CreateClient();

        var ui = await client.GetAsync(UiPath);
        var document = await client.GetAsync(DocumentPath);

        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
    }

    [Fact]
    public async Task The_ip_allowlist_refuses_an_address_that_is_not_on_it()
    {
        await using var factory = new YallaApiFactory().WithSwagger(enabled: true, "10.0.0.7");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, DocumentPath);
        request.Headers.Add(YallaApiFactory.RemoteAddressHeader, "10.0.0.8");

        var response = await client.SendAsync(request);

        // Again 404 rather than 403: a 403 says "this exists and you may not see it".
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_ip_allowlist_admits_an_address_that_is_on_it()
    {
        await using var factory = new YallaApiFactory().WithSwagger(enabled: true, "10.0.0.7");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, DocumentPath);
        request.Headers.Add(YallaApiFactory.RemoteAddressHeader, "10.0.0.7");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The frontend generates its function names from these. An auto-generated operation id
    /// produces something like <c>ApiBranchesBranchIdTablesTableIdSeatWalkInPost</c>, which is
    /// unusable, so every endpoint declares one explicitly with <c>WithName</c>.
    /// </summary>
    [Fact]
    public async Task Every_operation_has_an_explicit_camelCase_operation_id()
    {
        using var document = await GetDocumentAsync();

        var operationIds = new List<string>();
        var missing = new List<string>();

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!operation.Value.TryGetProperty("operationId", out var id)
                    || string.IsNullOrWhiteSpace(id.GetString()))
                {
                    missing.Add($"{operation.Name.ToUpperInvariant()} {path.Name}");
                    continue;
                }

                operationIds.Add(id.GetString()!);
            }
        }

        Assert.Empty(missing);
        Assert.NotEmpty(operationIds);

        // camelCase, and unique: a generated client turns each of these into a function name.
        Assert.All(operationIds, id => Assert.True(
            char.IsLower(id[0]) && id.All(char.IsLetterOrDigit),
            $"Operation id '{id}' is not camelCase."));

        Assert.Equal(operationIds.Count, operationIds.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The two responses the frontend has to treat as normal outcomes rather than failures.
    /// </summary>
    [Fact]
    public async Task Table_transitions_declare_their_409_and_422()
    {
        using var document = await GetDocumentAsync();

        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/branches/{branchId}/tables/{tableId}/seat-walk-in")
            .GetProperty("post");

        var responses = operation.GetProperty("responses");

        Assert.True(responses.TryGetProperty("409", out var conflict));
        Assert.True(responses.TryGetProperty("422", out var unprocessable));

        // Both carry the problem document, and both are described in the endpoint's own words -
        // "someone else took that table", not "Error".
        Assert.Contains("application/problem+json", conflict.GetProperty("content").EnumerateObject()
            .Select(c => c.Name));
        Assert.Contains(
            "Someone else changed this table first", conflict.GetProperty("description").GetString()!);
        Assert.Contains("state machine allows", unprocessable.GetProperty("description").GetString()!);
    }

    /// <summary>
    /// Enums are integers, matching the database - and each member is named in the schema, so the
    /// frontend gets <c>TableStatus.Occupied</c> rather than a magic 4.
    /// </summary>
    [Fact]
    public async Task Enums_are_integers_and_carry_their_member_names()
    {
        using var document = await GetDocumentAsync();

        var schema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("Yalla.Domain.Enums.TableStatus");

        Assert.Equal("integer", schema.GetProperty("type").GetString());

        var values = schema.GetProperty("enum").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        Assert.Equal([1, 2, 4, 5], values);

        // 3 was Reserved and is permanently retired. Its absence is part of the contract.
        Assert.DoesNotContain(3, values);

        var names = schema.GetProperty("x-enum-varnames").EnumerateArray()
            .Select(v => v.GetString())
            .ToArray();

        Assert.Equal(new[] { "Free", "Held", "Occupied", "OutOfService" }, names);
        Assert.Contains("4 Occupied", schema.GetProperty("description").GetString()!);
    }

    /// <summary>
    /// A non-nullable C# string must not become <c>string | null</c> in TypeScript, or the
    /// frontend writes null checks for values that cannot be null.
    /// </summary>
    [Fact]
    public async Task Non_nullable_reference_properties_are_required_and_not_nullable()
    {
        using var document = await GetDocumentAsync();

        var schema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("Yalla.Application.Auth.StaffSessionResult");

        var required = schema.GetProperty("required").EnumerateArray()
            .Select(v => v.GetString())
            .ToArray();

        Assert.Contains("accessToken", required);
        Assert.Contains("fullName", required);

        var accessToken = schema.GetProperty("properties").GetProperty("accessToken");
        Assert.False(accessToken.TryGetProperty("nullable", out var nullable) && nullable.GetBoolean());
    }

    /// <summary>The four surfaces the clients are built around.</summary>
    [Fact]
    public async Task Operations_are_tagged_with_the_surface_they_belong_to()
    {
        using var document = await GetDocumentAsync();

        var tags = document.RootElement
            .GetProperty("paths")
            .EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .Where(operation => operation.Value.TryGetProperty("tags", out _))
            .SelectMany(operation => operation.Value.GetProperty("tags").EnumerateArray())
            .Select(tag => tag.GetString())
            .Distinct()
            .ToArray();

        Assert.Contains("diner", tags);
        Assert.Contains("staff", tags);
        Assert.Contains("admin", tags);
        Assert.Contains("auth", tags);
    }

    /// <summary>Swagger UI needs this to have an Authorize button worth pressing.</summary>
    [Fact]
    public async Task The_document_declares_the_jwt_bearer_scheme()
    {
        using var document = await GetDocumentAsync();

        var scheme = document.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("bearer");

        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        Assert.Equal("JWT", scheme.GetProperty("bearerFormat").GetString());
    }

    // ------------------------------------------------------------ 22. typed error extensions

    /// <summary>
    /// <b>Test 22.</b> Every declared problem shape reaches the document with a real
    /// <c>context</c> object rather than a bag of unknowns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generic envelope's <c>context</c> is an <c>IReadOnlyDictionary&lt;string, object?&gt;</c>,
    /// which <c>openapi-typescript</c> emits as <c>Record&lt;string, unknown&gt;</c>. Every named
    /// error a client branches on was therefore stringly-typed on the other side, which is most of
    /// the argument for generating a client at all.
    /// </para>
    /// <para>
    /// This asserts the schema, not the runtime body - the two are checked against each other by the
    /// integration tests that read <c>context.remainingAmd</c> and friends off a real response.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_declared_problem_shape_has_a_typed_context_in_the_document()
    {
        using var document = await GetDocumentAsync();

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        // Shape name -> the field a client actually reads off it.
        var expected = new Dictionary<string, string[]>
        {
            ["MenuItemUnavailableProblem"] = ["itemName", "menuItemId"],
            ["TabNotAcceptingOrdersProblem"] = ["tabId", "status"],
            ["LineAlreadyPaidProblem"] = ["tabId", "lineId"],
            ["PaymentExceedsRemainingProblem"] = ["tabId", "requestedAmd", "remainingAmd"],
            ["ServiceRequestRateLimitedProblem"] = ["tabId", "limit", "windowMinutes"],
            ["LockTimeoutProblem"] = ["tableId", "tableLabel", "timeoutMilliseconds", "retryable"],
        };

        // Schema ids are fully qualified in this document, so match on the tail.
        JsonElement Find(string suffix)
        {
            foreach (var candidate in schemas.EnumerateObject())
            {
                if (candidate.Name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return candidate.Value;
                }
            }

            throw new Xunit.Sdk.XunitException(
                $"{suffix} is declared in ProblemShapes.cs but never reaches the document. "
                + "Some endpoint has to declare it with ProducesProblem<T>.");
        }

        foreach (var (shapeName, fields) in expected)
        {
            var properties = Find(shapeName).GetProperty("properties");

            // The standard envelope members are flattened onto the derived schema, so a generated
            // client gets one complete object rather than an intersection it has to unwrap.
            Assert.True(properties.TryGetProperty("code", out _), $"{shapeName} has no code.");
            Assert.True(properties.TryGetProperty("traceId", out _), $"{shapeName} has no traceId.");

            // And the part this test exists for: context is a named schema, not a free-form map.
            var context = properties.GetProperty("context");

            Assert.False(
                context.TryGetProperty("additionalProperties", out _),
                $"{shapeName}.context generated as a dictionary, which is the thing being fixed.");

            // Swashbuckle wraps the reference in a single-member allOf whenever the property also
            // carries a description, which these all do. Both forms are one named type, and
            // openapi-typescript resolves them identically.
            var reference = context.TryGetProperty("$ref", out var direct)
                ? direct.GetString()
                : context.GetProperty("allOf").EnumerateArray().First().GetProperty("$ref").GetString();

            Assert.NotNull(reference);

            var contextProperties = Find(reference!.Split('/')[^1]).GetProperty("properties");

            foreach (var field in fields)
            {
                Assert.True(
                    contextProperties.TryGetProperty(field, out _),
                    $"{shapeName}.context is missing {field}, which a client has to read.");
            }
        }

        // And where one status code can arrive in more than one form, the document says so with a
        // oneOf rather than silently documenting whichever Produces call came last.
        var placeOrder409 = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/tabs/{tabId}/orders")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("409")
            .GetProperty("content")
            .GetProperty("application/problem+json")
            .GetProperty("schema");

        var union = placeOrder409.GetProperty("oneOf").EnumerateArray()
            .Select(o => o.GetProperty("$ref").GetString()!)
            .ToList();

        Assert.Equal(2, union.Count);
        Assert.Contains(union, r => r.EndsWith("MenuItemUnavailableProblem", StringComparison.Ordinal));
        Assert.Contains(union, r => r.EndsWith("TabNotAcceptingOrdersProblem", StringComparison.Ordinal));
    }

    private static async Task<JsonDocument> GetDocumentAsync()
    {
        await using var factory = new YallaApiFactory().WithSwagger(enabled: true);
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync(DocumentPath);

        return JsonDocument.Parse(json);
    }
}
