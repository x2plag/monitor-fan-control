using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;

namespace MonitorFanControl;

internal sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _keepTargetItem;
    private readonly Forms.ToolStripMenuItem _startWithWindowsItem;
    private readonly DrawingIcon? _applicationIcon;

    public event Action? OpenRequested;
    public event Action<bool>? KeepTargetChanged;
    public event Action<bool>? StartWithWindowsChanged;
    public event Action? ExitRequested;

    public TrayController(AppSettings settings)
    {
        _menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Open fan control");
        _keepTargetItem = new Forms.ToolStripMenuItem("Keep saved target applied")
        {
            CheckOnClick = true,
            Checked = settings.KeepTargetApplied
        };
        _startWithWindowsItem = new Forms.ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = WindowsStartupRegistration.IsEnabled()
        };
        var exitItem = new Forms.ToolStripMenuItem("Exit");

        openItem.Click += (_, _) => OpenRequested?.Invoke();
        _keepTargetItem.Click += (_, _) => KeepTargetChanged?.Invoke(_keepTargetItem.Checked);
        _startWithWindowsItem.Click += (_, _) => StartWithWindowsChanged?.Invoke(_startWithWindowsItem.Checked);
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _menu.Items.Add(openItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(_keepTargetItem);
        _menu.Items.Add(_startWithWindowsItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(executablePath))
            _applicationIcon = DrawingIcon.ExtractAssociatedIcon(executablePath);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon ?? DrawingSystemIcons.Application,
            Text = "Monitor Fan Control",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void UpdateSettings(AppSettings settings)
    {
        _keepTargetItem.Checked = settings.KeepTargetApplied;
        _startWithWindowsItem.Checked = WindowsStartupRegistration.IsEnabled();
    }

    // Reads the colors from the active WPF theme, so AppTheme.Apply must run first.
    public void ApplyTheme()
    {
        var palette = new TrayMenuPalette(
            Background: ThemeColor("Surface"),
            Text: ThemeColor("Text"),
            Border: ThemeColor("Border"),
            Hover: ThemeColor("CheckHoverFill"),
            Accent: ThemeColor("Accent"),
            CheckMark: ThemeColor("CheckMark"));
        _menu.Renderer = new TrayMenuRenderer(palette);
        _menu.BackColor = palette.Background;
        _menu.ForeColor = palette.Text;
        foreach (Forms.ToolStripItem item in _menu.Items)
            item.ForeColor = palette.Text;
    }

    private static DrawingColor ThemeColor(string key)
    {
        var color = ((System.Windows.Media.SolidColorBrush)System.Windows.Application.Current.Resources[key]).Color;
        return DrawingColor.FromArgb(color.A, color.R, color.G, color.B);
    }

    public void UpdateStatus(MonitorSnapshot snapshot)
    {
        var text = snapshot.CurrentDuty is byte duty
            ? $"Monitor Fan Control: {duty}% {(snapshot.IsConnected ? "connected" : "disconnected")}"
            : "Monitor Fan Control: monitor not connected";
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    public void ShowMessage(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message.Length <= 255 ? message : message[..255];
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Error;
        _notifyIcon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _applicationIcon?.Dispose();
    }

    private sealed record TrayMenuPalette(
        DrawingColor Background,
        DrawingColor Text,
        DrawingColor Border,
        DrawingColor Hover,
        DrawingColor Accent,
        DrawingColor CheckMark);

    private sealed class TrayMenuRenderer(TrayMenuPalette palette)
        : Forms.ToolStripProfessionalRenderer(new TrayMenuColorTable(palette))
    {
        // Without visual styles the base renderer ignores the color table and paints the system highlight.
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
            {
                base.OnRenderMenuItemBackground(e);
                return;
            }

            using var fill = new SolidBrush(palette.Hover);
            e.Graphics.FillRectangle(fill, new Rectangle(2, 0, e.Item.Width - 3, e.Item.Height));
        }

        // The stock check glyph is always black; draw one that matches the window's checkboxes.
        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var bounds = e.ImageRectangle;
            bounds.Inflate(-1, -1);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            var graphics = e.Graphics;
            var previousSmoothing = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using (var fill = new SolidBrush(palette.Accent))
            {
                var radius = Math.Max(2, bounds.Width / 4);
                graphics.FillRoundedRectangle(fill, bounds, new Size(radius, radius));
            }

            using (var pen = new Pen(palette.CheckMark, Math.Max(1.5f, bounds.Width / 8f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                graphics.DrawLines(pen,
                [
                    new PointF(bounds.X + bounds.Width * 0.26f, bounds.Y + bounds.Height * 0.52f),
                    new PointF(bounds.X + bounds.Width * 0.43f, bounds.Y + bounds.Height * 0.70f),
                    new PointF(bounds.X + bounds.Width * 0.75f, bounds.Y + bounds.Height * 0.31f)
                ]);
            }

            graphics.SmoothingMode = previousSmoothing;
        }
    }

    private sealed class TrayMenuColorTable : Forms.ProfessionalColorTable
    {
        private readonly TrayMenuPalette _palette;

        public TrayMenuColorTable(TrayMenuPalette palette)
        {
            _palette = palette;
            UseSystemColors = false;
        }

        public override DrawingColor ToolStripDropDownBackground => _palette.Background;
        public override DrawingColor ImageMarginGradientBegin => _palette.Background;
        public override DrawingColor ImageMarginGradientMiddle => _palette.Background;
        public override DrawingColor ImageMarginGradientEnd => _palette.Background;
        public override DrawingColor MenuBorder => _palette.Border;
        public override DrawingColor MenuItemBorder => _palette.Hover;
        public override DrawingColor MenuItemSelected => _palette.Hover;
        public override DrawingColor MenuItemSelectedGradientBegin => _palette.Hover;
        public override DrawingColor MenuItemSelectedGradientEnd => _palette.Hover;
        public override DrawingColor MenuItemPressedGradientBegin => _palette.Hover;
        public override DrawingColor MenuItemPressedGradientEnd => _palette.Hover;
        public override DrawingColor SeparatorDark => _palette.Border;
        public override DrawingColor SeparatorLight => _palette.Background;
        public override DrawingColor CheckBackground => _palette.Accent;
        public override DrawingColor CheckSelectedBackground => _palette.Accent;
        public override DrawingColor CheckPressedBackground => _palette.Accent;
    }
}
