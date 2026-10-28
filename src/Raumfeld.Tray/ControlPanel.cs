using Raumfeld.DesktopStream.Runtime;

namespace Raumfeld.Tray;

internal sealed class ControlPanel : Form
{
    private static readonly Color s_muted = Color.FromArgb(85, 85, 85);
    private readonly Label _state;
    private readonly Label _room;
    private readonly Label _capture;
    private readonly Button _toggle;
    private readonly CheckBox _startup;
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private readonly PictureBox _logo;
    private bool _updating;

    public event EventHandler? ToggleRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler? LogsRequested;
    public event EventHandler? StartupChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool StartupEnabled
    {
        get => _startup.Checked;
        set
        {
            _updating = true;
            _startup.Checked = value;
            _updating = false;
        }
    }

    public ControlPanel(Icon icon)
    {
        Text = "Raumfeld Desktop";
        Icon = icon;
        BackColor = Color.White;
        ForeColor = Color.Black;
        Font = new Font("Segoe UI", 9);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(360, 224);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Padding = new Padding(16);

        using Stream image = typeof(ControlPanel).Assembly.GetManifestResourceStream("Raumfeld.Tray.Assets.raumfeld.png")!;
        _logo = new PictureBox
        {
            Location = new Point(148, 6),
            Size = new Size(64, 64),
            Image = new Bitmap(image),
            SizeMode = PictureBoxSizeMode.Zoom,
            AccessibleName = "Raumfeld Desktop logo"
        };
        Controls.Add(_logo);
        _state = AddLabel("Ready to stream", 16, 72, 328, 22, 10, Color.Black, FontStyle.Bold);
        AddLabel("ROOM", 16, 103, 156, 16, 7, s_muted);
        AddLabel("AUDIO SOURCE", 188, 103, 156, 16, 7, s_muted);
        _room = AddLabel("Automatic discovery", 16, 122, 156, 20, 9, Color.Black);
        _capture = AddLabel("Windows default output", 188, 122, 156, 20, 9, Color.Black);
        _toggle = AddButton("Start streaming", 16, 153, 328, 30, true);
        _toggle.Click += (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty);
        _startup = new CheckBox
        {
            Text = "Open at Windows login",
            Location = new Point(20, 195),
            Size = new Size(194, 20),
            ForeColor = Color.Black,
            BackColor = Color.White,
            UseVisualStyleBackColor = false,
            AccessibleName = "Open at Windows login"
        };
        _startup.CheckedChanged += (_, _) =>
        {
            if (!_updating)
            {
                StartupChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        Controls.Add(_startup);
        Button logs = AddButton("Logs", 222, 191, 54, 28);
        logs.Click += (_, _) => LogsRequested?.Invoke(this, EventArgs.Empty);
        Button quit = AddButton("Quit", 286, 191, 54, 28);
        quit.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
        Deactivate += (_, _) => Hide();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    public void Render(SessionStatus? status, bool active, bool busy, string? error)
    {
        string phase = status?.State switch
        {
            "Streaming" when !status.ReceiverConnected => "Reconnecting to speaker",
            "Streaming" => "Streaming live",
            "Discovering" => "Finding your speakers",
            "EnsuringZone" => "Preparing your room",
            "Capturing" => "Connecting audio",
            "Loading" or "Buffering" => "Buffering audio",
            "Failed" => "Stream interrupted",
            "Superseded" => "Speaker playing other audio",
            _ => active ? "Connecting…" : "Ready to stream"
        };
        string? issue = error ?? status?.Error;
        _state.Text = string.IsNullOrEmpty(issue) ? phase : issue;
        _tips.SetToolTip(_state, issue ?? (status == null ? phase :
            $"{phase} · {status.Format} · {status.HttpBytes / 1_048_576d:0.0} MB sent"));
        _room.Text = status?.Room ?? "Automatic discovery";
        _capture.Text = status?.Capture ?? "Windows default output";
        _tips.SetToolTip(_room, _room.Text);
        _tips.SetToolTip(_capture, _capture.Text);
        _toggle.Text = busy ? "Please wait…" : active ? "Stop streaming" : "Start streaming";
        _toggle.Enabled = !busy;
    }

    private Label AddLabel(string text, int x, int y, int width, int height, float size, Color color, FontStyle style = FontStyle.Regular)
    {
        var label = new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, height),
            Font = new Font("Segoe UI", size, style),
            ForeColor = color,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(label);
        return label;
    }

    private Button AddButton(string text, int x, int y, int width, int height, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, height),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Color.Black : Color.White,
            ForeColor = primary ? Color.White : Color.Black,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9, primary ? FontStyle.Bold : FontStyle.Regular),
            AccessibleName = text,
            TextAlign = ContentAlignment.MiddleCenter
        };
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(215, 215, 215);
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(50, 50, 50) : Color.FromArgb(238, 238, 238);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(75, 75, 75) : Color.FromArgb(220, 220, 220);
        Controls.Add(button);
        return button;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Color.FromArgb(210, 210, 210));
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
            _logo.Image?.Dispose();
        }

        base.Dispose(disposing);
    }
}
