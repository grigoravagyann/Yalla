using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The venue card's picture, and what a photo route answers when the bytes are gone.
/// </summary>
/// <remarks>
/// <para>
/// <c>Branch.CoverPhotoId</c> existed, the public page and the diner app read it, and the upload
/// route's own summary said "or a venue card" - but no route ever set it. It rides on the public
/// profile, because that form is "what we say to strangers on the internet", and the picture is
/// the first thing they see.
/// </para>
/// <para>
/// A photo row whose file is missing used to answer 500 with a stack trace, while a missing row
/// answered 404. Both are "no such picture" to a client, and the console shows a broken image for
/// the first and a placeholder for the second.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class BranchCoverPhotoTests(SqlServerFixture fixture) : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "yalla-cover-photo-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task A_manager_sets_their_own_branchs_photo_as_the_cover_and_reads_it_back()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        var photoId = await UploadedPhotoIdAsync(manager, mine.BranchId, Png(1));

        var saved = await manager.PutAsJsonAsync(
            $"/api/branches/{mine.BranchId}/public-profile",
            new { phoneE164 = "+37411223344", acceptsWebBookings = true, coverPhotoId = photoId });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(photoId, body.GetProperty("coverPhoto").GetProperty("photoId").GetGuid());
        Assert.Contains(photoId.ToString(), body.GetProperty("coverPhoto").GetProperty("cardUrl").GetString());

        // Read back, not just echoed.
        var read = await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{mine.BranchId}/public-profile");
        Assert.Equal(photoId, read.GetProperty("coverPhoto").GetProperty("photoId").GetGuid());

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal(photoId, (await db.Branches.FirstAsync(b => b.Id == mine.BranchId)).CoverPhotoId);
        }

        // Null clears it, the same way it clears the phone.
        var cleared = await manager.PutAsJsonAsync(
            $"/api/branches/{mine.BranchId}/public-profile",
            new { phoneE164 = "+37411223344", acceptsWebBookings = true, coverPhotoId = (Guid?)null });

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        // The API omits null fields rather than writing `null` (docs/public-surface.md), so a
        // cleared picture is a key that is not there at all.
        var clearedBody = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(
            clearedBody.TryGetProperty("coverPhoto", out var stillThere) && stillThere.ValueKind != JsonValueKind.Null,
            "coverPhoto should be absent or null after clearing");
    }

    [SkippableFact]
    public async Task Another_venues_photo_is_not_found_at_this_branch_and_nothing_changes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        AuthBranch theirs;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            theirs = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        using var neighbour = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, theirs));
        var theirPhoto = await UploadedPhotoIdAsync(neighbour, theirs.BranchId, Png(2));

        var refused = await manager.PutAsJsonAsync(
            $"/api/branches/{mine.BranchId}/public-profile",
            new { phoneE164 = (string?)null, acceptsWebBookings = false, coverPhotoId = theirPhoto });

        // The same answer a foreign category or menu photo gets: simply not found here.
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Null((await db.Branches.FirstAsync(b => b.Id == mine.BranchId)).CoverPhotoId);
        }
    }

    [SkippableFact]
    public async Task A_photo_whose_file_is_gone_answers_not_found_rather_than_a_server_error()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        var upload = await UploadAsync(manager, mine.BranchId, Png(3));
        var cardUrl = (await upload.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("photo").GetProperty("cardUrl").GetString()!;

        using var anyone = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anyone.GetAsync(cardUrl)).StatusCode);

        // The bytes vanish underneath the row - a disk restored from an older backup, a seed row
        // that never had any. The row is still there; the picture is not.
        foreach (var file in Directory.EnumerateFiles(root, "*.webp", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        var gone = await anyone.GetAsync(cardUrl);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    // ------------------------------------------------------------ helpers

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("PhotoStorage:RootPath", root);

    private static async Task<Guid> UploadedPhotoIdAsync(HttpClient client, Guid branchId, byte[] bytes)
    {
        var response = await UploadAsync(client, branchId, bytes);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("photo").GetProperty("photoId").GetGuid();
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid branchId, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "cover.png");

        return await client.PostAsync($"/api/branches/{branchId}/photos", form);
    }

    /// <summary>A small PNG, different per seed so no two uploads deduplicate into one row.</summary>
    private static byte[] Png(byte seed)
    {
        using var bitmap = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(seed, (byte)(255 - seed), 128));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
