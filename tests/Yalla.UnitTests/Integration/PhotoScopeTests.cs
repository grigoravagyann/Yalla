using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using Yalla.Application.Menus;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Who may upload a photo to a branch, and whose photo a menu item may carry.
/// </summary>
/// <remarks>
/// <para>
/// The upload route is addressed by branch and is <c>BranchScoped</c> like every other admin
/// route that is; this proves the policy is on the route, through the real pipeline, because
/// "the policy was never applied" is the failure no service test can see. The service checks the
/// stored staff row as well, so a deactivated account whose token has not yet expired is refused
/// here too - the token still says Manager, the row no longer does.
/// </para>
/// <para>
/// The photo id on a menu item is caller-supplied and not secret, so the menu service refuses one
/// that was uploaded for another branch, the way it refuses a category from another branch.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class PhotoScopeTests(SqlServerFixture fixture) : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc);

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "yalla-photo-scope-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------ the route

    [SkippableFact]
    public async Task A_manager_uploads_to_their_own_branch_and_is_refused_at_another_venues()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch mine;
        AuthBranch theirs;
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            mine = await AuthTestData.CreateBranchAsync(db);
            theirs = await AuthTestData.CreateBranchAsync(db);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, mine));
        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));

        // Their own branch: still works.
        var own = await UploadAsync(manager, mine.BranchId, Png(1));
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);

        // A branch of the neighbouring venue: the token is a manager's, and that is not enough.
        var foreign = await UploadAsync(manager, theirs.BranchId, Png(2));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

        // And nothing was written for it - a refusal that stored the file would be no refusal.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.False(await db.Photos.AnyAsync(p => p.BranchId == theirs.BranchId));
            Assert.True(await db.Photos.AnyAsync(p => p.BranchId == mine.BranchId));
        }

        // The platform tier belongs to no venue and passes for every branch.
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(platform, mine.BranchId, Png(3))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(platform, theirs.BranchId, Png(4))).StatusCode);
    }

    [SkippableFact]
    public async Task A_deactivated_managers_still_valid_token_cannot_upload()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        // Signed in while active; the access token outlives what happens next.
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(manager, branch.BranchId, Png(5))).StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.StaffMembers.FirstAsync(s => s.Id == branch.ManagerId);
            row.SetActive(false);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(manager, branch.BranchId, Png(6))).StatusCode);
    }

    // ------------------------------------------------------------ attaching

    [SkippableFact]
    public async Task A_menu_item_may_carry_its_own_branchs_photo_and_not_another_venues()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var db = fixture.CreateContext(new TestClock(Now));
        var mine = await TestBranchBuilder.CreateAsync(db);
        var theirs = await TestBranchBuilder.CreateAsync(db);
        var myPhoto = await TestMenuBuilder.AddPhotoAsync(db, mine.BranchId);
        var theirPhoto = await TestMenuBuilder.AddPhotoAsync(db, theirs.BranchId);

        var menu = fixture.CreateMenuService(db);
        var category = await menu.CreateCategoryAsync(mine.BranchId, new CreateMenuCategoryCommand("Mains"));

        // Own photo: attached, on create and on update.
        var item = await menu.CreateItemAsync(
            mine.BranchId, category.Id, new CreateMenuItemCommand("Khachapuri", 3_200L, PhotoId: myPhoto));

        Assert.Equal(myPhoto, item.Photo?.PhotoId);

        var edited = await menu.UpdateItemAsync(
            mine.BranchId, item.Id, new UpdateMenuItemCommand(PhotoId: myPhoto));

        Assert.Equal(myPhoto, edited.Photo?.PhotoId);

        // The neighbour's photo: not found at this branch, the same answer their category gets.
        var refusedOnCreate = await Assert.ThrowsAsync<KeyNotFoundException>(() => menu.CreateItemAsync(
            mine.BranchId, category.Id, new CreateMenuItemCommand("Lahmajun", 900L, PhotoId: theirPhoto)));

        Assert.Contains(theirPhoto.ToString(), refusedOnCreate.Message);

        var refusedOnUpdate = await Assert.ThrowsAsync<KeyNotFoundException>(() => menu.UpdateItemAsync(
            mine.BranchId, item.Id, new UpdateMenuItemCommand(PhotoId: theirPhoto)));

        Assert.Contains(theirPhoto.ToString(), refusedOnUpdate.Message);

        // And the item still carries what it had.
        db.ChangeTracker.Clear();
        Assert.Equal(myPhoto, (await db.MenuItems.FirstAsync(i => i.Id == item.Id)).PhotoId);
        Assert.False(await db.MenuItems.AnyAsync(i => i.PhotoId == theirPhoto));
    }

    // ------------------------------------------------------------ helpers

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("PhotoStorage:RootPath", root);

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid branchId, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "dish.png");

        return await client.PostAsync($"/api/branches/{branchId}/photos", form);
    }

    /// <summary>A small PNG, different per seed so no two uploads deduplicate into one row.</summary>
    private static byte[] Png(byte seed)
    {
        using var bitmap = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(seed, (byte)(seed * 7), (byte)(seed * 13)));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);

        return png.ToArray();
    }
}
