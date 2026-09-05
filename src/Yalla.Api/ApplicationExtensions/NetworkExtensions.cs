using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Yalla.Api.ApplicationExtensions;

/// <summary>
/// Development only: says, on startup, where a phone on the same wifi can reach this API.
/// </summary>
/// <remarks>
/// <para>
/// The Development Kestrel endpoints bind to <c>0.0.0.0</c> (see <c>appsettings.Development.json</c>),
/// which is what makes the API reachable from other devices - but "listening on 0.0.0.0" tells
/// nobody what to type into the frontend config. This logs the machine's LAN addresses with both
/// ports, so the value for the web app, the Expo app and <c>pnpm api:generate</c> is in the
/// console rather than in <c>ipconfig</c>.
/// </para>
/// <para>
/// Nothing here changes behaviour. It reads what Kestrel bound and what the network stack reports
/// and writes a log line. Outside Development it does nothing at all.
/// </para>
/// </remarks>
public static class NetworkExtensions
{
    /// <summary>Adapter names and descriptions that mean "not the wifi" - listed, but last and flagged.</summary>
    private static readonly string[] VirtualAdapterMarkers =
    [
        "virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "docker", "wsl", "loopback", "tailscale", "zerotier",
    ];

    public static WebApplication LogLanAddresses(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            return app;
        }

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            try
            {
                Log(app);
            }
            catch (Exception ex)
            {
                // A log line must never take the host down.
                app.Logger.LogDebug(ex, "Could not determine the LAN address.");
            }
        });

        return app;
    }

    private static void Log(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses ?? [];

        var bound = addresses
            .Select(a => Uri.TryCreate(a.Replace("*", "0.0.0.0").Replace("+", "0.0.0.0"), UriKind.Absolute, out var uri) ? uri : null)
            .Where(u => u is not null)
            .Select(u => u!)
            .ToList();

        var onAllInterfaces = bound
            .Where(u => u.Host is "0.0.0.0" or "::" or "[::]")
            .ToList();

        if (onAllInterfaces.Count == 0)
        {
            app.Logger.LogInformation(
                "Listening on {Addresses} - loopback only, not reachable from other devices. "
                + "The Development Kestrel endpoints in appsettings.Development.json bind 0.0.0.0.",
                string.Join(", ", addresses));

            return;
        }

        var lan = LanAddresses();

        if (lan.Count == 0)
        {
            app.Logger.LogWarning(
                "Listening on all interfaces ({Addresses}) but no LAN IPv4 address was found. Is the machine on a network?",
                string.Join(", ", addresses));

            return;
        }

        var http = onAllInterfaces.FirstOrDefault(u => u.Scheme == Uri.UriSchemeHttp);
        var https = onAllInterfaces.FirstOrDefault(u => u.Scheme == Uri.UriSchemeHttps);
        var swaggerOn = SwaggerExtensions.IsEnabled(app.Configuration);

        var lines = new List<string> { "Reachable from other devices on the local network (Development only):" };

        foreach (var (ip, adapter, isVirtual) in lan)
        {
            var tag = isVirtual ? $"  ({adapter} - probably a virtual adapter, try the other one first)" : $"  ({adapter})";

            if (http is not null)
            {
                lines.Add($"  http://{ip}:{http.Port}{tag}");
            }

            if (https is not null)
            {
                lines.Add($"  https://{ip}:{https.Port}{(http is null ? tag : string.Empty)}");
            }
        }

        var preferred = lan[0].Address;
        var preferredHttp = http is null ? null : $"http://{preferred}:{http.Port}";

        if (preferredHttp is not null)
        {
            lines.Add(string.Empty);
            lines.Add("  Plain HTTP is for local device testing ONLY - phones will not trust the dev certificate.");
            lines.Add($"  Web app / Expo app API base URL:  {preferredHttp}");

            if (swaggerOn)
            {
                lines.Add($"  Swagger UI:                       {preferredHttp}{SwaggerExtensions.BasePath}");
                lines.Add($"  pnpm api:generate:                {preferredHttp}{SwaggerExtensions.BasePath}/v1/swagger.json");
            }
        }

        lines.Add("  If a phone cannot connect, Windows Firewall is usually blocking the port - see README.md.");

        app.Logger.LogInformation("{LanAddresses}", string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// Every IPv4 address on an up, non-loopback adapter, real wifi and ethernet first, virtual
    /// adapters (VirtualBox host-only, Hyper-V, WSL, Docker) after them and flagged.
    /// </summary>
    private static List<(IPAddress Address, string Adapter, bool IsVirtual)> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Where(u => !IsLinkLocal(u.Address))
                .Select(u => (u.Address, Adapter: n.Name, IsVirtual: LooksVirtual(n))))
            .OrderBy(x => x.IsVirtual)
            .ThenBy(x => x.Adapter)
            .ToList();

    private static bool LooksVirtual(NetworkInterface adapter)
    {
        var text = $"{adapter.Name} {adapter.Description}";

        return VirtualAdapterMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>169.254.x.x - an adapter that never got an address.</summary>
    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        return bytes[0] == 169 && bytes[1] == 254;
    }
}
