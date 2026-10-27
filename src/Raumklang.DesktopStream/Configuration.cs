using System.Text.Json;

namespace Raumklang.DesktopStream;

public sealed record Configuration
{
    public string? RoomName { get; init; }
    public string RaumfeldHost { get; init; } = "auto";
    public int RaumfeldPort { get; init; } = 47365;
    public string AudioDevice { get; init; } = "default";
    public string? LocalAddress { get; init; }
    public int StreamPort { get; init; }
    public int BitrateKbps { get; init; } = 192;
    public int StartupTimeoutSeconds { get; init; } = 90;
    public string LogLevel { get; init; } = "info";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raumklang.DesktopStream");

    public static Configuration Load(string? path)
    {
        path ??= Path.Combine(DataDirectory, "config.json");
        Configuration config;
        if (File.Exists(path))
        {
            config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Empty configuration.");
        }
        else if (path == Path.Combine(DataDirectory, "config.json"))
        {
            config = new Configuration();
        }
        else
        {
            throw new FileNotFoundException("Configuration not found.", path);
        }

        if (config.StreamPort is < 0 or > 65535 || config.RaumfeldPort is < 1 or > 65535)
        {
            throw new InvalidDataException("Ports must be 1..65535. streamPort 0 selects a free port.");
        }

        if (config.BitrateKbps is not (96 or 128 or 160 or 192 or 224 or 256 or 320))
        {
            throw new InvalidDataException("bitrateKbps must be 96, 128, 160, 192, 224, 256 or 320.");
        }

        if (config.StartupTimeoutSeconds is < 10 or > 300)
        {
            throw new InvalidDataException("startupTimeoutSeconds must be 10..300.");
        }

        if (config.LogLevel is not ("info" or "debug"))
        {
            throw new InvalidDataException("logLevel must be info or debug.");
        }

        return config;
    }
}

public static class Log
{
    private static readonly Lock s_gate = new();
    public static bool Verbose { get; set; }

    public static void Write(string message)
    {
        string line = $"{DateTimeOffset.Now:O} {message}";
        lock (s_gate)
        {
            Console.WriteLine(line);
            Directory.CreateDirectory(Configuration.DataDirectory);
            string path = Path.Combine(Configuration.DataDirectory, "raumklang.log");
            if (File.Exists(path) && new FileInfo(path).Length > 5_000_000)
            {
                File.Move(path, path + ".1", true);
            }

            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    public static void Debug(string message)
    {
        if (Verbose)
        {
            Write(message);
        }
    }
}
