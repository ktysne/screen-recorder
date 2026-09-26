using System.Diagnostics;
using System.Drawing.Drawing2D;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly Settings _settings;
    private readonly DailyLog _log;
    private readonly NotifyIcon _tray;

    public TrayApplicationContext(Settings settings, DailyLog log)
    {
        _settings = settings;
        _log = log;
        var menu = new ContextMenuStrip();
        menu.Items.Add(CreateCaptureMenu(UiLabels.Screenshot));
        menu.Items.Add(CreateCaptureMenu(UiLabels.Record));
        var pauseResume = new ToolStripMenuItem(UiLabels.PauseResume, null, (_, _) => NotifyNotImplemented()) { Enabled = false, ShortcutKeyDisplayString = _settings.PauseRecordingShortcut };
        menu.Items.Add(pauseResume);
        menu.Items.Add(new ToolStripMenuItem(UiLabels.StopRecording, null, (_, _) => NotifyNotImplemented()) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(UiLabels.OpenImageFolder, null, (_, _) => OpenFolder(_settings.StillImageDirectory)));
        menu.Items.Add(new ToolStripMenuItem(UiLabels.OpenVideoFolder, null, (_, _) => OpenFolder(_settings.VideoDirectory)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(UiLabels.Settings, null, (_, _) => NotifyNotImplemented()));
        menu.Items.Add(new ToolStripMenuItem(UiLabels.Manual, null, (_, _) => NotifyNotImplemented()));
        menu.Items.Add(new ToolStripMenuItem(UiLabels.CheckForUpdates, null, (_, _) => NotifyNotImplemented()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(UiLabels.Exit, null, (_, _) => ExitThread()));
        _tray = new NotifyIcon { Text = "ScreenRecorder", Icon = CreateIcon(false), ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => NotifyNotImplemented();
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }

    private ToolStripMenuItem CreateCaptureMenu(string label)
    {
        var item = new ToolStripMenuItem(label);
        var screenshot = label == UiLabels.Screenshot;
        item.DropDownItems.Add(CaptureMenuItem(UiLabels.FullDisplay, screenshot ? _settings.ScreenshotFullScreenShortcut : _settings.RecordingFullScreenShortcut));
        item.DropDownItems.Add(CaptureMenuItem(UiLabels.SelectRegion, screenshot ? _settings.ScreenshotRegionShortcut : _settings.RecordingRegionShortcut));
        item.DropDownItems.Add(CaptureMenuItem(UiLabels.SelectWindow, screenshot ? _settings.ScreenshotWindowShortcut : _settings.RecordingWindowShortcut));
        return item;
    }

    private ToolStripMenuItem CaptureMenuItem(string label, string shortcut) => new(label, null, (_, _) => NotifyNotImplemented()) { ShortcutKeyDisplayString = shortcut };

    private void NotifyNotImplemented() => _tray.ShowBalloonTip(2500, "ScreenRecorder", "この機能はまだ利用できません。", ToolTipIcon.Info);

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _log.Write($"Open folder failed: {exception}");
            _tray.ShowBalloonTip(3000, "ScreenRecorder", $"フォルダーを開けませんでした: {exception.Message}", ToolTipIcon.Error);
        }
    }

    private static Icon CreateIcon(bool recording)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(recording ? Color.Firebrick : Color.FromArgb(35, 110, 190)))
        using (var border = new Pen(Color.White, 2))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            graphics.FillRoundedRectangle(brush, new Rectangle(2, 2, 28, 28), 6);
            graphics.DrawRoundedRectangle(border, new Rectangle(2, 2, 28, 28), 6);
            if (recording) graphics.FillEllipse(Brushes.White, 11, 11, 10, 10);
            else graphics.DrawRectangle(Pens.White, 9, 9, 14, 14);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = RoundedRectangle(bounds, radius);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using var path = RoundedRectangle(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
