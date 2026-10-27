using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NAudio.Lame;
using NAudio.Wave;
using Raumklang.DesktopStream;
using Raumklang.DesktopStream.Audio;
using Raumklang.DesktopStream.Diagnostics;
using Raumklang.DesktopStream.Raumfeld;
using Raumklang.DesktopStream.Runtime;
using Raumklang.DesktopStream.Streaming;

int failures = 0;
int passed = 0;
async Task TestAsync(string name, Func<Task> body)
{
    try
    {
        await body();
        Console.WriteLine($"PASS {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL {name}: {ex}");
        failures++;
    }
}

void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".xml"));
string unassigned = Fixture("unassigned");
string active = Fixture("active");
string roomUdn = "uuid:6129ac01-b954-4e95-984c-85706f182557";
string zoneUdn = "uuid:f02ad526-5f3b-49b7-89ff-52de8ad9fbdb";
string physicalUdn = "uuid:73bf5950-bc33-4a43-abe9-ef557ecdb195";
await TestAsync("unassigned room, standby, name/UDN/physical renderer matching", () =>
{
    IReadOnlyList<Room> rooms = Protocol.ParseRooms(XDocument.Parse(unassigned));
    Room room = Protocol.ResolveRoom(rooms, "wohnzimmer");
    Check(room.ZoneUdn == null && room.PowerState == "AUTOMATIC_STANDBY", "Unassigned standby room lost");
    Check(Protocol.ResolveRoom(rooms, roomUdn) == room && Protocol.ResolveRoom(rooms, physicalUdn) == room, "Identifier matching");
    Check(Protocol.ResolveRoom(rooms, null) == room, "Single-room auto select");
    Room[] duplicate = [.. rooms, room with { Name = "Other", Udn = "uuid:other" }];
    try
    {
        Protocol.ResolveRoom(duplicate, null);
        throw new Exception("Ambiguous rooms accepted");
    }
    catch (InvalidOperationException ex)
    {
        Check(ex.Message.Contains("Other"), "Missing useful room guidance");
    }

    return Task.CompletedTask;
});
await TestAsync("active room resolves virtual zone identity separately from physical renderer", () =>
{
    Room room = Protocol.ParseRooms(XDocument.Parse(active)).Single();
    Check(room.ZoneUdn == zoneUdn && room.Renderers.Single().Udn == physicalUdn, "Room and zone UDNs were mixed up");
    return Task.CompletedTask;
});
await TestAsync("DIDL preserves Raumfeld baseline fields, MIME declaration and valid namespaces", () =>
{
    var uri = new Uri("http://192.168.1.162:12345/stream/live.mp3?x=1&title=%3Ctest%3E");
    var xml = XDocument.Parse(Protocol.Metadata(uri));
    Check(Protocol.Value(xml, "class") == "object.item.audioItem.audioBroadcast.radio", "Wrong class");
    Check(Protocol.Value(xml, "durability") == "120" && Protocol.Value(xml, "section") == "External", "Wrong RF metadata");
    Check(xml.Descendants().Single(e => e.Name.LocalName == "item").Attribute("id")!.Value == "0/External", "Wrong ID");
    XElement resource = xml.Descendants().Single(e => e.Name.LocalName == "res");
    Check(resource.Attribute("protocolInfo")!.Value == "http-get:*:audio/x-mpegurl:*", "MIME normalized incorrectly");
    Check(resource.Value == uri.AbsoluteUri, "URL did not survive XML escaping");
    return Task.CompletedTask;
});
await TestAsync("SOAP roundtrip escapes URI and embedded DIDL exactly once", () =>
{
    var uri = new Uri("http://192.168.1.162:12345/stream/live.mp3?a=1&b=2");
    string metadata = Protocol.Metadata(uri);
    var soap = XDocument.Parse(Protocol.Soap("urn:schemas-upnp-org:service:AVTransport:1", "SetAVTransportURI", ("InstanceID", "0"), ("CurrentURI", uri.AbsoluteUri), ("CurrentURIMetaData", metadata)));
    Check(Protocol.Value(soap, "CurrentURI") == uri.AbsoluteUri, "URI escaping");
    Check(Protocol.Value(soap, "CurrentURIMetaData") == metadata, "Metadata double escape");
    Check(soap.Descendants().Single(e => e.Name.LocalName == "SetAVTransportURI").Name.NamespaceName.EndsWith("AVTransport:1"), "SOAP action namespace");
    return Task.CompletedTask;
});
await TestAsync("route/address filtering rejects loopback, APIPA, multicast and virtual adapter names", () =>
{
    foreach (string? address in new[]
    {
        "127.0.0.1",
        "169.254.1.2",
        "0.0.0.0",
        "239.1.2.3",
        "::1"
    }

    )
    {
        Check(!Network.SuitableAddress(IPAddress.Parse(address)), address);
    }

    Check(Network.SuitableAddress(IPAddress.Parse("192.168.1.162")), "LAN rejected");
    Check(Network.VirtualAdapter("vEthernet (WSL (Hyper-V firewall))") && !Network.VirtualAdapter("Ethernet"), "Adapter filtering");
    return Task.CompletedTask;
});
await TestAsync("playback phases require owned URI, PLAYING and ongoing receiver bytes", () =>
{
    var uri = new Uri("http://192.0.2.1:1234/stream/live.mp3");
    var playing = new RendererState("PLAYING", uri.AbsoluteUri, "");
    Check(Session.PlaybackPhase(playing, uri, false, 0) == "Buffering", "Play accepted was treated as success");
    Check(Session.PlaybackPhase(playing, uri, true, 32768) == "Streaming", "Valid streaming rejected");
    Check(Session.PlaybackPhase(playing with { AvTransportUri = "" }, uri, true, 32768) == "Superseded", "Empty URI accepted");
    Check(Session.PlaybackPhase(playing with { TransportState = "STOPPED" }, uri, true, 32768) == "Disconnected", "Stopped receiver streaming");
    return Task.CompletedTask;
});
await TestAsync("zone creation, reuse, SOAP playback and URI ownership", async () =>
{
    await using var mock = new MockRaumfeld(unassigned, active, zoneUdn);
    await mock.StartAsync();
    using var http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(3)
    };
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var client = new RaumfeldClient(http, mock.Url);
    Room room = Protocol.ResolveRoom(await client.GetRoomsAsync(timeout.Token), null);
    (Room Room, Renderer Renderer) resolved = await client.EnsureZoneAsync(room, timeout.Token);
    Check(mock.ZoneCreates == 1 && resolved.Renderer.Udn == zoneUdn, "Zone not created or wrong renderer");
    Check(resolved.Renderer.ControlUrl.AbsolutePath == "/virtual/avtransport", "Guessed control path");
    await client.EnsureZoneAsync(resolved.Room, timeout.Token);
    Check(mock.ZoneCreates == 1, "Created duplicate zone");
    var uri = new Uri("http://192.168.1.162:12345/stream/live.mp3?x=1&y=2");
    await client.LoadUriAsync(resolved.Renderer, uri, timeout.Token);
    Check((await client.GetStateAsync(resolved.Renderer, timeout.Token)).Owns(uri), "URI not loaded");
    await client.PlayAsync(resolved.Renderer, timeout.Token);
    Check((await client.GetStateAsync(resolved.Renderer, timeout.Token)).TransportState == "PLAYING", "No playback state");
    Check(await client.StopIfOwnedAsync(resolved.Renderer, uri, timeout.Token), "Own playback not stopped");
    mock.CurrentUri = "spotify:track:another-user-session";
    int stops = mock.Stops;
    Check(!await client.StopIfOwnedAsync(resolved.Renderer, uri, timeout.Token) && mock.Stops == stops, "Stopped Spotify session");
    mock.FailNext = true;
    try
    {
        await client.PlayAsync(resolved.Renderer, timeout.Token);
        throw new Exception("SOAP fault ignored");
    }
    catch (IOException ex)
    {
        Check(ex.Message.Contains("701") && ex.Message.Contains("Play"), "Missing action/fault diagnostics");
    }

    Check(mock.CorrectSoap, "Wrong SOAP action header or InstanceID");
});
foreach (int rate in new[]
{
    44100,
    48000
}

)
{
    await TestAsync($"native MP3 {rate} Hz stereo: multiple complete consecutive MPEG frames", () =>
    {
        using var output = new MemoryStream();
        using (var encoder = new LameMP3FileWriter(output, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2), new LameConfig { BitRate = 192, WriteVBRTag = false }))
        {
            float[] samples = new float[rate * 2];
            for (int i = 0; i < samples.Length / 2; i++)
            {
                samples[i * 2] = samples[(i * 2) + 1] = (float)(0.1 * Math.Sin(2 * Math.PI * 440 * i / rate));
            }

            byte[] bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
            for (int i = 0; i < 3; i++)
            {
                encoder.Write(bytes, 0, bytes.Length);
            }
        }

        byte[] data = output.ToArray();
        Check(ValidFrames(data, rate) > 20, "No valid contiguous MPEG1 Layer III frames");
        Check(data.Length is > 30_000 and < 100_000, "Unexpected bitrate/output size");
        return Task.CompletedTask;
    });
}

