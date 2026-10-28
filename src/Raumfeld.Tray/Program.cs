using System.Diagnostics;
using Microsoft.Win32;
using Raumfeld.DesktopStream;
using Raumfeld.DesktopStream.Runtime;

namespace Raumfeld.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Classic);
        using var instance = new Mutex(true, @"Local\Raumfeld.Tray-" + InstanceControl.PipeName, out bool first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Raumfeld.Show-" + InstanceControl.PipeName);
        if (!first)
        {
            show.Set();
            return;
        }

        string? config = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length)
            {
                config = Path.GetFullPath(args[++i]);
            }
            else if (args[i] != "--hidden")
            {
                MessageBox.Show("Usage: Raumfeld.Tray [--hidden] [--config path]", "Raumfeld");
                return;
            }
        }

        using var tray = new TrayContext(config, show);
        if (!args.Contains("--hidden"))
        {
            tray.ShowPanel();
        }

        Application.Run(tray);
    }
}

internal static class LoginStartup
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RaumfeldDesktopStream";

    public static bool Enabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled, string? configPath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled)
        {
            string command = $"\"{Environment.ProcessPath}\" --hidden";
            if (configPath != null)
            {
                command += $" --config \"{configPath}\"";
            }

            key.SetValue(ValueName, command);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ControlPanel _panel;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1200 };
    private readonly EventWaitHandle _show;
    private readonly string? _configPath;
    private readonly Icon _art;
    private bool _polling;
    private bool _busy;
    private bool _exiting;
    private SessionStatus? _status;
    private Process? _worker;
    private string? _error;

    public TrayContext(string? configPath, EventWaitHandle show)
    {
        _configPath = configPath;
        _show = show;
        using Stream image = typeof(TrayContext).Assembly.GetManifestResourceStream("Raumfeld.Tray.Assets.raumfeld.ico")!;
        _art = new Icon(image);
        _panel = new ControlPanel(_art);
        _panel.ToggleRequested += async (_, _) => await ToggleAsync();
        _panel.QuitRequested += async (_, _) => await QuitAsync();
        _panel.StartupChanged += (_, _) =>
        {
            try
            {
                LoginStartup.Set(_panel.StartupEnabled, _configPath);
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                _panel.StartupEnabled = LoginStartup.Enabled;
                Render();
            }
        };
        _panel.LogsRequested += (_, _) => OpenFile(Path.Combine(Configuration.DataDirectory, "raumfeld.log"));
        var menu = new ContextMenuStrip
        {
            RenderMode = ToolStripRenderMode.System,
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Font = new Font("Segoe UI", 9)
        };
        menu.Items.Add("Open Raumfeld", null, (_, _) => ShowPanel());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Raumfeld", null, async (_, _) => await QuitAsync());
        _icon = new NotifyIcon { Icon = _art, Text = "Raumfeld · Ready", Visible = true, ContextMenuStrip = menu };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_panel.Visible)
                {
                    _panel.Hide();
                }
                else
                {
                    ShowPanel();
                }
            }
        };
        _timer.Tick += async (_, _) =>
        {
            if (_show.WaitOne(0))
            {
                ShowPanel();
            }

            await RefreshAsync();
        };
        _timer.Start();
        Render();
    }

    public void ShowPanel()
    {
        _panel.StartupEnabled = LoginStartup.Enabled;
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        int gap = (int)Math.Round(24 * _panel.DeviceDpi / 96d);
        int bottomGap = (int)Math.Round(88 * _panel.DeviceDpi / 96d);
        int x = Math.Clamp(Cursor.Position.X - (_panel.Width / 2), area.Left + gap, Math.Max(area.Left + gap, area.Right - _panel.Width - gap));
        int y = Math.Max(area.Top + gap, Math.Min(area.Bottom - _panel.Height - bottomGap, Cursor.Position.Y - _panel.Height - gap));
        _panel.Location = new Point(x, y);
        // Reset visibility when a launcher supplied a hidden startup window state.
        _panel.Hide();
        _panel.Show();
        _panel.Activate();
    }

    private async Task RefreshAsync()
    {
        if (_polling || _exiting)
        {
            return;
        }

        _polling = true;
        try
        {
            _status = await InstanceControl.RequestAsync("status", 600);
            if (_worker?.HasExited == true)
            {
                if (_worker.ExitCode != 0)
                {
                    _error = "The stream could not continue. Open logs for details, then try again.";
                }

                _worker.Dispose();
                _worker = null;
            }

            Render();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Render();
        }
        finally
        {
            _polling = false;
        }
    }

    private void Render()
    {
        bool active = _status != null || _worker is { HasExited: false };
        _panel.Render(_status, active, _busy, _error);
        string text = "Raumfeld · " + (_status?.State ?? (active ? "Starting" : "Ready"));
        _icon.Text = text[..Math.Min(63, text.Length)];
    }

    private async Task ToggleAsync()
    {
        if (_busy || _exiting)
        {
            return;
        }

        _busy = true;
        _error = null;
        Render();
        try
        {
            _status = await InstanceControl.RequestAsync("status", 600);
            if (_status != null || _worker is { HasExited: false })
            {
                await StopAsync();
            }
            else
            {
                Configuration.Load(_configPath);
                var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Raumfeld.DesktopStream.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                info.ArgumentList.Add("worker");
                if (_configPath != null)
                {
                    info.ArgumentList.Add("--config");
                    info.ArgumentList.Add(_configPath);
                }

                _worker = Process.Start(info) ?? throw new IOException("Could not start the stream.");
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
            await RefreshAsync();
            Render();
        }
    }

    private async Task StopAsync()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(22);
        // A freshly launched worker may still be opening its pipe.
        while (DateTimeOffset.UtcNow < deadline)
        {
            await InstanceControl.RequestAsync("stop", 600);
            try
            {
                using var check = new InstanceLock();
                if (_worker == null || _worker.HasExited)
                {
                    _status = null;
                    _worker?.Dispose();
                    _worker = null;
                    return;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(200);
        }

        throw new IOException("Stopping is taking longer than expected. Check logs and try again.");
    }

    private async Task QuitAsync()
    {
        if (_busy || _exiting)
        {
            return;
        }

        _exiting = true;
        _busy = true;
        Render();
        try
        {
            await StopAsync();
            ExitThread();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _exiting = false;
            _busy = false;
            Render();
            ShowPanel();
        }
    }

    private void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                throw new IOException("No log file yet. Start a stream first.");
            }

            var info = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
            info.ArgumentList.Add(path);
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Render();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
            _panel.Dispose();
            _art.Dispose();
            _worker?.Dispose();
        }

        base.Dispose(disposing);
    }
}
