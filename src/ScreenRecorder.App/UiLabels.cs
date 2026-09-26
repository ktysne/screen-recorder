using ScreenRecorder.Core;

namespace ScreenRecorder.App;

public static class UiLabels
{
    public const string AppName = "ScreenRecorder";
    public const string Screenshot = "スクリーンショット";
    public const string Record = "録画";
    public const string FullDisplay = "ディスプレイ全体";
    public const string SelectRegion = "範囲を指定";
    public const string SelectWindow = "ウィンドウを指定";
    public const string PauseResume = "録画を一時停止/再開";
    public const string PauseRecording = "録画を一時停止";
    public const string ResumeRecording = "録画を再開";
    public const string StopRecording = "録画を停止";
    public const string RecordingPreparing = "録画の準備中";
    public const string OpenImageFolder = "静止画の保存先を開く";
    public const string OpenVideoFolder = "動画の保存先を開く";
    public const string Settings = "設定...";
    public const string SettingsTitle = "ScreenRecorder の設定";
    public const string GeneralTab = "全般";
    public const string GeneralPreferences = "サインインと通知";
    public const string FilenameOptions = "ファイル名";
    public const string ApplicationTools = "アプリの管理";
    public const string DiagnosticLog = "診断ログ";
    public const string DiagnosticLogLevel = "記録レベル";
    public const string DiagnosticLogLevelSilent = "記録しない";
    public const string DiagnosticLogLevelError = "エラーのみ";
    public const string DiagnosticLogLevelWarn = "警告まで";
    public const string DiagnosticLogLevelInfo = "情報まで (既定)";
    public const string DiagnosticLogLevelDebug = "詳細";
    public const string DiagnosticLogNote = "記録はこの PC の中のファイルに残るだけで、あなたの操作なしに外部へ送られることはありません。何がどこまで残るかは「診断ログポリシーを確認」で読めます。";
    public const string OpenDiagnosticLogsFolder = "診断ログのフォルダーを開く";
    public const string ReviewDiagnosticLogPolicy = "診断ログポリシーを確認";
    public const string DiagnosticLogPolicyTitle = "診断ログポリシー";
    public const string DiagnosticLogPolicyPromise = "あなたの操作なしに、ログが外へ出ることはありません。";
    public const string DiagnosticLogPolicyLocationHeading = "保存される場所";
    public const string DiagnosticLogPolicyLevelsHeading = "記録レベルごとに残るもの";
    public const string DiagnosticLogPolicyExcludedHeading = "記録しないもの";
    public const string DiagnosticLogPolicyIncludedHeading = "記録するもの";
    public const string DiagnosticLogPolicyPathHeading = "ファイル名とパスについて";
    public const string DiagnosticLogPolicyLocation = "診断ログは、この PC の中にテキストファイルとして残ります。\n場所: {0}\n起動 1 回につきファイルが 1 本でき、直近 10 起動分だけを残します。それより古いものは、起動のたびに自動で消えます。\n「診断ログのフォルダーを開く」を押すと、この場所がエクスプローラーで開きます。中身はメモ帳などで読めます。";
    public const string DiagnosticLogPolicyLevels = "記録しない: 何も残しません。ファイルも作りません。\nエラーのみ: そのままでは続けられなかった失敗だけを残します。例: 録画を保存できなかった、撮影に失敗した。\n警告まで: 上に加えて、うまくいかなかったが処理は続いた出来事を残します。例: MP3 へ変換できず AAC のまま保存した、マイクを使えず音声なしで録画した。\n情報まで (既定): 上に加えて、操作と、それによる状態の変化を残します。例: 録画を始めた・保存した、設定を保存した、更新を確認した。\n詳細: 上に加えて、開発者向けの細かな追跡を残します。量が増えるので、不具合を調べているときだけ選んでください。";
    public const string DiagnosticLogPolicyExcluded = "・撮影した画像、録画した映像と音声そのもの\n・クリップボードの中身\n・ウィンドウのタイトル\n・IP アドレスや利用統計";
    public const string DiagnosticLogPolicyIncluded = "・あなたの操作と、それによる状態の変化\n・エラーと、その理由の文言 (ライブラリや Windows が返すメッセージを含みます)\n・ファイル名、フォルダーのパス、撮影する範囲の座標と大きさ、フレームレート、ビットレート、音声の形式\n・アプリのバージョンと時刻";
    public const string DiagnosticLogPolicyPath = "フォルダーのパスには、Windows のユーザー名が含まれることがあります (C:\\Users\\<名前>\\... の形)。保存先が分からないと書き込みの失敗を切り分けられないため、ここは省いていません。\nファイル名のひな形に {window} を使っている場合は、保存したファイル名にウィンドウのタイトルが含まれます。\nログを開発者へ渡すかどうかは、中身を読んだうえでご自分で決めてください。アプリの側から送ることはありません。";
    public const string Close = "閉じる";
    public const string StillImageTab = "静止画";
    public const string StillImageOptions = "撮影と保存";
    public const string VideoTab = "動画";
    public const string VideoImageOptions = "映像";
    public const string AudioOptions = "音声";
    public const string ShortcutsTab = "ショートカット";
    public const string ShortcutInstructions = "入力方法";
    public const string ShortcutAssignments = "操作ごとの割り当て";
    public const string StartWithWindows = "サインイン時に起動";
    public const string CheckForUpdatesAutomatically = "更新を自動で確認";
    public const string NotifyWhenSaved = "保存完了を通知";
    public const string PlayCaptureSound = "撮影時に効果音を鳴らす";
    public const string FileNameTemplate = "ファイル名のひな形";
    public const string OrganizeByMonth = "月ごとのフォルダーに分ける";
    public const string RestoreDefaults = "すべての設定を既定に戻す";
    public const string OpenLicense = "ライセンスを開く";
    public const string StillImageDirectory = "保存先";
    public const string ImageFormat = "形式";
    public const string JpegQuality = "JPEG の品質";
    public const string PngCompression = "PNG の圧縮";
    public const string CopyImageToClipboard = "クリップボードへコピー";
    public const string CaptureImageCursor = "カーソルを写す";
    public const string CaptureDelay = "撮影までの遅延";
    public const string AfterCaptureAction = "撮影後の動作";
    public const string VideoDirectory = "保存先";
    public const string FrameRate = "フレームレート";
    public const string VideoBitrate = "ビットレート";
    public const string CaptureVideoCursor = "カーソルを写す";
    public const string Countdown = "録画開始前のカウントダウン";
    public const string OutputScale = "出力の大きさ";
    public const string HighlightClicks = "クリック位置を強調する";
    public const string Encoder = "エンコーダ";
    public const string CaptureSystemAudio = "PC の音を収録";
    public const string CaptureMicrophone = "マイクを収録";
    public const string MicrophoneDevice = "マイクのデバイス";
    public const string AudioFormat = "音声形式";
    public const string AacBitrate = "AAC のビットレート";
    public const string Mp3Bitrate = "MP3 のビットレート";
    public const string DefaultDevice = "既定のデバイス";
    public const string SavedMicrophoneDevice = "保存済みのデバイス";
    public const string DeviceNotFound = "見つかりません";
    public const string Mp3ConversionHelp = "MP3 は録画の後で変換するため、保存に少し時間がかかります。";
    public const string Jpeg = "JPEG";
    public const string Png = "PNG";
    public const string PngFast = "速い";
    public const string PngStandard = "標準";
    public const string PngSmallest = "最小サイズ";
    public const string CaptureAfterNone = "何もしない";
    public const string CaptureAfterOpenFile = "既定のアプリで開く";
    public const string CaptureAfterOpenFolder = "保存先を開く";
    public const string ScreenshotCountdownPrefix = "撮影まで";
    public const string RecordingCountdownPrefix = "録画開始まで";
    public const string ScreenshotCountdownAccessibleName = "撮影までの残り時間";
    public const string RecordingCountdownAccessibleName = "録画開始までの残り時間";
    public const string Aac = "AAC";
    public const string Mp3 = "MP3";
    public const string EncoderAutomatic = "自動";
    public const string EncoderSoftwareOnly = "ソフトウェアのみ";
    public const string None = "なし";
    public const string Browse = "参照...";
    public const string Ok = "OK";
    public const string Cancel = "キャンセル";
    public const string ShortcutUnassigned = "割り当てなし";
    public const string ShortcutDuplicate = "ほかの操作と重複しています。キーを変更してください。";
    public const string ShortcutInvalid = "キーの表記を読み取れません。別のキーを入力してください。";
    public const string ShortcutStatusUnavailable = "登録できません";
    public const string ShortcutStatusDuplicate = "重複しています";
    public const string ShortcutRegistrationFailed = "このキーを登録できませんでした。ほかのアプリが使用している可能性があります。";
    public const string PrintScreenSnippingHint = "Windows の「Print Screen キーを使用して画面キャプチャを開く」が有効なため、PrtSc を登録できません。";
    public const string OpenKeyboardSettings = "Windows のキーボード設定を開く";
    public const string StartupHotkeyFailureTitle = "ショートカットを登録できませんでした";
    public const string StartupHotkeyFailureBody = "一部のショートカットを登録できませんでした。設定画面で状態を確認してください。";
    public const string SettingsSaveFailed = "設定を保存できませんでした。入力内容を確認して、もう一度お試しください。";
    public const string RestoreDefaultsConfirmation = "すべての設定を既定値に戻します。変更は OK を押すまで保存されません。続けますか？";
    public const string RestoreDefaultsTitle = "設定を既定に戻す";
    public const string ScreenshotSavedNotification = "画像を保存しました。クリックすると保存先でファイルを選択します。";
    public const string ScreenshotCaptureFailed = "撮影できませんでした: {0}";
    public const string ScreenshotExitWaiting = "撮影と保存の処理が終わったら ScreenRecorder を終了します。";
    public const string ScreenshotClipboardFailed = "画像は保存しましたが、クリップボードへコピーできませんでした。";
    public const string ScreenshotAfterActionFailed = "画像は保存しましたが、撮影後の動作に失敗しました。クリックすると保存先でファイルを選択します。";
    public const string RecordingSavedNotification = "動画を保存しました。クリックすると保存先でファイルを選択します。";
    public const string RecordingStartFailed = "録画を開始できませんでした: {0}";
    public const string RecordingStartTimedOut = "録画ライブラリの準備が終わりませんでした";
    public const string RecordingStartTemporaryFileRetained = "録画を開始できませんでした。一時ファイルが残っています: {0}";
    public const string RecordingFinalizationTimedOut = "録画の停止後、保存完了を確認できませんでした";
    public const string RecordingOutputTooSmall = "出力の大きさが 2 ピクセル未満になるため録画を開始できません。";
    public const string RecordingRegionTooSmall = "録画する大きさが 2 ピクセル未満です。範囲を広げてください。";
    public const string RecordingSpaceInsufficient = "保存先の空き容量が 1 GB 未満のため、録画を開始できません。";
    public const string RecordingSpaceCheckFailed = "保存先の空き容量を確認できません: {0}";
    public const string RecordingFailed = "録画を完了できませんでした: {0}";
    public const string RecordingAfterActionFailed = "動画は保存しましたが、撮影後の動作に失敗しました。クリックすると保存先でファイルを選択します。";
    public const string RecordingTemporaryFileRetained = "録画を完了できませんでした。一時ファイルが残っています: {0}";
    public const string RecordingTemporaryFileIncomplete = "保存完了を確認できませんでした。未完了の一時ファイルが残っています: {0}";
    public const string RecordingTemporaryFileNotFound = "録画を完了できませんでした。一時ファイルは残っていません。";
    public const string RecordingExitWaiting = "録画を保存してから ScreenRecorder を終了します。";
    public const string IncompleteRecordingsFound = "未完了の録画ファイルが {0} 件残っています。場所: {1}";
    public const string FolderOpenFailed = "フォルダーを開けませんでした: {0}";
    public const string FolderPickerTitle = "保存先のフォルダーを選択";
    public const string SettingsApplyFailed = "設定は保存しましたが、自動起動の登録に失敗しました。";
    public const string SettingsApplyHotkeyFailed = "設定は保存しましたが、一部のショートカットを登録できませんでした。";
    public const string SettingsApplyPrintScreenFailed = "Windows の画面キャプチャ設定が有効なため、PrtSc を登録できませんでした。";
    public const string NoShortcutFailures = "登録に失敗したショートカットはありません。";
    public const string ShortcutRegistrationProblem = "登録できないショートカットがあります。";
    public const string ShortcutEntryHelp = "入力欄を選んでキーを押すと登録します。Backspace または Delete で割り当てを解除します。";
    public const string ShortcutRegistrationStatus = "登録状態";
    public const string ShortcutColumnAction = "操作";
    public const string ShortcutColumnEnabled = "有効";
    public const string ShortcutColumnKey = "キー";
    public const string ShortcutColumnStatus = "状態";
    public const string ShortcutStatusDisabled = "無効";
    public const string ShortcutDisabledToolTip = "設定のショートカットタブで無効にしています";
    public const string FilenameTemplateHelp = "{date} は日付、{time} は時刻、{mode} は撮影方法、{window} はウィンドウ名に置き換わります。";
    public const string CaptureDelayHelp = "撮影の操作をしてから撮影するまで待つ時間です。範囲やウィンドウを指定する場合は、選び終えてから数えます。";
    public const string DirectoryRequired = "保存先のフォルダーを指定してください。";
    public const string SettingsValuesInvalid = "設定値を確認してください。";
    public const string LicenseOpenFailed = "ライセンスを開けませんでした。";
    public const string ManualOpenFailed = "マニュアルを開けませんでした。";
    public const string KeyboardSettingsOpenFailed = "Windows のキーボード設定を開けませんでした。";
    public const string MegabitsPerSecond = "Mbps";
    public static string Seconds(int value) => value == 0 ? None : $"{value} 秒";
    public static string FramesPerSecond(int value) => $"{value} fps";
    public static string Percentage(int value) => $"{value}%";
    public static string NumericRange(int minimum, int maximum) => $"{minimum}–{maximum}";
    public static string KilobitsPerSecond(int value) => $"{value} kbps";
    public const string UpdateDialogTitle = "ScreenRecorder の更新";
    public const string UpdateAvailableHeading = "ScreenRecorder {0} を利用できます";
    public const string UpdateCurrentVersion = "現在の版";
    public const string UpdateNewVersion = "新しい版";
    public const string UpdateReleasedAt = "公開日";
    public const string UpdateRestartNote = "更新すると ScreenRecorder をいったん終了し、新しい版で起動し直します。";
    public const string UpdateNow = "今すぐ更新";
    public const string UpdateLater = "後で";
    public const string UpdateSkipVersion = "この版をスキップ";
    public const string UpdateRetry = "もう一度試す";
    public const string UpdateCancelDownload = "ダウンロードを中止";
    public const string OpenDistributionPage = "配布ページを開く";
    public const string UpdateBlockedByRecording = "録画中は更新できません。録画を停止して保存が終わると「今すぐ更新」を押せます。";
    public const string UpdateBlockedByCapture = "撮影中は更新できません。撮影と保存が終わると「今すぐ更新」を押せます。";
    public const string UpdateInstallNotWritable = "ScreenRecorder のフォルダーに書き込めないため、自動では更新できません。配布ページから zip を入手し、書き込めるフォルダーへ展開してください。";
    public const string UpdateInstallFolder = "フォルダー: {0}";
    public const string UpdateDownloading = "新しい版をダウンロードしています。";
    public const string UpdateVerifying = "ダウンロードしたファイルを確かめています。";
    public const string UpdateApplying = "ScreenRecorder を終了して更新します。新しい版が起動するまでお待ちください。";
    public const string UpdateDownloadCancelled = "ダウンロードを中止しました。更新するには「今すぐ更新」を押してください。";
    public const string UpdateFailed = "更新できませんでした。{0}";
    public const string UpdateFailedNextAction = "「もう一度試す」を押すか、配布ページから zip を入手して展開してください。詳しい内容はログに記録しました。";
    public const string UpdateChecking = "更新を確認しています。";
    public const string UpdateUpToDate = "ScreenRecorder は最新の版です({0})。";
    public const string UpdateCheckFailed = "更新を確認できませんでした。{0}";
    public const string UpdateAvailableNotification = "ScreenRecorder {0} を利用できます。クリックすると更新の画面を開きます。";
    public const string UpdateCompleted = "ScreenRecorder を {0} に更新しました。";
    public const string UpdateSkipSaveFailed = "スキップする版を保存できませんでした。次の確認でもう一度通知します。";
    public const string DistributionPageOpenFailed = "配布ページを開けませんでした: {0}";
    public const string UpdateApplyFailedRestored = "ScreenRecorder を {0} に更新できなかったため、元の版に戻して起動します。\n\n理由: {1}\n\nもう一度更新するか、配布ページ({2})から zip を入手して展開してください。詳しい内容はログに記録しました。";
    public const string UpdateApplyFailedBroken = "ScreenRecorder を {0} に更新できず、元の版に戻せないファイルがありました。\n\n理由: {1}\n\n配布ページ({2})から zip を入手し、次のフォルダーへ上書きで展開してください。\n{3}\n\n詳しい内容はログに記録しました。";
    public const string UpdateApplyOldProcessRunning = "ScreenRecorder が終了しなかったため、更新を中止しました。ファイルは変更していません。\n\nScreenRecorder を終了してから、もう一度更新してください。";
    public const string UpdateApplyAnotherInstance = "ScreenRecorder を {0} に更新しましたが、別の ScreenRecorder が起動しているため新しい版を起動できませんでした。\n\n起動中の ScreenRecorder を終了してから、起動し直してください。";
    public const string UpdateApplyInvalidArguments = "更新の指定が正しくないため、更新を中止しました。ファイルは変更していません。";
    public const string UpdateApplyAlreadyRunning = "別の更新の処理が実行中のため、この更新を中止しました。ファイルは変更していません。";
    public const string UpdateApplyNewVersionExited = "新しい版が起動の直後に終了しました。";
    public const string UpdateApplyFilesLocked = "ScreenRecorder のファイルがほかのプログラムに使われていて、30 秒待っても置き換えられませんでした。";
    public const string Manual = "マニュアル";
    public const string CheckForUpdates = "更新を確認";
    public const string Exit = "終了";

