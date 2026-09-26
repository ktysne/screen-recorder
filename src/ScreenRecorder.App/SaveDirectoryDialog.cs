using System.Diagnostics;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class SaveDirectoryDialog : Form
{
    private sealed class AnnouncedLabel : Label
    {
        protected override void OnTextChanged(EventArgs eventArgs)
        {
            base.OnTextChanged(eventArgs);
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
        }
    }

    private const string WindowsSecurityUri = "windowsdefender://ransomwareprotection/";
    private readonly SaveDirectoryKind _kind;
    private readonly Font _headingFont = new(Control.DefaultFont, FontStyle.Bold);
    private readonly TextBox _directoryInput = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly Label _failureDetails = new AnnouncedLabel { AutoSize = true, MaximumSize = new Size(620, 0), Visible = false };
    private readonly Label _status = new AnnouncedLabel { AutoSize = true, MaximumSize = new Size(620, 0) };
    private readonly Button _changeButton = new() { Text = UiLabels.SaveDirectoryChange, AutoSize = true };
    private readonly Button _primaryButton = new() { Text = UiLabels.SaveDirectoryConfirm, AutoSize = true, MinimumSize = new Size(170, 0) };
    private readonly Button _chooseAnotherButton = new() { Text = UiLabels.SaveDirectoryChooseAnother, AutoSize = true, Visible = false };
    private readonly Button _openSecurityButton = new() { Text = UiLabels.SaveDirectoryOpenSecurity, AutoSize = true, Visible = false };
    private readonly Button _cancelButton = new() { Text = UiLabels.Cancel, AutoSize = true, MinimumSize = new Size(96, 0) };
    private bool _failureState;
    private bool _closing;

    public SaveDirectoryDialog(SaveDirectoryKind kind, string directory, Exception? initialFailure = null)
    {
        _kind = kind;
        _directoryInput.AccessibleName = kind == SaveDirectoryKind.StillImage ? UiLabels.StillImageDirectory : UiLabels.VideoDirectory;
        _directoryInput.Text = directory;
        _failureState = initialFailure is not null;

        Text = UiLabels.AppName;
        Name = "SaveDirectoryDialog";
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 420);
        KeyPreview = true;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(18), AutoScroll = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var heading = new Label
        {
            Text = kind == SaveDirectoryKind.StillImage ? UiLabels.SaveDirectoryStillImageTitle : UiLabels.SaveDirectoryVideoTitle,
            AutoSize = true,
            Font = _headingFont,
            MaximumSize = new Size(620, 0),
            Margin = new Padding(3, 3, 3, 10)
        };
        root.Controls.Add(heading, 0, 0);
        root.Controls.Add(new Label { Text = UiLabels.SaveDirectoryHelp, AutoSize = true, Margin = new Padding(3, 0, 3, 12) }, 0, 1);

        var pathRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 10) };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.Controls.Add(_directoryInput, 0, 0);
        pathRow.Controls.Add(_changeButton, 1, 0);
        root.Controls.Add(pathRow, 0, 2);
        root.Controls.Add(_failureDetails, 0, 3);
        root.Controls.Add(_status, 0, 4);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = true, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_primaryButton);
        buttons.Controls.Add(_chooseAnotherButton);
        buttons.Controls.Add(_openSecurityButton);
        root.Controls.Add(buttons, 0, 5);

        _changeButton.Click += (_, _) => BrowseForDirectory(testImmediately: false);
        _primaryButton.Click += async (_, _) =>
        {
            if (_failureState) await TryFallbackDirectoriesAsync();
            else await VerifySelectedDirectoryAsync();
        };
        _chooseAnotherButton.Click += (_, _) => BrowseForDirectory(testImmediately: true);
        _openSecurityButton.Click += (_, _) => OpenWindowsSecurity();
        _cancelButton.Click += (_, _) => CloseAsCancelled();
        AcceptButton = _primaryButton;
        CancelButton = _cancelButton;
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Escape) return;
            eventArgs.Handled = true;
            CloseAsCancelled();
        };

        if (initialFailure is not null) ShowFailure(initialFailure.Message);
        UpdateState();
        Shown += (_, _) =>
        {
            TopMost = true;
            BringToFront();
            Activate();
            _primaryButton.Focus();
        };
    }

    public string? SelectedDirectory { get; private set; }

    public void CancelFromExit()
    {
        if (_closing || IsDisposed || Disposing) return;
        CloseAsCancelled();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _headingFont.Dispose();
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        base.OnFormClosing(eventArgs);
        if (!eventArgs.Cancel) _closing = true;
    }

    private void BrowseForDirectory(bool testImmediately)
    {
        using var picker = new FolderBrowserDialog
        {
            Description = UiLabels.FolderPickerTitle,
            UseDescriptionForTitle = true,
            SelectedPath = _directoryInput.Text
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _directoryInput.Text = picker.SelectedPath;
        if (testImmediately) _ = VerifySelectedDirectoryAsync();
    }

    private async Task VerifySelectedDirectoryAsync()
    {
        var directory = _directoryInput.Text;
        SetBusy(true, UiLabels.SaveDirectoryChecking);
        try
        {
            await Task.Run(() => SaveDirectoryProbe.Check(directory));
            if (_closing || IsDisposed || Disposing) return;
            CloseWithSelection(directory);
        }
        catch (Exception exception)
        {
            if (_closing || IsDisposed || Disposing) return;
            ShowFailure(exception.Message);
        }
        finally
        {
            if (!_closing && !IsDisposed && !Disposing && DialogResult == DialogResult.None)
            {
                SetBusy(false);
                _primaryButton.Focus();
            }
        }
    }

    private async Task TryFallbackDirectoriesAsync()
    {
        var candidates = SaveDirectoryRules.GetFallbackDirectories(
            _kind,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var failures = new List<string>();
        SetBusy(true, UiLabels.SaveDirectoryChecking);
        try
        {
            foreach (var candidate in candidates)
            {
                if (_closing || IsDisposed || Disposing) return;
                _status.Text = string.Format(UiLabels.SaveDirectoryCheckingCandidate, candidate);
                try
                {
                    await Task.Run(() => SaveDirectoryProbe.Check(candidate));
                    if (_closing || IsDisposed || Disposing) return;
                    CloseWithSelection(candidate);
                    return;
                }
                catch (Exception exception)
                {
                    if (_closing || IsDisposed || Disposing) return;
                    failures.Add(string.Format(UiLabels.SaveDirectoryFailureReason, CaptureText.ErrorDetail(exception.Message)));
                }
            }

            if (_closing || IsDisposed || Disposing) return;
            var details = failures.Count == 0 ? UiLabels.SaveDirectoryNoFallbackCandidates : string.Join(Environment.NewLine, failures);
            ShowFailure($"{UiLabels.SaveDirectoryFallbackFailed}{Environment.NewLine}{details}");
        }
        finally
        {
            if (!_closing && !IsDisposed && !Disposing && DialogResult == DialogResult.None)
            {
                SetBusy(false);
                _primaryButton.Focus();
            }
        }
    }

    private void OpenWindowsSecurity()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(WindowsSecurityUri) { UseShellExecute = true })
                ?? throw new InvalidOperationException(UiLabels.SaveDirectorySecurityOpenFailedReason);
            _status.Text = UiLabels.SaveDirectorySecurityOpened;
        }
        catch (Exception exception)
        {
            _status.Text = string.Format(UiLabels.SaveDirectorySecurityOpenFailed, CaptureText.ErrorDetail(exception.Message));
        }
    }

    private void ShowFailure(string reason)
    {
        _failureState = true;
        _failureDetails.Text = $"{string.Format(UiLabels.SaveDirectoryFailureReason, CaptureText.ErrorDetail(reason))}{Environment.NewLine}{Environment.NewLine}{UiLabels.SaveDirectoryProtectionHelp}";
        _failureDetails.Visible = true;
        _status.Text = string.Empty;
        UpdateState();
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _status.Text = message ?? string.Empty;
        _changeButton.Enabled = !busy;
        _primaryButton.Enabled = !busy;
        _chooseAnotherButton.Enabled = !busy;
        _openSecurityButton.Enabled = !busy;
        _cancelButton.Enabled = !busy;
    }

    private void UpdateState()
    {
        _primaryButton.Text = _failureState ? UiLabels.SaveDirectoryChooseUnprotected : UiLabels.SaveDirectoryConfirm;
        _primaryButton.Visible = true;
        _chooseAnotherButton.Visible = _failureState;
        _openSecurityButton.Visible = _failureState;
        _changeButton.Visible = !_failureState;
        AcceptButton = _primaryButton;
    }

    private void CloseWithSelection(string directory)
    {
        SelectedDirectory = directory;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CloseAsCancelled()
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }
}
