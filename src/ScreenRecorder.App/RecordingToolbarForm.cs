using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class RecordingToolbarForm : CaptureExcludedOverlayForm
{
    private readonly Label _status = new()
    {
        AutoSize = true,
        AccessibleName = "録画の状態と経過時間",
        AccessibleRole = AccessibleRole.StatusBar,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(8, 0, 8, 0)
    };
    private readonly Button _pauseResume = new()
    {
        AutoSize = true,
        MinimumSize = new Size(88, 32),
        AccessibleName = UiLabels.PauseRecording
    };
    private readonly Button _stop = new()
    {
        Text = UiLabels.StopRecording,
        AutoSize = true,
        MinimumSize = new Size(88, 32),
        AccessibleName = UiLabels.StopRecording
    };

    public RecordingToolbarForm(Point location)
    {
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        Location = location;
        ClientSize = new Size(356, 48);
        BackColor = Color.FromArgb(35, 35, 35);
        ForeColor = Color.White;
        TopMost = true;
        ShowInTaskbar = false;
        Padding = new Padding(6, 4, 6, 4);

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = BackColor,
            ForeColor = ForeColor,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        _status.ForeColor = ForeColor;
        layout.Controls.Add(_status);
        layout.Controls.Add(_pauseResume);
        layout.Controls.Add(_stop);
        Controls.Add(layout);

        _pauseResume.Click += (_, _) => PauseResumeRequested?.Invoke(this, EventArgs.Empty);
        _stop.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? PauseResumeRequested;
    public event EventHandler? StopRequested;

    public void UpdateStatus(VideoRecordingState state, TimeSpan elapsed)
    {
        var duration = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        _status.Text = state switch
        {
            VideoRecordingState.Preparing => UiLabels.RecordingPreparing,
            VideoRecordingState.Paused => $"一時停止中  {duration}",
            VideoRecordingState.Saving => "保存中",
            _ => $"録画中  {duration}"
        };
        _status.AccessibleDescription = _status.Text;
        _pauseResume.Text = state == VideoRecordingState.Paused ? UiLabels.ResumeRecording : UiLabels.PauseRecording;
        _pauseResume.AccessibleName = _pauseResume.Text;
        _pauseResume.Enabled = state is VideoRecordingState.Recording or VideoRecordingState.Paused;
        _stop.Enabled = state is VideoRecordingState.Preparing or VideoRecordingState.Recording or VideoRecordingState.Paused;
        AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
    }
}

internal sealed class RecordingRegionFrameForm : CaptureExcludedOverlayForm
{
    private const int BorderWidth = 3;

    public RecordingRegionFrameForm(Rectangle bounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Rectangle.Inflate(bounds, BorderWidth, BorderWidth);
        BackColor = Color.Lime;
        TransparencyKey = Color.Lime;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.Red, BorderWidth);
        e.Graphics.DrawRectangle(pen, new Rectangle(0, 0, Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1)));
    }
}

internal static class RecordingToolbarPlacement
{
    private const int Margin = 12;

    public static Point FindLocation(Rectangle targetBounds, Rectangle targetDisplayBounds, Size toolbarSize)
    {
        foreach (var displayBounds in new[] { targetDisplayBounds }.Concat(Screen.AllScreens.Select(screen => screen.Bounds).Where(bounds => bounds != targetDisplayBounds)))
        {
            var candidates = new[]
            {
                new Point(displayBounds.Left + Margin, displayBounds.Top + Margin),
                new Point(displayBounds.Right - toolbarSize.Width - Margin, displayBounds.Top + Margin),
                new Point(displayBounds.Left + Margin, displayBounds.Bottom - toolbarSize.Height - Margin),
                new Point(displayBounds.Right - toolbarSize.Width - Margin, displayBounds.Bottom - toolbarSize.Height - Margin)
            };
            var location = candidates.FirstOrDefault(candidate =>
                !new Rectangle(candidate, toolbarSize).IntersectsWith(targetBounds));
            if (candidates.Any(candidate => !new Rectangle(candidate, toolbarSize).IntersectsWith(targetBounds))) return location;
        }

        return new Point(targetDisplayBounds.Right - toolbarSize.Width - Margin, targetDisplayBounds.Top + Margin);
    }
}
