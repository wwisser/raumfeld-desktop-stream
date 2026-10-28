using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Raumfeld.DesktopStream.Runtime;

public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _file;
    public InstanceLock(string? path = null)
    {
        Directory.CreateDirectory(Configuration.DataDirectory);
        _file = new FileStream(path ?? Path.Combine(Configuration.DataDirectory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose()
    {
        _file.Dispose();
    }
}

public static class InstanceControl
{
    public static string PipeName => "Raumfeld.DesktopStream-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value)))[..24];

    public static async Task<SessionStatus?> RequestAsync(string command, int timeoutMs = 1500, string? pipeName = null)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        await using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync(command.AsMemory(), timeout.Token);
            string? line = await reader.ReadLineAsync(timeout.Token);
            return line == null ? null : JsonSerializer.Deserialize<SessionStatus>(line);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException)
        {
            return null;
        }
    }

    public static async Task ListenAsync(Session session, CancellationTokenSource lifetime, CancellationToken cancellationToken, string? pipeName = null)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(pipeName ?? PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true)
                {
                    AutoFlush = true
                };
                string? command = await reader.ReadLineAsync(timeout.Token);
                if (command is not ("status" or "stop"))
                {
                    continue;
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(session.Snapshot).AsMemory(), timeout.Token);
                if (command == "stop")
                {
                    await lifetime.CancelAsync();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
            }
        }
    }
}
