using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SkiaSharp;
using Yalla.Application.Media;
using Yalla.Infrastructure;
using Yalla.Infrastructure.Media;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The orphan photo sweep on its schedule, and where the photo folder is.
/// </summary>
/// <remarks>
/// Time moves by advancing a <see cref="FakeTimeProvider"/>; nothing waits an hour. The one test that
/// runs the hosted API waits only for the pass the advance set off to finish.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class PhotoSweepHostedServiceTests(SqlServerFixture fixture) : IDisposable
{
    private const string Password = "khachapuri-2026";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "yalla-photo-sweep-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------ the hosted API, end to end

    /// <summary>
    /// A diner replaces their picture; a day later the hosted sweep's next tick deletes the old one,
    /// row and file, and its URL answers 404 - while the current picture stays.
    /// </summary>
    [SkippableFact]
    public async Task A_replaced_picture_is_deleted_by_the_hosted_sweep_a_day_later()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await using var factory = new YallaApiFactory()
            .WithDatabase(fixture.ConnectionString)
            .With("PhotoStorage:RootPath", root)
            .With("PhotoStorage:SweepIntervalMinutes", "60")
            .WithServices(services => services.AddSingleton<TimeProvider>(time));

        using var anonymous = factory.CreateClient();
        using var me = factory.CreateClientWithToken(await RegisterAsync(anonymous));

        var replaced = await UploadAsync(me, Png(1));
        var current = await UploadAsync(me, Png(2));
        Assert.NotEqual(replaced, current);

        string replacedFile;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var row = await db.Photos.AsNoTracking().SingleAsync(p => p.Id == replaced);
            replacedFile = Path.Combine(root, row.FullPath.Replace('/', Path.DirectorySeparatorChar));
        }

        Assert.True(File.Exists(replacedFile));

        // Past the day of grace on the domain clock, then one interval on the timer.
        factory.Clock.Advance(TimeSpan.FromHours(25));
        time.Advance(TimeSpan.FromMinutes(60));

        await WaitUntilAsync(async () =>
        {
            await using var db = fixture.CreateContext(factory.Clock);

            return !await db.Photos.AnyAsync(p => p.Id == replaced);
        });

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/photos/{replaced}/full")).StatusCode);
        Assert.False(File.Exists(replacedFile), "The swept picture's file is still on disk.");

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/photos/{current}/full")).StatusCode);
    }

    // ------------------------------------------------------------ the loop

    /// <summary>
    /// Nothing before the first interval, one pass per interval after it, each in a scope of its own -
    /// and a pass that throws does not end the loop.
    /// </summary>
    [Fact]
    public async Task Each_interval_runs_one_pass_in_its_own_scope_and_a_failed_pass_does_not_stop_the_next()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var photos = new RecordingPhotoService(throwOnFirstCall: true);
        var scopes = 0;

        var services = new ServiceCollection();
        services.AddScoped<IPhotoService>(_ =>
        {
            Interlocked.Increment(ref scopes);
            return photos;
        });

        await using var provider = services.BuildServiceProvider();
        using var sweep = NewService(provider, time, intervalMinutes: 60);

        await sweep.StartAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(0, photos.Calls);

        time.Advance(TimeSpan.FromMinutes(1));
        await photos.WaitForCallAsync();

        time.Advance(TimeSpan.FromMinutes(60));
        await photos.WaitForCallAsync();

        Assert.Equal(2, photos.Calls);
        Assert.Equal(2, scopes);

        await sweep.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Zero_minutes_switches_the_sweep_off()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var photos = new RecordingPhotoService(throwOnFirstCall: false);

        var services = new ServiceCollection();
        services.AddScoped<IPhotoService>(_ => photos);

        await using var provider = services.BuildServiceProvider();
        using var sweep = NewService(provider, time, intervalMinutes: 0);

        await sweep.StartAsync(CancellationToken.None);

        // No loop at all: the background task ends by itself, so no advance can reach it.
        await sweep.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromDays(3));
        Assert.Equal(0, photos.Calls);

        await sweep.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void A_negative_interval_refuses_to_start()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PhotoStorage:SweepIntervalMinutes"] = "-5" })
            .Build();

        var failure = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddInfrastructure("Server=localhost;Database=Yalla_Unused", configuration));

        Assert.Contains("SweepIntervalMinutes", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hosted_api_registers_the_sweep()
    {
        using var factory = new YallaApiFactory();

        Assert.Contains(
            factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            s => s is PhotoSweepHostedService);

        Assert.Equal(
            PhotoSweepOptions.DefaultIntervalMinutes,
            factory.Services.GetRequiredService<PhotoSweepOptions>().SweepIntervalMinutes);
    }

    // ------------------------------------------------------------ the folder

    [Theory]
    [InlineData(null, "photos")]
    [InlineData("", "photos")]
    [InlineData("   ", "photos")]
    [InlineData("~/photos", "photos")]
    [InlineData("~\\photos", "photos")]
    [InlineData("~", "")]
    [InlineData("~/photos/nested", "photos/nested")]
    public void Tilde_and_blank_resolve_under_the_users_Yalla_folder(string? configured, string under)
    {
        var userData = Path.Combine(Path.GetTempPath(), "yalla-user-data");

        var expected = Path.GetFullPath(
            Path.Combine(userData, PhotoStorageRoot.ApplicationFolder, under.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Equal(expected, PhotoStorageRoot.Resolve(configured, userData));
    }

    [Fact]
    public void Any_other_path_is_taken_as_it_is()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "somewhere", "photos");

        Assert.Equal(Path.GetFullPath(absolute), PhotoStorageRoot.Resolve(absolute, "unused"));
        Assert.Equal(Path.GetFullPath(".photos"), PhotoStorageRoot.Resolve(".photos", "unused"));
    }

    [Fact]
    public void A_tilde_with_no_user_data_folder_refuses_with_the_fix()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => PhotoStorageRoot.Resolve("~/photos", string.Empty));

        Assert.Contains("PhotoStorage__RootPath", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Development puts photos in the user's own Yalla folder, outside every checkout, so worktrees that
    /// share the database share the files.
    /// </summary>
    [Fact]
    public void Development_resolves_its_root_outside_the_checkout()
    {
        using var factory = new YallaApiFactory().Without("PhotoStorage:RootPath");

        var expected = PhotoStorageRoot.Resolve("~/photos");

        Assert.Equal(expected, factory.Services.GetRequiredService<PhotoStorageOptions>().RootPath);
        Assert.True(Path.IsPathFullyQualified(expected));
        Assert.EndsWith(Path.Combine(PhotoStorageRoot.ApplicationFolder, "photos"), expected, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ helpers

    private static PhotoSweepHostedService NewService(IServiceProvider provider, TimeProvider time, int intervalMinutes) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new PhotoSweepOptions { SweepIntervalMinutes = intervalMinutes },
            new PhotoStorageOptions { RootPath = Path.GetTempPath() },
            time,
            NullLogger<PhotoSweepHostedService>.Instance);

    /// <summary>Registers a diner through the real endpoint and returns their access token.</summary>
    private static async Task<string> RegisterAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var suffix = Guid.NewGuid().ToString("N")[..10];

            var response = await client.PostAsJsonAsync(
                "/api/auth/diner/register",
                new
                {
                    username = $"sweep_{suffix}",
                    email = $"sweep-{suffix}@example.test",
                    password = Password,

                    // The +374 91 000 xxx test range; a clash with another test's number is retried.
                    phoneE164 = $"+37491000{Random.Shared.Next(100, 1000)}",
                    displayName = "Ani",
                });

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                continue;
            }

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        }

        throw new Xunit.Sdk.XunitException("Five registrations in a row found their number taken.");
    }

    private static async Task<Guid> UploadAsync(HttpClient client, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "me.png");

        var response = await client.PostAsync("/api/diner/me/photo", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("photoId").GetGuid();
    }

    /// <summary>A small PNG whose colour depends on <paramref name="seed"/>, so two seeds are two photos.</summary>
    private static byte[] Png(int seed)
    {
        using var bitmap = new SKBitmap(96, 96);

        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor((byte)(60 * seed), 120, (byte)(200 - (40 * seed))));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);

        return png.ToArray();
    }

    /// <summary>Polls until <paramref name="condition"/> holds: the pass runs on the thread pool after the tick.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException("The hosted sweep did not run within 20 seconds of its tick.");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>Counts sweep passes and lets a test wait for the next one.</summary>
    private sealed class RecordingPhotoService(bool throwOnFirstCall) : IPhotoService
    {
        private readonly SemaphoreSlim _called = new(0);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task WaitForCallAsync() =>
            Assert.True(await _called.WaitAsync(TimeSpan.FromSeconds(10)), "No sweep pass ran after the tick.");

        public Task<int> SweepOrphansAsync(CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            _called.Release();

            return call == 1 && throwOnFirstCall
                ? throw new IOException("The disk went away for a moment.")
                : Task.FromResult(0);
        }

        public Task<PhotoUploadResult> UploadAsync(Guid branchId, Stream content, string contentType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(Stream Content, string ContentType)> OpenVariantAsync(Guid photoId, string variant, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid photoId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
