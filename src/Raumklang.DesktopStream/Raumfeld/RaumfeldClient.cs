using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace Raumklang.DesktopStream.Raumfeld;

public sealed class RaumfeldClient(HttpClient http, Uri host)
{
    public Uri Host { get; } = host;

    private async Task<XDocument> GetXmlAsync(string path, CancellationToken cancellationToken)
    {
        var uri = new Uri(Host, path);
        Log.Debug($"GET {uri}");
        using HttpResponseMessage response = await http.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<Room>> GetRoomsAsync(CancellationToken cancellationToken)
    {
        return Protocol.ParseRooms(await GetXmlAsync("getZones", cancellationToken));
    }

    public async Task<(Room Room, Renderer Renderer)> EnsureZoneAsync(Room room, CancellationToken cancellationToken)
    {
        // Refresh before mutation: another controller may have assigned this room since discovery.
        room = Protocol.ResolveRoom(await GetRoomsAsync(cancellationToken), room.Udn);
        if (string.IsNullOrEmpty(room.ZoneUdn))
        {
            Log.Write($"Creating zone for unassigned room {room.Name} ({room.PowerState}).");
            using HttpResponseMessage response = await http.GetAsync(new Uri(Host, $"connectRoomToZone?roomUDN={Uri.EscapeDataString(room.Udn)}&zoneUDN="), cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        else
        {
            Log.Write($"Reusing zone {room.ZoneUdn} for {room.Name}.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        Exception? last = null;
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    room = Protocol.ResolveRoom(await GetRoomsAsync(deadline.Token), room.Udn);
                    if (!string.IsNullOrEmpty(room.ZoneUdn))
                    {
                        Renderer? renderer = await ResolveRendererAsync(room.ZoneUdn, deadline.Token);
                        if (renderer != null)
                        {
                            return (room, renderer);
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
                {
                    last = ex;
                }

                await Task.Delay(400, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"Zone creation/propagation failed for {room.Name}; virtual AVTransport renderer never became available. {last?.Message}");
        }
    }

    public async Task<Renderer?> ResolveRendererAsync(string zoneUdn, CancellationToken cancellationToken)
    {
        XDocument devices = await GetXmlAsync("listDevices", cancellationToken);
        XElement[] entries = [.. devices.Descendants().Where(e => e.Name.LocalName == "device")];
        // Resolve the zone's AVTransport URL from its device description.
        foreach (XElement? entry in entries.OrderByDescending(e => (string?)e.Attribute("udn") == zoneUdn))
        {
            string? locationText = (string?)entry.Attribute("location");
            if (string.IsNullOrEmpty(locationText))
            {
                continue;
            }

            var location = new Uri(Host, locationText);
            try
            {
                var doc = XDocument.Parse(await http.GetStringAsync(location, cancellationToken));
                Renderer? renderer = Protocol.ParseRenderer(doc, location, zoneUdn);
                if (renderer != null)
                {
                    return renderer;
                }
            }
            catch (HttpRequestException ex)
            {
                Log.Debug($"Description unavailable at {location}: {ex.Message}");
            }
        }

        return null;
    }

    public async Task<XDocument> SendActionAsync(Renderer renderer, string action, CancellationToken cancellationToken, params (string Key, string Value)[] args)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, renderer.ControlUrl);
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{renderer.ServiceType}#{action}\"");
        request.Content = new StringContent(Protocol.Soap(renderer.ServiceType, action, [("InstanceID", "0"), .. args]), Encoding.UTF8, "text/xml");
        Log.Debug($"SOAP {renderer.ControlUrl} {action} ({renderer.Udn})");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        XDocument xml;
        try
        {
            xml = XDocument.Parse(text);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new IOException($"{action}: HTTP {(int)response.StatusCode}, invalid SOAP XML", ex);
        }

        string fault = Protocol.Value(xml, "faultstring");
        string error = Protocol.Value(xml, "errorDescription");
        return !response.IsSuccessStatusCode || xml.Descendants().Any(e => e.Name.LocalName == "Fault") ? throw new IOException($"{action} rejected: HTTP {(int)response.StatusCode}, {fault} {error} (UPnP {Protocol.Value(xml, "errorCode")})") : xml;
    }

    public Task<XDocument> LoadUriAsync(Renderer renderer, Uri uri, CancellationToken cancellationToken)
    {
        return SendActionAsync(renderer, "SetAVTransportURI", cancellationToken, ("CurrentURI", uri.AbsoluteUri), ("CurrentURIMetaData", Protocol.Metadata(uri)));
    }

    public Task<XDocument> PlayAsync(Renderer renderer, CancellationToken cancellationToken)
    {
        return SendActionAsync(renderer, "Play", cancellationToken, ("Speed", "1"));
    }

    public Task<XDocument> StopAsync(Renderer renderer, CancellationToken cancellationToken)
    {
        return SendActionAsync(renderer, "Stop", cancellationToken);
    }

    public async Task<RendererState> GetStateAsync(Renderer renderer, CancellationToken cancellationToken)
    {
        XDocument media = await SendActionAsync(renderer, "GetMediaInfo", cancellationToken);
        XDocument transport = await SendActionAsync(renderer, "GetTransportInfo", cancellationToken);
        // CurrentURI from GetMediaInfo is the transport URI, unlike a transient track URI.
        return new(Protocol.Value(transport, "CurrentTransportState"), Protocol.Value(media, "CurrentURI"), "");
    }

    public async Task<bool> StopIfOwnedAsync(Renderer renderer, Uri uri, CancellationToken cancellationToken)
    {
        if (!(await GetStateAsync(renderer, cancellationToken)).Owns(uri))
        {
            Log.Write("Receiver now has another URI; leaving its playback alone.");
            return false;
        }

        await StopAsync(renderer, cancellationToken);
        return true;
    }
}

public sealed record NetworkAddress(string Name, IPAddress Address);

public static class Network
{
    public const string ConfigTarget = "urn:schemas-raumfeld-com:device:ConfigDevice:1";

    private static readonly string[] s_virtualAdapters =
    [
        "vethernet", "hyper-v", "wsl", "virtualbox", "vmware", "loopback", "tailscale", "vpn", "docker"
    ];

    public static bool SuitableAddress(IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetwork
            && !IPAddress.IsLoopback(address)
            && !address.Equals(IPAddress.Any)
            && address.GetAddressBytes()[0] is > 0 and < 224
            && !(address.GetAddressBytes()[0] == 169 && address.GetAddressBytes()[1] == 254);
    }

    public static bool VirtualAdapter(string name)
    {
        return s_virtualAdapters.Any(adapter => name.Contains(adapter, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<NetworkAddress> Interfaces()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
                && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
                .Where(address => SuitableAddress(address.Address))
                .Select(address => new NetworkAddress(adapter.Name, address.Address)));
    }

    public static IPAddress LocalAddress(IPAddress host, string? manual)
    {
        IPAddress selected;
        if (!string.IsNullOrEmpty(manual))
        {
            selected = IPAddress.Parse(manual);
        }
        else
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(host, 47365));
            selected = ((IPEndPoint)socket.LocalEndPoint!).Address;
        }

        if (!SuitableAddress(selected) || !Interfaces().Any(i => i.Address.Equals(selected)))
        {
            throw new IOException($"Wrong local IP {selected}; set localAddress to this PC's reachable LAN IPv4 address.");
        }

        NetworkAddress adapter = Interfaces().First(i => i.Address.Equals(selected));
        if (string.IsNullOrEmpty(manual) && VirtualAdapter(adapter.Name))
        {
            throw new IOException($"Route to Raumfeld uses virtual/VPN adapter {adapter.Name}. Fix routing or set localAddress.");
        }

        return selected;
    }

    public static async Task<Uri> DiscoverAsync(Configuration config, HttpClient http, CancellationToken cancellationToken)
    {
        if (!string.Equals(config.RaumfeldHost, "auto", StringComparison.OrdinalIgnoreCase))
        {
            Uri host = new UriBuilder("http", config.RaumfeldHost, config.RaumfeldPort).Uri;
            await new RaumfeldClient(http, host).GetRoomsAsync(cancellationToken);
            return host;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        NetworkAddress[] interfaces = [.. Interfaces().Where(i => !VirtualAdapter(i.Name))];
        if (interfaces.Length == 0)
        {
            throw new IOException("No active LAN IPv4 interfaces available for SSDP discovery.");
        }

        var attempts = interfaces.Select(i => ProbeAsync(i.Address, config.RaumfeldPort, http, deadline.Token)).ToList();
        while (attempts.Count > 0)
        {
            Task<Uri?> done = await Task.WhenAny(attempts);
            attempts.Remove(done);
            Uri? found = await done;
            if (found != null)
            {
                await deadline.CancelAsync();
                await Task.WhenAll(attempts);
                return found;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new IOException("Raumfeld host not found via ConfigDevice SSDP. Check LAN/multicast/firewall or set raumfeldHost in config.json.");
    }

    private static async Task<Uri?> ProbeAsync(IPAddress local, int port, HttpClient http, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(new IPEndPoint(local, 0));
        byte[] search = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {ConfigTarget}\r\n\r\n");
        try
        {
            // Repeat SSDP searches until the deadline.
            while (!cancellationToken.IsCancellationRequested)
            {
                await udp.SendAsync(search, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), cancellationToken);
                using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                window.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    while (true)
                    {
                        UdpReceiveResult reply = await udp.ReceiveAsync(window.Token);
                        var headers = Encoding.ASCII.GetString(reply.Buffer).Split("\r\n").Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2).GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);
                        if (!headers.TryGetValue("ST", out string? target) || target != ConfigTarget || !headers.TryGetValue("LOCATION", out string? value) || !Uri.TryCreate(value, UriKind.Absolute, out Uri? location))
                        {
                            continue;
                        }

                        Uri host = new UriBuilder("http", location.Host, port).Uri;
                        try
                        {
                            await new RaumfeldClient(http, host).GetRoomsAsync(cancellationToken);
                            return host;
                        }
                        catch (HttpRequestException ex)
                        {
                            Log.Debug($"SSDP candidate {host}: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or HttpRequestException)
        {
            Log.Debug($"SSDP {local}: {ex.Message}");
        }

        return null;
    }
}