    public static string ShortcutActionName(RecorderAction action) => action switch
    {
        RecorderAction.ScreenshotRegion => $"{Screenshot}: {SelectRegion}",
        RecorderAction.ScreenshotFullScreen => $"{Screenshot}: {FullDisplay}",
        RecorderAction.ScreenshotWindow => $"{Screenshot}: {SelectWindow}",
        RecorderAction.RecordingRegion => $"{Record}: {SelectRegion}",
        RecorderAction.RecordingFullScreen => $"{Record}: {FullDisplay}",
        RecorderAction.RecordingWindow => $"{Record}: {SelectWindow}",
        RecorderAction.PauseResume => PauseResume,
        _ => StopRecording
    };

    public static string ShortcutEnabledAccessibleName(RecorderAction action) => $"{ShortcutActionName(action)}を有効にする";

    public static string RecordingStateName(VideoRecordingState state) => state switch
    {
        VideoRecordingState.Idle => string.Empty,
        VideoRecordingState.Countdown => "録画開始前",
        VideoRecordingState.Preparing => RecordingPreparing,
        VideoRecordingState.Recording => "録画中",
        VideoRecordingState.Paused => "一時停止中",
        VideoRecordingState.Saving => "保存中",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    public static string TrayRecordingStatus(VideoRecordingState state) => state == VideoRecordingState.Idle
        ? AppName
        : $"{AppName} ({RecordingStateName(state)})";

    public static string RecordingToolbarStatus(VideoRecordingState state, TimeSpan elapsed)
    {
        var duration = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        return state switch
        {
            VideoRecordingState.Recording or VideoRecordingState.Paused => $"{RecordingStateName(state)}  {duration}",
            VideoRecordingState.Preparing or VideoRecordingState.Saving or VideoRecordingState.Countdown => RecordingStateName(state),
            VideoRecordingState.Idle => $"{RecordingStateName(VideoRecordingState.Recording)}  {duration}",
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
    }
}