await TestAsync("streaming MP3 emits frames without the one-second file buffer", () =>
{
    foreach (int rate in new[] { 44100, 48000 })
    {
        using var output = new MemoryStream();
        using var encoder = new StreamingMp3Encoder(output, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2), 192);
        byte[] block = new byte[rate / 100 * 8];
        using var oldOutput = new MemoryStream();
        using var oldEncoder = new LameMP3FileWriter(oldOutput, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2),
            new LameConfig { BitRate = 192, WriteVBRTag = false });
        int oldFirstOutput = 0;
        for (int index = 0; index < 200; index++)
        {
            encoder.Write(block);
            oldEncoder.Write(block, 0, block.Length);
            if (oldFirstOutput == 0 && oldOutput.Length > 0)
            {
                oldFirstOutput = (index + 1) * 10;
            }
        }

        Check(encoder.FirstOutputInputMilliseconds is > 0 and <= 100, "MP3 output waited more than 100 ms of input");
        Check(oldFirstOutput == 1000, "Unexpected file-wrapper batching; revise the latency comparison");
        Check(ValidFrames(output.ToArray(), rate) > 30, "Streaming encoder did not produce complete stereo MP3 frames");
        Console.WriteLine($"MP3 {rate} Hz first output: {oldFirstOutput} ms before, {encoder.FirstOutputInputMilliseconds:F1} ms now.");
    }

    return Task.CompletedTask;
});

