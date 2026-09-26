using System.Drawing.Drawing2D;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal enum TrayIconState
{
    Idle,
    Recording,
    Paused,
    Saving
}

internal sealed class TrayIcons : IDisposable
{
    private readonly Icon _idle = CreateIcon(TrayIconState.Idle);
    private readonly Icon _recording = CreateIcon(TrayIconState.Recording);
    private readonly Icon _paused = CreateIcon(TrayIconState.Paused);
    private readonly Icon _saving = CreateIcon(TrayIconState.Saving);

    public Icon For(VideoRecordingState state) => state switch
    {
        VideoRecordingState.Recording => _recording,
        VideoRecordingState.Paused => _paused,
        VideoRecordingState.Saving => _saving,
        _ => _idle
    };

    public Icon Idle => _idle;

    public void Dispose()
    {
        _idle.Dispose();
        _recording.Dispose();
        _paused.Dispose();
        _saving.Dispose();
    }

    private static Icon CreateIcon(TrayIconState state)
    {
        // 待機中だけアプリの図柄にする。図柄にも赤い丸があるため、録画中などに印を重ねると小さいトレイでは見分けにくい。
        if (state == TrayIconState.Idle) return AppIcon.Create(SystemInformation.SmallIconSize);
        var color = state switch
        {
            TrayIconState.Recording => Color.Firebrick,
            TrayIconState.Paused => Color.DarkOrange,
            TrayIconState.Saving => Color.DimGray,
            _ => Color.FromArgb(35, 110, 190)
        };
        using var bitmap = new Bitmap(32, 32);
        using var indicator = new Pen(Color.White, 2);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(color))
        using (var border = new Pen(Color.White, 2))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            graphics.FillRoundedRectangle(brush, new Rectangle(2, 2, 28, 28), 6);
            graphics.DrawRoundedRectangle(border, new Rectangle(2, 2, 28, 28), 6);
            switch (state)
            {
                case TrayIconState.Recording:
                    graphics.FillEllipse(Brushes.White, 11, 11, 10, 10);
                    break;
                case TrayIconState.Paused:
                    graphics.FillRectangle(Brushes.White, 9, 9, 5, 14);
                    graphics.FillRectangle(Brushes.White, 18, 9, 5, 14);
                    break;
                case TrayIconState.Saving:
                    graphics.DrawRectangle(indicator, 9, 9, 14, 14);
                    graphics.DrawLine(Pens.White, 16, 12, 16, 16);
                    graphics.DrawLine(Pens.White, 16, 16, 20, 18);
                    break;
                default:
                    graphics.DrawRectangle(Pens.White, 9, 9, 14, 14);
                    break;
            }
        }
        var iconHandle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(iconHandle);
            return (Icon)icon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(iconHandle);
        }
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
