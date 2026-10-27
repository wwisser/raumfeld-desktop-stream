using System.Net;
using Raumklang.DesktopStream.Audio;
using Raumklang.DesktopStream.Raumfeld;
using Raumklang.DesktopStream.Streaming;

namespace Raumklang.DesktopStream.Runtime;

public sealed record SessionStatus
{
    public string State { get; init; } = "Starting";
    public string? Room { get; init; }
    public string? Device { get; init; }
    public string? Capture { get; init; }
    public bool CaptureActive { get; init; }
    public string Format { get; init; } = "MP3";
    public string? StreamUrl { get; init; }
    public bool ReceiverConnected { get; init; }
    public string? Playback { get; init; }
    public string? Error { get; init; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public long EncodedBytes { get; init; }
    public long HttpBytes { get; init; }
    public long DroppedPcmBytes { get; init; }
    public int HttpClients { get; init; }
    public DateTimeOffset? ReceiverCheckedAt { get; init; }
    public double PcmBufferedMilliseconds { get; init; }
    public double EncoderFirstOutputInputMilliseconds { get; init; }
}

public sealed class Session(Configuration config)
{
    public static string PlaybackPhase(RendererState state, Uri ours, bool receiverConnected, long receiverBytes)
    {
        if (!state.Owns(ours))
        {
            return "Superseded";
        }

        if (state.TransportState == "PLAYING" && receiverConnected && receiverBytes >= 8192)
        {
            return "Streaming";
        }

        if (state.TransportState is "TRANSITIONING" or "PLAYING")
        {
            return "Buffering";
        }

        return "Disconnected";
    }

    private SessionStatus _status = new();
    private AudioHttpServer? _server;
    private AudioPipeline? _audio;
    public SessionStatus Snapshot => Volatile.Read(ref _status) with
    {
        ReceiverConnected = _server?.ReceiverConnected ?? false,
        EncodedBytes = _server?.EncodedBytes ?? 0,
        HttpBytes = _server?.SentBytes ?? 0,
        DroppedPcmBytes = _audio?.DroppedBytes ?? 0,
        HttpClients = _server?.ClientCount ?? 0,
        CaptureActive = _audio?.IsCapturing ?? false,
        Capture = _audio?.DeviceName ?? Volatile.Read(ref _status).Capture,
        PcmBufferedMilliseconds = _audio?.BufferedMilliseconds ?? 0,
        EncoderFirstOutputInputMilliseconds = _audio?.EncoderFirstOutputInputMilliseconds ?? 0
    };

    private void Set(string state, string? error = null)
    {
        Volatile.Write(ref _status, _status with { State = state, Error = error });
        Log.Write($"State: {state}{(error == null ? "" : ": " + error)}");
    }

