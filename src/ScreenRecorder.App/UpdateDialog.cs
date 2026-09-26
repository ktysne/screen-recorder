using ScreenRecorder.Core;

namespace ScreenRecorder.App;

/// <summary>新しい版の通知から、ダウンロード、適用の開始までを 1 つの画面で見せる。</summary>
internal sealed class UpdateDialog : Form
{
    private const int ContentWidth = 480;
    private readonly Label _heading = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
    private readonly Label _statusText = new() { AutoSize = true, MaximumSize = new Size(ContentWidth - 28, 0), Margin = new Padding(0, 2, 0, 4) };
    private readonly Label _folderText = new() { AutoSize = true, MaximumSize = new Size(ContentWidth - 28, 0), Margin = new Padding(0, 0, 0, 4), Visible = false };
    private readonly PictureBox _statusIcon = new() { Size = new Size(20, 20), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 2, 8, 0), Visible = false };
    private readonly ProgressBar _progress = new() { Width = ContentWidth - 28, Height = 18, Margin = new Padding(0, 4, 0, 2), Visible = false, AccessibleName = UiLabels.UpdateDownloading };
    private readonly Label _progressText = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 4), Visible = false };
    private readonly TableLayoutPanel _statusPanel = new() { AutoSize = true, ColumnCount = 2, RowCount = 4, Margin = new Padding(0, 12, 0, 0), Visible = false };
    private readonly Button _primaryButton = new() { AutoSize = true, MinimumSize = new Size(112, 30) };
    private readonly Button _secondaryButton = new() { AutoSize = true, MinimumSize = new Size(96, 30) };
    private readonly Button _distributionPageButton = new() { Text = UiLabels.OpenDistributionPage, AutoSize = true, MinimumSize = new Size(96, 30), Visible = false };
    private readonly Button _skipButton = new() { Text = UiLabels.UpdateSkipVersion, AutoSize = true, MinimumSize = new Size(96, 30) };
    private readonly Font _headingFont;
    private readonly bool _installWritable;
    private Phase _phase = Phase.Ready;
    private string? _busyReason;
    private string? _failureReason;

    private enum Phase { Ready, Downloading, Verifying, Applying, Cancelled, Failed }

    public event EventHandler? UpdateRequested;
    public event EventHandler? SkipRequested;
    public event EventHandler? CancelDownloadRequested;
    public event EventHandler? DistributionPageRequested;

    public UpdateManifest Manifest { get; }

    public UpdateDialog(UpdateManifest manifest, string currentVersion, string installDirectory, bool installWritable)
    {
        Manifest = manifest;
        _installWritable = installWritable;
        _headingFont = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        Text = UiLabels.UpdateDialogTitle;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BuildLayout(currentVersion, installDirectory);
        Render();
    }

    public bool IsWorking => _phase is Phase.Downloading or Phase.Verifying or Phase.Applying;

    public void SetBusyReason(string? reason)
    {
        _busyReason = reason;
        Render();
    }

    public void ShowReady() => SetPhase(Phase.Ready);

    public void ShowDownloading(UpdateDownloadProgress progress)
    {
        if (progress.TotalBytes is > 0 and var total)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = (int)Math.Clamp(progress.ReceivedBytes * 100 / total, 0, 100);
            _progressText.Text = $"{FormatMegabytes(progress.ReceivedBytes)} / {FormatMegabytes(total)} ({_progress.Value}%)";
        }
        else
        {
            _progress.Style = ProgressBarStyle.Marquee;
            _progressText.Text = FormatMegabytes(progress.ReceivedBytes);
        }
        SetPhase(Phase.Downloading);
    }

    public void ShowVerifying() => SetPhase(Phase.Verifying);

    public void ShowApplying() => SetPhase(Phase.Applying);

    public void ShowCancelled() => SetPhase(Phase.Cancelled);

    public void ShowFailed(string reason)
    {
        _failureReason = reason;
        SetPhase(Phase.Failed);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_phase == Phase.Downloading) CancelDownloadRequested?.Invoke(this, EventArgs.Empty);
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _headingFont.Dispose();
            _statusIcon.Image?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void BuildLayout(string currentVersion, string installDirectory)
    {
        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(16), Dock = DockStyle.Fill };
        Controls.Add(root);

        _heading.Font = _headingFont;
        _heading.Text = string.Format(UiLabels.UpdateAvailableHeading, Manifest.Version);
        root.Controls.Add(_heading);

        var versions = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
        versions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        versions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddVersionRow(versions, UiLabels.UpdateCurrentVersion, currentVersion);
        AddVersionRow(versions, UiLabels.UpdateNewVersion, Manifest.Version.ToString());
        if (Manifest.ReleasedAt is { } releasedAt)
            AddVersionRow(versions, UiLabels.UpdateReleasedAt, $"{releasedAt.Year}年{releasedAt.Month}月{releasedAt.Day}日");
        root.Controls.Add(versions);

        if (_installWritable)
            root.Controls.Add(new Label { Text = UiLabels.UpdateRestartNote, AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Margin = new Padding(0, 0, 0, 4) });

        _statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _statusPanel.Controls.Add(_statusIcon, 0, 0);
        _statusPanel.Controls.Add(_statusText, 1, 0);
        _statusPanel.Controls.Add(_folderText, 1, 1);
        _statusPanel.Controls.Add(_progress, 1, 2);
        _statusPanel.Controls.Add(_progressText, 1, 3);
        _folderText.Text = string.Format(UiLabels.UpdateInstallFolder, installDirectory);
        root.Controls.Add(_statusPanel);

        var footer = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0), MinimumSize = new Size(ContentWidth, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _skipButton.Anchor = AnchorStyles.Left;
        footer.Controls.Add(_skipButton, 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        actions.Controls.Add(_primaryButton);
        actions.Controls.Add(_distributionPageButton);
        actions.Controls.Add(_secondaryButton);
        footer.Controls.Add(actions, 1, 0);
        root.Controls.Add(footer);

        _primaryButton.Click += (_, _) =>
        {
            if (_installWritable) UpdateRequested?.Invoke(this, EventArgs.Empty);
            else DistributionPageRequested?.Invoke(this, EventArgs.Empty);
        };
        _distributionPageButton.Click += (_, _) => DistributionPageRequested?.Invoke(this, EventArgs.Empty);
        _secondaryButton.Click += (_, _) =>
        {
            if (_phase == Phase.Downloading) CancelDownloadRequested?.Invoke(this, EventArgs.Empty);
            else Close();
        };
        _skipButton.Click += (_, _) => SkipRequested?.Invoke(this, EventArgs.Empty);
        AcceptButton = _primaryButton;
        CancelButton = _secondaryButton;
        ActiveControl = _primaryButton;
    }

    private static void AddVersionRow(TableLayoutPanel table, string label, string value)
    {
        var row = table.RowCount++;
        table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 2, 16, 2) }, 0, row);
        table.Controls.Add(new Label { Text = value, AutoSize = true, Margin = new Padding(0, 2, 0, 2) }, 1, row);
    }

    private void SetPhase(Phase phase)
    {
        _phase = phase;
        Render();
    }

    private void Render()
    {
        var working = IsWorking;
        var status = StatusMessage();
        _statusText.Text = status ?? string.Empty;
        _folderText.Visible = !_installWritable;
        _progress.Visible = _phase is Phase.Downloading or Phase.Verifying;
        if (_phase == Phase.Verifying) _progress.Style = ProgressBarStyle.Marquee;
        _progressText.Visible = _phase == Phase.Downloading;
        _statusPanel.Visible = status is not null;
        SetStatusIcon(_phase == Phase.Failed || !_installWritable);

        _primaryButton.Text = !_installWritable ? UiLabels.OpenDistributionPage
            : _phase == Phase.Failed ? UiLabels.UpdateRetry
            : UiLabels.UpdateNow;
        _primaryButton.Enabled = !_installWritable || (!working && _busyReason is null);
        _distributionPageButton.Visible = _installWritable && _phase == Phase.Failed;
        _secondaryButton.Text = _phase == Phase.Downloading ? UiLabels.UpdateCancelDownload : UiLabels.UpdateLater;
        _secondaryButton.Enabled = _phase is not (Phase.Verifying or Phase.Applying);
        _skipButton.Enabled = !working;
    }

    private string? StatusMessage()
    {
        if (!_installWritable) return UiLabels.UpdateInstallNotWritable;
        return _phase switch
        {
            Phase.Downloading => UiLabels.UpdateDownloading,
            Phase.Verifying => UiLabels.UpdateVerifying,
            Phase.Applying => UiLabels.UpdateApplying,
            Phase.Failed => string.Join(
                Environment.NewLine,
                new[] { string.Format(UiLabels.UpdateFailed, _failureReason), UiLabels.UpdateFailedNextAction, _busyReason }.OfType<string>()),
            Phase.Cancelled when _busyReason is null => UiLabels.UpdateDownloadCancelled,
            _ => _busyReason
        };
    }

    private void SetStatusIcon(bool visible)
    {
        if (visible && _statusIcon.Image is null) _statusIcon.Image = SystemIcons.Warning.ToBitmap();
        _statusIcon.Visible = visible;
    }

    private static string FormatMegabytes(long bytes) => $"{bytes / (1024.0 * 1024.0):0.0} MB";
}
