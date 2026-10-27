using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using NAudio.CoreAudioApi;
using Raumklang.DesktopStream;
using Raumklang.DesktopStream.Audio;
using Raumklang.DesktopStream.Raumfeld;
using Raumklang.DesktopStream.Runtime;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Raumklang Desktop Stream requires Windows.");
    return 1;
}

string command = args.FirstOrDefault() ?? "status";
string? configPath = null;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--config" && i + 1 < args.Length)
    {
        configPath = Path.GetFullPath(args[++i]);
    }
    else
    {
        Console.Error.WriteLine($"Unknown argument: {args[i]}");
        return 2;
    }
}

try
{
    if (command == "logs")
    {
        string path = Path.Combine(Configuration.DataDirectory, "raumklang.log");
        Console.WriteLine(File.Exists(path) ? string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(100)) : "No logs yet.");
        return 0;
    }

    if (command is "status" or "stop")
    {
        SessionStatus? status = await InstanceControl.RequestAsync(command);
        if (status == null)
        {
            try
            {
                using var check = new InstanceLock();
            }
            catch (IOException)
            {
                Console.Error.WriteLine("State: Unresponsive. Worker lock is held, but its control pipe is unavailable. Check logs.");
                return 1;
            }

            Console.WriteLine("State: Stopped (no worker responding)");
            return command == "stop" ? 0 : 3;
        }

        if (command == "status")
        {
            Console.WriteLine(JsonSerializer.Serialize(status, Configuration.Json));
            return status.State == "Failed" ? 1 : 0;
        }

        // Stop completes when the worker releases its lock.
        DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < until)
        {
            try
            {
                using var check = new InstanceLock();
                Console.WriteLine("State: Stopped");
                return 0;
            }
            catch (IOException)
            {
                await Task.Delay(200);
            }
        }

        Console.Error.WriteLine("Stop has not finished. Check logs.");
        return 1;
    }

    var config = Configuration.Load(configPath);
    Log.Verbose = config.LogLevel == "debug";
    if (command == "doctor")
    {
        return await DoctorAsync(config);
    }

    if (command == "start")
    {
        SessionStatus? existing = await InstanceControl.RequestAsync("status");
        if (existing?.State == "Streaming")
        {
            Console.WriteLine(JsonSerializer.Serialize(existing, Configuration.Json));
            return 0;
        }

        Process? launched = null;
        if (existing == null)
        {
            string path = Environment.ProcessPath!;
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            }

            info.ArgumentList.Add("worker");
            if (configPath != null)
            {
                info.ArgumentList.Add("--config");
                info.ArgumentList.Add(configPath);
            }

            launched = Process.Start(info) ?? throw new IOException("Could not start background worker.");
        }

        using (launched)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(config.StartupTimeoutSeconds + 15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                SessionStatus? current = await InstanceControl.RequestAsync("status", 700);
                if (current?.State == "Streaming")
                {
                    Console.WriteLine(JsonSerializer.Serialize(current, Configuration.Json));
                    return 0;
                }

                if (current?.State is "Failed" or "Stopped")
                {
                    throw new IOException(current.Error ?? "Worker stopped during startup.");
                }

                if (current == null && launched?.HasExited == true)
                {
                    // A racing start can lose the lock. Give the winner a chance to open IPC.
                    await Task.Delay(400);
                    if (await InstanceControl.RequestAsync("status") == null)
                    {
                        throw new IOException("Worker exited during startup. Run logs for the cause.");
                    }
                }

                await Task.Delay(300);
            }
        }

        throw new IOException("Startup did not finish within its deadline. Inspect status/logs; use stop to cancel partial startup.");
    }

    if (command is not ("run" or "worker"))
    {
        Console.Error.WriteLine("Usage: Raumklang.DesktopStream start|stop|status|doctor|logs|run [--config path]");
        return 2;
    }

    InstanceLock instance;
    try
    {
        instance = new InstanceLock();
    }
    catch (IOException)
    {
        Console.WriteLine("Raumklang Desktop Stream is already running for this user.");
        return 0;
    }

    using (instance)
    using (var lifetime = new CancellationTokenSource())
    using (var ipcStop = new CancellationTokenSource())
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            lifetime.Cancel();
        };
        var session = new Session(config);
        Task ipc = InstanceControl.ListenAsync(session, lifetime, ipcStop.Token);
        Task<int> run = session.RunAsync(lifetime.Token);
        try
        {
            if (await Task.WhenAny(run, ipc) == ipc)
            {
                await lifetime.CancelAsync();
                await run;
                await ipc;
                throw new IOException("Local control pipe stopped unexpectedly.");
            }

            return await run;
        }
        finally
        {
            await ipcStop.CancelAsync();
            await ipc;
        }
    }
}
catch (Exception ex)
{
    Log.Write($"ERROR: {ex.Message}");
    if (Log.Verbose)
    {
        Log.Write(ex.ToString());
    }

    return 1;
}