    public async Task<int> RunAsync(CancellationToken stop)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(stop);
        startup.CancelAfter(TimeSpan.FromSeconds(config.StartupTimeoutSeconds));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        RaumfeldClient? client = null;
        Renderer? renderer = null;
        Task? audioTask = null;
        bool loadAttempted = false;
        try
        {
            Set("Discovering");
            Uri host = await Network.DiscoverAsync(config, http, startup.Token);
            client = new RaumfeldClient(http, host);
            Room room = Protocol.ResolveRoom(await client.GetRoomsAsync(startup.Token), config.RoomName);
            IPAddress hostIp = (await Dns.GetHostAddressesAsync(host.Host, startup.Token)).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            IPAddress local = Network.LocalAddress(hostIp, config.LocalAddress);
            Set("EnsuringZone");
            (room, renderer) = await client.EnsureZoneAsync(room, startup.Token);
            _status = _status with
            {
                Room = room.Name,
                Device = string.Join(", ", room.Renderers.Select(r => r.Name))
            };
            _server = new AudioHttpServer();
            // Virtual renderer's description identifies the actual HTTP consumer host.
            IPAddress receiverIp = (await Dns.GetHostAddressesAsync(renderer.ControlUrl.Host, startup.Token)).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            await _server.StartAsync(local, config.StreamPort, receiverIp, startup.Token);
            _status = _status with
            {
                StreamUrl = _server.Url.AbsoluteUri
            };
            Set("Capturing");
            _audio = new AudioPipeline(config, _server);
            audioTask = _audio.RunAsync(lifetime.Token);
            // Inspect our own listener without counting this connection as the receiver.
            await ProbeStreamAsync(_server.Url, http, startup.Token);
            Set("Loading");
            loadAttempted = true; // Also clean up if receiver accepted the URI but its reply was lost.
            await client.LoadUriAsync(renderer, _server.Url, startup.Token);
            await WaitForAsync(async cancellationToken => (await client.GetStateAsync(renderer, cancellationToken)).Owns(_server.Url), "loadUri accepted but AVTransportURI did not become our stream (metadata/URI rejected).", startup.Token);
            Set("Buffering");
            await client.PlayAsync(renderer, startup.Token);
            await WaitForAsync(async cancellationToken =>
            {
                if (audioTask.IsCompleted)
                {
                    await audioTask;
                }

                RendererState state = await client.GetStateAsync(renderer, cancellationToken);
                _status = _status with
                {
                    Playback = state.TransportState,
                    ReceiverCheckedAt = DateTimeOffset.UtcNow
                };
                return PlaybackPhase(state, _server.Url, _server.ReceiverConnected, _server.ReceiverBytes) == "Streaming";
            }, "Receiver did not enter PLAYING and consume MP3 bytes. Check Windows Firewall/LAN reachability and receiver buffering.", startup.Token);
            Set("Streaming");
            using var poll = new PeriodicTimer(TimeSpan.FromSeconds(2));
            DateTimeOffset? unavailable = null;
            while (await poll.WaitForNextTickAsync(stop))
            {
                if (audioTask.IsCompleted)
                {
                    await audioTask;
                }

                try
                {
                    RendererState state = await client.GetStateAsync(renderer, stop);
                    _status = _status with
                    {
                        Playback = state.TransportState,
                        ReceiverCheckedAt = DateTimeOffset.UtcNow
                    };
                    unavailable = null;
                    if (!state.Owns(_server.Url))
                    {
                        Set("Superseded", "Receiver switched to another source; ending our session.");
                        break;
                    }

                    string next = PlaybackPhase(state, _server.Url, _server.ReceiverConnected, _server.ReceiverBytes);
                    if (_status.State != next)
                    {
                        Set(next);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !stop.IsCancellationRequested)
                {
                    unavailable ??= DateTimeOffset.UtcNow;
                    Set("Disconnected", $"Receiver state unavailable: {ex.Message}");
                    if (DateTimeOffset.UtcNow - unavailable > TimeSpan.FromSeconds(30))
                    {
                        throw new IOException("Receiver unreachable for 30 seconds.", ex);
                    }
                }
            }

            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            string message = ex is OperationCanceledException && startup.IsCancellationRequested ? $"Startup timed out in {_status.State}. Check discovery, audio, loadUri metadata, firewall and HTTP receiver consumption." : ex.Message;
            Set("Failed", message);
            return 1;
        }
        finally
        {
            bool failed = _status.State == "Failed";
            if (!failed)
            {
                Set("Stopping");
            }

            // Use a fresh timeout because the session token is already cancelled.
            if (loadAttempted && client != null && renderer != null && _server != null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    await client.StopIfOwnedAsync(renderer, _server.Url, cleanup.Token);
                }
                catch (Exception ex)
                {
                    Log.Write($"Could not confirm/stop our receiver playback: {ex.Message}");
                }
            }

            await lifetime.CancelAsync();
            if (audioTask != null)
            {
                try
                {
                    await audioTask;
                }
                catch (Exception ex)
                {
                    Log.Debug($"Audio worker ended: {ex.Message}");
                }
            }

            try
            {
                _audio?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Write($"Audio cleanup: {ex.Message}");
            }

            if (_server != null)
            {
                await _server.DisposeAsync();
            }

            if (!failed)
            {
                Set("Stopped");
            }
        }
    }

    public static async Task ProbeStreamAsync(Uri uri, HttpClient http, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "audio/mpeg" || response.Content.Headers.ContentLength != null)
        {
            throw new IOException("HTTP stream has invalid live MP3 headers.");
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] bytes = new byte[4096];
        int count = await body.ReadAsync(bytes, cancellationToken);
        if (count == 0)
        {
            throw new IOException("HTTP stream delivers no MP3 bytes.");
        }

        Log.Write($"HTTP self-check: 200 audio/mpeg; received {count} bytes (local check only).");
    }

    private static async Task WaitForAsync(Func<CancellationToken, Task<bool>> check, string error, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        Exception? last = null;
        try
        {
            while (true)
            {
                try
                {
                    if (await check(deadline.Token))
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    last = ex;
                }

                await Task.Delay(500, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException(error + (last == null ? "" : " " + last.Message));
        }
    }
}