await TestAsync("acoustic correlation finds a delayed chirp and rejects silence", () =>
{
    float[] chirp = LatencyMeasurement.CreateChirp(8000);
    float[] recording = new float[32000];
    var random = new Random(42);
    for (int index = 0; index < recording.Length; index++)
    {
        recording[index] = (float)((random.NextDouble() - 0.5) * 0.002);
    }

    for (int index = 0; index < chirp.Length; index++)
    {
        recording[12345 + index] += chirp[index] * 0.4f;
    }

    CorrelationPeak peak = LatencyMeasurement.FindPeak(recording, chirp, 0, recording.Length);
    Check(Math.Abs(peak.Sample - 12345) <= 1 && peak.Confidence > 0.95, "Incorrect acoustic delay or correlation confidence");
    for (int index = 0; index < chirp.Length; index++)
    {
        recording[2000 + index] += chirp[index];
    }

    CorrelationPeak speaker = LatencyMeasurement.FindPeak(recording, chirp, 0, recording.Length, [2000]);
    Check(Math.Abs(speaker.Sample - 12345) <= 1, "Local leakage was mistaken for the speaker signal");
    Check(LatencyMeasurement.FindPeak(new float[32000], chirp, 0, 32000).Confidence == 0, "Silence reported as a measurement");
    return Task.CompletedTask;
});