static async Task<int> DoctorAsync(Configuration config)
{
    Console.WriteLine($"[OK] Windows; runtime {Environment.Version}; {Environment.ProcessPath}");
    int errors = 0;
    try
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (MMDevice? device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                Console.WriteLine($"Audio: {device.FriendlyName} [{device.ID}]");
            }
        }

        using MMDevice selected = AudioPipeline.SelectDevice(config.AudioDevice);
        Console.WriteLine($"[OK] Selected output: {selected.FriendlyName}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] Cannot capture audio: {ex.Message}");
        errors++;
    }

    foreach (NetworkAddress adapter in Network.Interfaces())
    {
        string kind = Network.VirtualAdapter(adapter.Name) ? " virtual/VPN" : "";
        Console.WriteLine($"Interface: {adapter.Name} {adapter.Address}{kind}");
    }

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(4)
    };
    try
    {
        Uri host = await Network.DiscoverAsync(config, http, timeout.Token);
        var client = new RaumfeldClient(http, host);
        Console.WriteLine($"[OK] Raumfeld host: {host}");
        IReadOnlyList<Room> rooms = await client.GetRoomsAsync(timeout.Token);
        foreach (Room r in rooms)
        {
            Console.WriteLine($"Room: {r.Name} [{r.Udn}]; zone={r.ZoneUdn ?? "unassigned"}; power={r.PowerState}");
        }

        Room room = Protocol.ResolveRoom(rooms, config.RoomName);
        Console.WriteLine($"[OK] Selected room: {room.Name}");
        IPAddress hostIp = (await Dns.GetHostAddressesAsync(host.Host, timeout.Token)).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        IPAddress local = Network.LocalAddress(hostIp, config.LocalAddress);
        Console.WriteLine($"[OK] Selected LAN address: {local}");
        SessionStatus? active = await InstanceControl.RequestAsync("status");
        if (active?.StreamUrl != null)
        {
            await Session.ProbeStreamAsync(new Uri(active.StreamUrl), http, timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(active, Configuration.Json));
            if (!active.ReceiverConnected)
            {
                Console.WriteLine("[WARN] No current receiver consumption. Local HTTP success cannot prove inbound Windows Firewall access.");
            }
        }
        else
        {
            // Check capture and HTTP without loading media on the receiver.
            await using var server = new Raumklang.DesktopStream.Streaming.AudioHttpServer();
            await server.StartAsync(local, config.StreamPort, hostIp, timeout.Token);
            using var audio = new AudioPipeline(config, server);
            using var audioStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            Task task = audio.RunAsync(audioStop.Token);
            try
            {
                await Session.ProbeStreamAsync(server.Url, http, timeout.Token);
                Console.WriteLine("[OK] WASAPI, native MP3 encoder and HTTP listener smoke test");
            }
            finally
            {
                await audioStop.CancelAsync();
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                }
            }

            Console.WriteLine("[INFO] No active session. Receiver playback and remote firewall access are unverified; doctor does not create zones or load media.");
        }

        if (room.ZoneUdn != null)
        {
            Renderer? renderer = await client.ResolveRendererAsync(room.ZoneUdn, timeout.Token);
            if (renderer != null)
            {
                Console.WriteLine($"Receiver: {JsonSerializer.Serialize(await client.GetStateAsync(renderer, timeout.Token))}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] {ex.Message}");
        errors++;
    }

    return errors == 0 ? 0 : 1;
}
