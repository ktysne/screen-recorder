using System.Diagnostics;
using System.Globalization;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

/// <summary>更新の確認(自動と手動)、更新ダイアログ、適用の開始をまとめる。UI スレッドからだけ呼ぶ。</summary>
internal sealed class UpdateController : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private readonly DailyLog _log;
    private readonly UpdateService _service;
    private readonly string _installDirectory;
    private readonly Func<Settings> _getSettings;
    private readonly Func<string, bool> _saveSkippedVersion;
    private readonly Func<string?> _getBusyReason;
    private readonly Action<string, ToolTipIcon, bool> _notify;
    private readonly Action _requestExitForUpdate;
    // GetTickCount64 はスリープ中も進むため、スリープをまたいでも 24 時間の間隔を保てる。
    private readonly long _startedAtTickCount = Environment.TickCount64;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)PollInterval.TotalMilliseconds };
    private readonly CancellationTokenSource _lifetime = new();
    private TimeSpan? _lastAutomaticCheck;
    private bool _checking;
    private UpdateDialog? _dialog;
    private UpdateManifest? _notifiedManifest;
    private CancellationTokenSource? _downloadCancellation;
    private PreparedUpdate? _prepared;
    private bool _disposed;

    /// <param name="notify">通知の文言、アイコン、クリックで更新ダイアログを開くか。</param>
    /// <param name="getBusyReason">録画中や撮影中なら、更新できない理由。</param>
    public UpdateController(
        DailyLog log,
        string installDirectory,
        Func<Settings> getSettings,
        Func<string, bool> saveSkippedVersion,
        Func<string?> getBusyReason,
        Action<string, ToolTipIcon, bool> notify,
        Action requestExitForUpdate)
    {
        _log = log;
        _service = new UpdateService(log);
        _installDirectory = installDirectory;
        _getSettings = getSettings;
        _saveSkippedVersion = saveSkippedVersion;
        _getBusyReason = getBusyReason;
        _notify = notify;
        _requestExitForUpdate = requestExitForUpdate;
        _timer.Tick += (_, _) => CheckAutomaticallyIfDue();
    }

    public void Start() => _timer.Start();

    public void CheckManually()
    {
        if (_dialog is { IsDisposed: false, IsWorking: true })
        {
            ActivateDialog();
            return;
        }
        _notify(UiLabels.UpdateChecking, ToolTipIcon.Info, false);
        if (_checking) return;
        _ = CheckAsync(UpdateCheckTrigger.Manual);
    }

    public void OpenNotifiedUpdate()
    {
        if (_notifiedManifest is { } manifest) ShowDialog(manifest);
    }

    public void RefreshBusyState()
    {
        if (_dialog is { IsDisposed: false } dialog) dialog.SetBusyReason(_getBusyReason());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        _lifetime.Cancel();
        _downloadCancellation?.Cancel();
        _dialog?.Close();
    }

    private void CheckAutomaticallyIfDue()
    {
        if (_disposed || _checking || _dialog is { IsDisposed: false }) return;
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - _startedAtTickCount);
        if (!UpdateCheckSchedule.IsAutomaticCheckDue(_getSettings().CheckForUpdatesAutomatically, elapsed, _lastAutomaticCheck)) return;
        _lastAutomaticCheck = elapsed;
        _ = CheckAsync(UpdateCheckTrigger.Automatic);
    }

    private async Task CheckAsync(UpdateCheckTrigger trigger)
    {
        _checking = true;
        try
        {
            var result = await _service.CheckAsync(_getSettings().SkippedUpdateVersion, trigger, _lifetime.Token);
            if (_disposed) return;
            HandleCheckResult(result, trigger);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception)
        {
            _log.Write($"Update check crashed: trigger={trigger}; {exception}");
            if (!_disposed && trigger == UpdateCheckTrigger.Manual)
                _notify(string.Format(UiLabels.UpdateCheckFailed, "予期しないエラーが発生しました。"), ToolTipIcon.Warning, false);
        }
        finally
        {
            _checking = false;
        }
    }

    private void HandleCheckResult(UpdateCheckResult result, UpdateCheckTrigger trigger)
    {
        if (!UpdateCheckEvaluator.ShouldNotify(result, trigger)) return;
        switch (result.Kind)
        {
            case UpdateCheckKind.Available or UpdateCheckKind.Skipped when result.Manifest is { } manifest:
                _notifiedManifest = manifest;
                if (trigger == UpdateCheckTrigger.Manual) ShowDialog(manifest);
                else _notify(string.Format(UiLabels.UpdateAvailableNotification, manifest.Version), ToolTipIcon.Info, true);
                break;
            case UpdateCheckKind.UpToDate:
                _notify(string.Format(UiLabels.UpdateUpToDate, AppVersion.Current), ToolTipIcon.Info, false);
                break;
            default:
                _notify(string.Format(UiLabels.UpdateCheckFailed, result.Error), ToolTipIcon.Warning, false);
                break;
        }
    }

    private void ShowDialog(UpdateManifest manifest)
    {
        if (_dialog is { IsDisposed: false } existing)
        {
            if (existing.IsWorking || existing.Manifest.Version == manifest.Version)
            {
                ActivateDialog();
                return;
            }
            existing.Close();
        }
        if (_prepared is { } prepared && prepared.Manifest.Version != manifest.Version) _prepared = null;

        var dialog = new UpdateDialog(manifest, AppVersion.Current, _installDirectory, CanWriteInstallDirectory());
        dialog.UpdateRequested += (_, _) => StartUpdate(dialog);
        dialog.SkipRequested += (_, _) => SkipVersion(dialog);
        dialog.CancelDownloadRequested += (_, _) => _downloadCancellation?.Cancel();
        dialog.DistributionPageRequested += (_, _) => OpenDistributionPage();
        dialog.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_dialog, dialog)) _dialog = null;
        };
        dialog.SetBusyReason(_getBusyReason());
        _dialog = dialog;
        dialog.Show();
        ActivateDialog();
    }

    private void ActivateDialog()
    {
        if (_dialog is not { IsDisposed: false } dialog) return;
        if (dialog.WindowState == FormWindowState.Minimized) dialog.WindowState = FormWindowState.Normal;
        dialog.BringToFront();
        dialog.Activate();
    }

    private async void StartUpdate(UpdateDialog dialog)
    {
        if (_getBusyReason() is { } busy)
        {
            dialog.SetBusyReason(busy);
            return;
        }
        if (_prepared is { } ready && ready.Manifest.Version == dialog.Manifest.Version)
        {
            ApplyPreparedUpdate(ready, dialog);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        dialog.ShowDownloading(new UpdateDownloadProgress(0, null));
        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            if (!dialog.IsDisposed && ReferenceEquals(_downloadCancellation, cancellation)) dialog.ShowDownloading(value);
        });
        try
        {
            var prepared = await _service.PrepareAsync(dialog.Manifest, progress, () =>
            {
                if (!dialog.IsDisposed) dialog.ShowVerifying();
            }, cancellation.Token);
            if (_disposed) return;
            _prepared = prepared;
            if (dialog.IsDisposed) return;
            if (_getBusyReason() is { } busyAfterDownload)
            {
                dialog.SetBusyReason(busyAfterDownload);
                dialog.ShowReady();
                return;
            }
            ApplyPreparedUpdate(prepared, dialog);
        }
        catch (OperationCanceledException)
        {
            if (!dialog.IsDisposed) dialog.ShowCancelled();
        }
        catch (UpdatePackageException exception)
        {
            if (!dialog.IsDisposed) dialog.ShowFailed(exception.Message);
        }
        catch (Exception exception)
        {
            _log.Write($"Update preparation crashed: {exception}");
            if (!dialog.IsDisposed) dialog.ShowFailed("予期しないエラーが発生しました。");
        }
        finally
        {
            if (ReferenceEquals(_downloadCancellation, cancellation)) _downloadCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ApplyPreparedUpdate(PreparedUpdate prepared, UpdateDialog dialog)
    {
        dialog.ShowApplying();
        try
        {
            var start = new ProcessStartInfo(prepared.ExecutablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = prepared.ExtractedDirectory
            };
            start.ArgumentList.Add(UpdateApplier.ApplyUpdateArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(prepared.ExtractedDirectory);
            start.ArgumentList.Add(_installDirectory);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("更新の処理を起動できませんでした。");
            _log.Write($"Update applier started: pid={process.Id}, version={prepared.Manifest.Version}, source={prepared.ExtractedDirectory}, install={_installDirectory}");
        }
        catch (Exception exception)
        {
            _log.Write($"Starting update applier failed: {exception}");
            _prepared = null;
            dialog.ShowFailed("更新の処理を起動できませんでした。");
            return;
        }
        _requestExitForUpdate();
    }

    private void SkipVersion(UpdateDialog dialog)
    {
        var version = dialog.Manifest.Version.ToString();
        if (!_saveSkippedVersion(version))
        {
            _notify(UiLabels.UpdateSkipSaveFailed, ToolTipIcon.Warning, false);
            return;
        }
        _log.Write($"Update version skipped: {version}");
        dialog.Close();
    }

    private void OpenDistributionPage()
    {
        try
        {
            using (Process.Start(new ProcessStartInfo(UpdateManifestParser.DistributionPageUrl) { UseShellExecute = true })) { }
        }
        catch (Exception exception)
        {
            _log.Write($"Opening distribution page failed: {exception}");
            _notify(string.Format(UiLabels.DistributionPageOpenFailed, exception.Message), ToolTipIcon.Error, false);
        }
    }

    /// <summary>Program Files のように書き込めない場所では、更新を始める前に手動の展開へ案内する。</summary>
    private bool CanWriteInstallDirectory()
    {
        var probe = Path.Combine(_installDirectory, $".screenrecorder-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Write($"Install directory is not writable: {_installDirectory}; {exception.Message}");
            return false;
        }
    }
}
