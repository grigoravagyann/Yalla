using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SkiaSharp;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// K8: the <c>diner-write</c> policy - ten writes a minute per signed-in caller across reviews,
/// reports and photo uploads - fires at its threshold, and fires for one caller only.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class WriteRateLimitTests(SqlServerFixture fixture) : IDisposable
{
    private const int Limit = 10;

    private readonly string root = Path.Combine(Path.GetTempPath(), "yalla-write-limit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task The_eleventh_review_write_in_a_minute_is_refused_and_the_next_diner_is_not()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        var (busyToken, busyId) = await ReviewIntegrityTests.SignInDinerAsync(factory);
        var (quietToken, quietId) = await ReviewIntegrityTests.SignInDinerAsync(factory);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await ReviewTestData.SeedTabVisitAsync(db, branch, factory.Clock.UtcNow, busyId, quietId);
        }

        using var busy = factory.CreateClientWithToken(busyToken);
        using var quiet = factory.CreateClientWithToken(quietToken);
        var route = $"/api/diner/branches/{branch.BranchId}/review";

        for (var write = 0; write < Limit; write++)
        {
            var response = await busy.PutAsJsonAsync(route, new { rating = write % 5 + 1 });

            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
                $"Write {write + 1} answered {(int)response.StatusCode}.");
        }

        await ReviewIntegrityTests.AssertProblemAsync(
            await busy.PutAsJsonAsync(route, new { rating = 3 }), HttpStatusCode.TooManyRequests, "rate-limited");

        // The budget is per caller: the diner at the next table has spent nothing.
        Assert.Equal(HttpStatusCode.Created, (await quiet.PutAsJsonAsync(route, new { rating = 4 })).StatusCode);
    }

    [SkippableFact]
    public async Task The_eleventh_profile_photo_upload_in_a_minute_is_refused()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var diner = factory.CreateClientWithToken((await ReviewIntegrityTests.SignInDinerAsync(factory)).AccessToken);

        for (var upload = 0; upload < Limit; upload++)
        {
            var response = await UploadAsync(diner, Png((byte)(upload * 20)));

            Assert.True(response.StatusCode == HttpStatusCode.Created, $"Upload {upload + 1} answered {(int)response.StatusCode}.");
        }

        await ReviewIntegrityTests.AssertProblemAsync(
            await UploadAsync(diner, Png(250)), HttpStatusCode.TooManyRequests, "rate-limited");
    }

    [SkippableFact]
    public async Task Reviews_uploads_and_reports_spend_one_budget_and_the_next_diner_still_has_theirs()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        Guid reviewId;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            var authorId = await ReviewTestData.SeedDinerAsync(db, "Narek Petrosyan", factory.Clock.UtcNow);
            reviewId = await ReviewTestData.SeedReviewAsync(db, branch.BranchId, authorId, 2, "Slow.", factory.Clock.UtcNow);
        }

        var (busyToken, busyId) = await ReviewIntegrityTests.SignInDinerAsync(factory);
        var (quietToken, _) = await ReviewIntegrityTests.SignInDinerAsync(factory);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await ReviewTestData.SeedTabVisitAsync(db, branch, factory.Clock.UtcNow, busyId);
        }

        using var busy = factory.CreateClientWithToken(busyToken);
        using var quiet = factory.CreateClientWithToken(quietToken);
        var reviewRoute = $"/api/diner/branches/{branch.BranchId}/review";
        var reportRoute = $"/api/diner/reviews/{reviewId}/report";

        // Half the budget on a review...
        for (var write = 0; write < Limit / 2; write++)
        {
            var response = await busy.PutAsJsonAsync(reviewRoute, new { rating = write % 5 + 1 });

            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
                $"Review write {write + 1} answered {(int)response.StatusCode}.");
        }

        // ...and the rest on pictures.
        for (var upload = 0; upload < Limit - Limit / 2; upload++)
        {
            var response = await UploadAsync(busy, Png((byte)(upload * 20)));

            Assert.True(response.StatusCode == HttpStatusCode.Created, $"Upload {upload + 1} answered {(int)response.StatusCode}.");
        }

        // A report is a write too, from the same budget, and there is nothing left of it.
        await ReviewIntegrityTests.AssertProblemAsync(
            await busy.PostAsJsonAsync(reportRoute, new { reason = "spam" }), HttpStatusCode.TooManyRequests, "rate-limited");

        // Per caller: the next diner reports the same review.
        Assert.Equal(HttpStatusCode.NoContent, (await quiet.PostAsJsonAsync(reportRoute, new { reason = "spam" })).StatusCode);
    }

    // ------------------------------------------------------------ helpers

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("PhotoStorage:RootPath", root)
            .With("RateLimiting:Enabled", "true")
            .With("RateLimiting:DinerWritePermitLimit", Limit.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .With("RateLimiting:DinerWriteWindowSeconds", "60")

            // Everything else out of the way: this is about the write budget and nothing else.
            .With("RateLimiting:GlobalPermitLimit", "1000")
            .With("RateLimiting:AuthPermitLimit", "1000")
            .With("RateLimiting:CodeRequestPermitLimit", "1000");

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "me.png");

        return await client.PostAsync("/api/diner/me/photo", form);
    }

    /// <summary>A small PNG, different per seed so no two uploads are the same bytes.</summary>
    private static byte[] Png(byte seed)
    {
        using var bitmap = new SKBitmap(32, 32);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(seed, (byte)(255 - seed), 90));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
