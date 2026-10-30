using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WinIsland.Services;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;

    public event Action? ToggleRequested;
    public event Action? SettingsRequested;
    public event Action? TestNotificationRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Renderer = new ToolStripProfessionalRenderer()
        };

        var toggle = new ToolStripMenuItem("展开 / 收起  (Ctrl+Alt+I)", null, (_, _) => ToggleRequested?.Invoke())
        {
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        menu.Items.Add(toggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("设置", null, (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("发送测试通知", null, (_, _) => TestNotificationRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出 WinIsland", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "WinIsland 灵动岛",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ToggleRequested?.Invoke();
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var path = RoundedRect(new RectangleF(2, 8, 28, 16), 8f);
            using var brush = new LinearGradientBrush(
                new RectangleF(0, 0, 32, 32),
                Color.FromArgb(255, 108, 140, 255),
                Color.FromArgb(255, 176, 108, 255),
                30f);
            graphics.FillPath(brush, path);

            using var dot = new SolidBrush(Color.White);
            graphics.FillEllipse(dot, 21, 13, 6, 6);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
