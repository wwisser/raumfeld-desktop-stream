using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Raumfeld.DesktopStream.Streaming;

public sealed class StreamClient(IPAddress? address) : IDisposable
{
    private readonly Lock _gate = new();
    private bool _ended;
    public Guid Id { get; } = Guid.NewGuid();
    public IPAddress? Address { get; } = address;
    public Channel<byte[]> Queue { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    public CancellationTokenSource End { get; } = new();

    public long Bytes;
    public long LastWriteTick;
    public void Dispose()
    {
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            Queue.Writer.TryComplete();
            End.Cancel();
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            Dispose();
            End.Dispose();
        }
    }
}

// Disconnect slow clients when their queues fill so capture can continue.
public sealed class AudioHttpServer : Stream, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, StreamClient> _clients = new();
    private WebApplication? _app;
    private long _encodedBytes;
    private long _sentBytes;
    private long _slowClients;
    private int _disposed;
    private IPAddress? _receiverAddress;
    public Uri Url { get; private set; } = null!;
    public long EncodedBytes => Interlocked.Read(ref _encodedBytes);
    public long SentBytes => Interlocked.Read(ref _sentBytes);
    public long SlowClients => Interlocked.Read(ref _slowClients);
    public int ClientCount => _clients.Count;
    public long ReceiverBytes => _clients.Values.Where(IsReceiver).Sum(c => Interlocked.Read(ref c.Bytes));
    public bool ReceiverConnected => _clients.Values.Any(c => IsReceiver(c) && Environment.TickCount64 - Interlocked.Read(ref c.LastWriteTick) < 5000 && Interlocked.Read(ref c.Bytes) > 0);

    private bool IsReceiver(StreamClient c)
    {
        return c.Address != null && _receiverAddress != null && c.Address.MapToIPv4().Equals(_receiverAddress.MapToIPv4());
    }

    public async Task StartAsync(IPAddress local, int port, IPAddress receiver, CancellationToken cancellationToken)
    {
        _receiverAddress = receiver;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(local, port);
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.MinResponseDataRate = null;
        });
        _app = builder.Build();
        _app.MapMethods("/stream/live.mp3", ["GET", "HEAD"], ServeAsync);
        await _app.StartAsync(cancellationToken);
        ICollection<string> addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Url = new Uri(addresses.Single().TrimEnd('/') + "/stream/live.mp3");
        Log.Write($"HTTP MP3 listener: {Url}");
    }

    private async Task ServeAsync(HttpContext context)
    {
        context.Response.ContentType = "audio/mpeg";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["transferMode.dlna.org"] = "Streaming";
        context.Response.Headers["Accept-Ranges"] = "none";
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        var client = new StreamClient(context.Connection.RemoteIpAddress);
        _clients[client.Id] = client;
        Log.Write($"HTTP consumer connected: {client.Address}");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, client.End.Token);
        try
        {
            await context.Response.StartAsync(cancel.Token);
            await context.Response.Body.FlushAsync(cancel.Token);
            await foreach (byte[] bytes in client.Queue.Reader.ReadAllAsync(cancel.Token))
            {
                await context.Response.Body.WriteAsync(bytes, cancel.Token);
                await context.Response.Body.FlushAsync(cancel.Token);
                Interlocked.Add(ref client.Bytes, bytes.Length);
                Interlocked.Add(ref _sentBytes, bytes.Length);
                Interlocked.Exchange(ref client.LastWriteTick, Environment.TickCount64);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            client.Release();
            context.Abort();
            Log.Write($"HTTP consumer disconnected: {client.Address}; bytes={client.Bytes}");
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count == 0)
        {
            return;
        }

        Interlocked.Add(ref _encodedBytes, count);
        if (_clients.IsEmpty)
        {
            return;
        }

        byte[] copy = buffer.AsSpan(offset, count).ToArray();
        foreach (StreamClient client in _clients.Values)
        {
            if (!client.Queue.Writer.TryWrite(copy))
            {
                Interlocked.Increment(ref _slowClients);
                client.Dispose();
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (StreamClient client in _clients.Values)
        {
            client.Dispose();
        }

        if (_app != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(timeout.Token);
            await _app.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }
}