await TestAsync("live HTTP MP3 headers, dynamic URL, disconnect/reconnect, silence bytes and cleanup", async () =>
{
    await using var server = new AudioHttpServer();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await server.StartAsync(IPAddress.Loopback, 0, IPAddress.Parse("192.0.2.10"), timeout.Token);
    Uri url = server.Url;
    Check(url.Host == "127.0.0.1" && url.Port > 0 && url.AbsolutePath == "/stream/live.mp3", "Incorrect bound URL");
    using var http = new HttpClient();
    using var encoder = new LameMP3FileWriter(server, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), new LameConfig { BitRate = 192, WriteVBRTag = false });
    byte[] silence = new byte[48000 * 8];
    for (int attempt = 0; attempt < 2; attempt++)
    {
        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType!.MediaType == "audio/mpeg", "Wrong HTTP headers");
        Check(response.Content.Headers.ContentLength == null, "Finite live stream");
        encoder.Write(silence, 0, silence.Length);
        encoder.Write(silence, 0, silence.Length);
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        byte[] bytes = new byte[8192];
        int count = await stream.ReadAtLeastAsync(bytes, 4096, false, timeout.Token);
        Check(count > 0 && ValidFrames(bytes[..count], 48000) > 2, "Missing MP3 silence frames");
        Check(!server.ReceiverConnected, "Local probe counted as receiver");
    }

    await server.DisposeAsync();
    try
    {
        using HttpResponseMessage response = await http.GetAsync(url, timeout.Token);
        throw new Exception("Listener survived stop");
    }
    catch (HttpRequestException)
    {
    }

    await using var restarted = new AudioHttpServer();
    await restarted.StartAsync(IPAddress.Loopback, url.Port, IPAddress.Loopback, timeout.Token);
    Check(restarted.Url.Port == url.Port, "Port was not released");
});
await TestAsync("exclusive worker lock rejects duplicate owners and releases after stop", () =>
{
    string path = Path.Combine(Path.GetTempPath(), "Raumklang.DesktopStream-test-" + Guid.NewGuid() + ".lock");
    try
    {
        using (var owner = new InstanceLock(path))
        {
            try
            {
                using var duplicate = new InstanceLock(path);
                throw new Exception("Second worker lock accepted");
            }
            catch (IOException)
            {
            }
        }

        using var restart = new InstanceLock(path);
    }
    finally
    {
        File.Delete(path);
    }

    return Task.CompletedTask;
});
await TestAsync("named pipe status comes from worker; stop cancels lifetime and pipe closes", async () =>
{
    string pipeName = "Raumklang.DesktopStream-test-" + Guid.NewGuid().ToString("N");
    using var sessionStop = new CancellationTokenSource();
    using var ipcStop = new CancellationTokenSource();
    var session = new Session(new());
    Task listen = InstanceControl.ListenAsync(session, sessionStop, ipcStop.Token, pipeName);
    try
    {
        SessionStatus? state = await InstanceControl.RequestAsync("status", pipeName: pipeName);
        Check(state?.State == "Starting" && state.ProcessId == Environment.ProcessId, "Status not from this worker");
        await InstanceControl.RequestAsync("stop", pipeName: pipeName);
        await Task.Delay(50);
        Check(sessionStop.IsCancellationRequested, "Stop did not cancel session");
    }
    finally
    {
        await ipcStop.CancelAsync();
        await listen;
    }

    Check(await InstanceControl.RequestAsync("status", 150, pipeName) == null, "IPC not released");
});
await TestAsync("port conflict leaves the existing listener available", async () =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    await using var owner = new AudioHttpServer();
    await owner.StartAsync(IPAddress.Loopback, 0, IPAddress.Loopback, timeout.Token);
    await using var duplicate = new AudioHttpServer();
    try
    {
        await duplicate.StartAsync(IPAddress.Loopback, owner.Url.Port, IPAddress.Loopback, timeout.Token);
        throw new Exception("Duplicate bind succeeded");
    }
    catch (IOException)
    {
    }

    using var http = new HttpClient();
    using HttpResponseMessage response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, owner.Url), timeout.Token);
    Check(response.IsSuccessStatusCode, "Port conflict disrupted existing listener");
});
await TestAsync("missing room returns Failed and leaves no HTTP listener", async () =>
{
    await using var mock = new MockRaumfeld(unassigned, active, zoneUdn);
    await mock.StartAsync();
    var session = new Session(new Configuration { RaumfeldHost = "127.0.0.1", RaumfeldPort = mock.Url.Port, RoomName = "Nonexistent" });
    Check(await session.RunAsync(CancellationToken.None) == 1, "Startup failure returned success");
    Check(session.Snapshot.State == "Failed" && session.Snapshot.Error!.Contains("Wohnzimmer"), "Missing useful failure state");
    Check(session.Snapshot.HttpClients == 0 && session.Snapshot.StreamUrl == null, "Startup failure leaked HTTP resources");
});
if (args.Contains("--audio-smoke"))
{
    await TestAsync("Windows hardware loopback + LAME + HTTP smoke (no receiver commands)", async () =>
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new AudioHttpServer();
        await server.StartAsync(IPAddress.Loopback, 0, IPAddress.Parse("192.0.2.1"), stop.Token);
        using var audio = new AudioPipeline(new(), server);
        Task task = audio.RunAsync(stop.Token);
        using var http = new HttpClient();
        try
        {
            await Session.ProbeStreamAsync(server.Url, http, stop.Token);
            Check(!task.IsFaulted, "Audio task faulted");
            long before = server.EncodedBytes;
            var clock = Stopwatch.StartNew();
            await Task.Delay(2000, stop.Token);
            double expected = clock.Elapsed.TotalSeconds * 192_000 / 8;
            double ratio = (server.EncodedBytes - before) / expected;
            Check(ratio is > 0.85 and < 1.15, "Audio output clock drifted when timer ticks were delayed");
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }
    });
}

Console.WriteLine($"{passed} passed, {failures} failed.");
return failures == 0 ? 0 : 1;
static int ValidFrames(byte[] bytes, int expectedRate)
{
    int[] rates = [44100, 48000, 32000];
    int[] bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    int max = 0;
    for (int start = 0; start + 4 < bytes.Length; start++)
    {
        int count = 0, offset = start;
        while (offset + 4 < bytes.Length && bytes[offset] == 0xff && (bytes[offset + 1] & 0xfe) == 0xfa)
        {
            int index = bytes[offset + 2] >> 4;
            int rateIndex = (bytes[offset + 2] >> 2) & 3;
            if (rateIndex == 3 || rates[rateIndex] != expectedRate || bitrates[index] == 0 || (bytes[offset + 3] >> 6) == 3)
            {
                break;
            }

            int length = (144000 * bitrates[index] / rates[rateIndex]) + ((bytes[offset + 2] >> 1) & 1);
            if (offset + length > bytes.Length)
            {
                break;
            }

            offset += length;
            count++;
        }

        max = Math.Max(max, count);
        if (count > 0)
        {
            start = offset - 1;
        }
    }

    return max;
}

internal sealed class MockRaumfeld(string unassigned, string active, string zoneUdn) : IAsyncDisposable
{
    private WebApplication _app = null!;
    public Uri Url { get; private set; } = null!;

    public int ZoneCreates;
    public int Stops;
    private int _propagation;
    public string CurrentUri = "";
    public bool FailNext;
    public bool CorrectSoap = true;
    private string _transport = "NO_MEDIA_PRESENT";
    public async Task StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.MapGet("/getZones", () => Results.Text(ZoneCreates > 0 && Interlocked.Increment(ref _propagation) > 2 ? active : unassigned, "text/xml"));
        _app.MapGet("/connectRoomToZone", (HttpContext c) =>
        {
            if (c.Request.Query["zoneUDN"] != "" || !c.Request.Query["roomUDN"].ToString().StartsWith("uuid:"))
            {
                return Results.BadRequest();
            }

            ZoneCreates++;
            return Results.Ok();
        });
        _app.MapGet("/listDevices", () => Results.Text($"<devices><device udn='{zoneUdn}' location='{Url}description.xml'/></devices>", "text/xml"));
        _app.MapGet("/description.xml", () => Results.Text($"""
            <root xmlns="urn:schemas-upnp-org:device-1-0"><device><deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
            <friendlyName>Raumfeld Zone</friendlyName><UDN>{zoneUdn}</UDN><serviceList><service>
            <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>/virtual/avtransport</controlURL>
            </service></serviceList></device></root>
            """, "text/xml"));
        _app.MapPost("/virtual/avtransport", async c =>
        {
            var xml = XDocument.Parse(await new StreamReader(c.Request.Body).ReadToEndAsync());
            string action = xml.Descendants().Single(e => e.Name.LocalName == "Body").Elements().Single().Name.LocalName;
            CorrectSoap &= c.Request.Headers["SOAPACTION"] == $"\"urn:schemas-upnp-org:service:AVTransport:1#{action}\"" && Protocol.Value(xml, "InstanceID") == "0";
            if (FailNext)
            {
                FailNext = false;
                c.Response.StatusCode = 500;
                await c.Response.WriteAsync("<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body><s:Fault><faultstring>UPnPError</faultstring><detail><errorCode>701</errorCode><errorDescription>Transition not available</errorDescription></detail></s:Fault></s:Body></s:Envelope>");
                return;
            }

            if (action == "SetAVTransportURI")
            {
                CurrentUri = Protocol.Value(xml, "CurrentURI");
                XDocument.Parse(Protocol.Value(xml, "CurrentURIMetaData"));
                _transport = "STOPPED";
            }

            if (action == "Play")
            {
                _transport = "PLAYING";
            }

            if (action == "Stop")
            {
                _transport = "STOPPED";
                Stops++;
            }

            string result = action switch
            {
                "GetMediaInfo" => new XElement("CurrentURI", CurrentUri).ToString(),
                "GetTransportInfo" => new XElement("CurrentTransportState", _transport).ToString(),
                _ => ""
            };
            c.Response.ContentType = "text/xml";
            await c.Response.WriteAsync($"<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body><u:{action}Response xmlns:u='urn:schemas-upnp-org:service:AVTransport:1'>{result}</u:{action}Response></s:Body></s:Envelope>");
        });
        await _app.StartAsync();
        Url = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
    }
}
