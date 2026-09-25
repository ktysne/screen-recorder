using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenRecorderLib;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class SettingsForm : Form
{
    private sealed record ChoiceOption<T>(string Label, T Value);

    private readonly Settings _initialSettings;
    private readonly IReadOnlyDictionary<RecorderAction, HotkeyFailure> _hotkeyFailures;
    private readonly DailyLog _log;
    private readonly Func<Settings, bool> _saveSettings;
    private readonly Func<bool> _isRecording;
    private (string Label, string? Value)[] _microphoneChoices = [];
    private bool _microphoneEnumerationFailed;
    private readonly List<Action<Settings>> _loaders = [];
    private readonly List<Action<Settings>> _readers = [];
    private readonly Dictionary<RecorderAction, TextBox> _shortcutInputs = [];
    private readonly Dictionary<RecorderAction, Label> _shortcutStatuses = [];
    private readonly Dictionary<string, Label> _directoryIssues = [];
    private readonly Label _formStatus = new() { AutoSize = true };
    private readonly Label _shortcutStatus = new() { AutoSize = true, MaximumSize = new Size(760, 0) };
    private readonly Label _shortcutFailureDetails = new() { AutoSize = true, ForeColor = SystemColors.ControlText, MaximumSize = new Size(760, 0), Visible = false };
    private readonly Label _printScreenHint = new() { AutoSize = true, MaximumSize = new Size(760, 0) };
    private readonly TableLayoutPanel _printScreenPanel = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1, Visible = false };
    private readonly Button _openKeyboardSettings = new() { Text = UiLabels.OpenKeyboardSettings };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Button _saveButton = new() { Text = UiLabels.Ok, AutoSize = true, MinimumSize = new Size(96, 0) };
    private readonly List<Control> _jpegControls = [];
    private readonly List<Control> _pngControls = [];
    private readonly List<Control> _aacControls = [];
    private readonly List<Control> _mp3Controls = [];
    private readonly List<Control> _microphoneControls = [];
    private readonly List<Control> _audioControls = [];
    private ComboBox _imageFormat = null!;
    private ComboBox _audioFormat = null!;
    private Label _mp3Hint = null!;
    private CheckBox _microphoneEnabled = null!;
    private TabPage _videoTab = null!;
    private bool _loading;

    public SettingsForm(
        Settings settings,
        IReadOnlyList<HotkeyFailure> hotkeyFailures,
        DailyLog log,
        Func<Settings, bool> saveSettings,
        Func<bool> isRecording)
    {
        _initialSettings = settings.Clone();
        _hotkeyFailures = hotkeyFailures.ToDictionary(failure => failure.Action);
        _log = log;
        _saveSettings = saveSettings;
        _isRecording = isRecording;
        _microphoneChoices = LoadMicrophoneChoices(_initialSettings.MicrophoneDeviceId, _log, out _microphoneEnumerationFailed);

        Text = UiLabels.SettingsTitle;
        Name = "SettingsForm";
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(760, 600);
        ClientSize = new Size(960, 760);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        BuildLayout();
        LoadSettings(_initialSettings);
        Activated += (_, _) => UpdateEnablement();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        _tabs.TabPages.Add(BuildGeneralTab());
        _tabs.TabPages.Add(BuildStillImageTab());
        _videoTab = BuildVideoTab();
        _tabs.TabPages.Add(_videoTab);
        _tabs.TabPages.Add(BuildShortcutsTab());
        root.Controls.Add(_tabs, 0, 0);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _formStatus.Margin = new Padding(4, 9, 8, 4);
        footer.Controls.Add(_formStatus, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Dock = DockStyle.Fill };
        var cancelButton = new Button { Text = UiLabels.Cancel, AutoSize = true, MinimumSize = new Size(96, 0), DialogResult = DialogResult.Cancel };
        _saveButton.Click += (_, _) => Save();
        buttons.Controls.Add(_saveButton);
        buttons.Controls.Add(cancelButton);
        footer.Controls.Add(buttons, 1, 0);
        root.Controls.Add(footer, 0, 1);
        AcceptButton = _saveButton;
        CancelButton = cancelButton;
    }

    public void RefreshRecordingState() => UpdateEnablement();

    private TabPage BuildGeneralTab()
    {
        var page = CreatePage(UiLabels.GeneralTab, out var root);
        var preferences = AddSection(root, UiLabels.GeneralPreferences);
        BindCheck(preferences, UiLabels.StartWithWindows, settings => settings.StartWithWindows, (settings, value) => settings.StartWithWindows = value);
        BindCheck(preferences, UiLabels.CheckForUpdatesAutomatically, settings => settings.CheckForUpdatesAutomatically, (settings, value) => settings.CheckForUpdatesAutomatically = value);
        BindCheck(preferences, UiLabels.NotifyWhenSaved, settings => settings.NotifyWhenSaved, (settings, value) => settings.NotifyWhenSaved = value);
        BindCheck(preferences, UiLabels.PlayCaptureSound, settings => settings.PlayCaptureSound, (settings, value) => settings.PlayCaptureSound = value);

        var naming = AddSection(root, UiLabels.FilenameOptions);
        BindText(naming, UiLabels.FileNameTemplate, settings => settings.FileNameTemplate, (settings, value) => settings.FileNameTemplate = value, UiLabels.FilenameTemplateHelp);
        BindCheck(naming, UiLabels.OrganizeByMonth, settings => settings.OrganizeByMonth, (settings, value) => settings.OrganizeByMonth = value);

        var tools = AddSection(root, UiLabels.ApplicationTools);
        AddButtonRow(tools, UiLabels.OpenLogsFolder, OpenLogsFolder);
        AddButtonRow(tools, UiLabels.RestoreDefaults, RestoreDefaults);
        AddButtonRow(tools, UiLabels.OpenLicense, OpenLicense);
        return page;
    }

    private TabPage BuildStillImageTab()
    {
        var page = CreatePage(UiLabels.StillImageTab, out var root);
        var capture = AddSection(root, UiLabels.StillImageOptions);
        BindDirectory(capture, UiLabels.StillImageDirectory, nameof(Settings.StillImageDirectory), settings => settings.StillImageDirectory, (settings, value) => settings.StillImageDirectory = value);
        _imageFormat = BindChoice(capture, UiLabels.ImageFormat,
            [(UiLabels.Jpeg, StillImageFormat.Jpeg), (UiLabels.Png, StillImageFormat.Png)],
            settings => settings.ImageFormat, (settings, value) => settings.ImageFormat = value);
        var jpegQuality = BindNumber(capture, UiLabels.JpegQuality, 1, 100, settings => settings.JpegQuality, (settings, value) => settings.JpegQuality = value, UiLabels.JpegQualityRange);
        _jpegControls.Add(jpegQuality);
        var pngCompression = BindChoice(capture, UiLabels.PngCompression,
            [(UiLabels.PngFast, PngCompression.Fast), (UiLabels.PngStandard, PngCompression.Standard), (UiLabels.PngSmallest, PngCompression.Smallest)],
            settings => settings.PngCompression, (settings, value) => settings.PngCompression = value);
        _pngControls.Add(pngCompression);
        BindCheck(capture, UiLabels.CopyImageToClipboard, settings => settings.CopyImageToClipboard, (settings, value) => settings.CopyImageToClipboard = value);
        BindCheck(capture, UiLabels.CaptureImageCursor, settings => settings.CaptureImageCursor, (settings, value) => settings.CaptureImageCursor = value);
        BindChoice(capture, UiLabels.CaptureDelay,
            [(UiLabels.None, 0), (UiLabels.Seconds3, 3), (UiLabels.Seconds5, 5), (UiLabels.Seconds10, 10)],
            settings => settings.CaptureDelaySeconds, (settings, value) => settings.CaptureDelaySeconds = value, UiLabels.CaptureDelayHelp);
        BindChoice(capture, UiLabels.AfterCaptureAction,
            [(UiLabels.CaptureAfterNone, CaptureAfterAction.None), (UiLabels.CaptureAfterOpenFile, CaptureAfterAction.OpenFile), (UiLabels.CaptureAfterOpenFolder, CaptureAfterAction.OpenFolder)],
            settings => settings.AfterCaptureAction, (settings, value) => settings.AfterCaptureAction = value);
        _imageFormat.SelectedValueChanged += (_, _) => UpdateEnablement();
        return page;
    }

    private TabPage BuildVideoTab()
    {
        var page = CreatePage(UiLabels.VideoTab, out var root);
        var video = AddSection(root, UiLabels.VideoImageOptions);
        BindDirectory(video, UiLabels.VideoDirectory, nameof(Settings.VideoDirectory), settings => settings.VideoDirectory, (settings, value) => settings.VideoDirectory = value);
        BindChoice(video, UiLabels.FrameRate,
            [(UiLabels.FifteenFramesPerSecond, 15), (UiLabels.TwentyFourFramesPerSecond, 24), (UiLabels.ThirtyFramesPerSecond, 30), (UiLabels.SixtyFramesPerSecond, 60)],
            settings => settings.FrameRate, (settings, value) => settings.FrameRate = value);
        BindNumber(video, UiLabels.VideoBitrate, 1, 100, settings => settings.VideoBitrateMbps, (settings, value) => settings.VideoBitrateMbps = value, UiLabels.MegabitsPerSecond);
        BindCheck(video, UiLabels.CaptureVideoCursor, settings => settings.CaptureVideoCursor, (settings, value) => settings.CaptureVideoCursor = value);
        BindChoice(video, UiLabels.Countdown,
            [(UiLabels.None, 0), (UiLabels.Seconds3, 3), (UiLabels.Seconds5, 5)],
            settings => settings.CountdownSeconds, (settings, value) => settings.CountdownSeconds = value);
        BindChoice(video, UiLabels.OutputScale,
            [("100%", 100), ("75%", 75), ("50%", 50)],
            settings => settings.OutputScalePercent, (settings, value) => settings.OutputScalePercent = value);
        BindCheck(video, UiLabels.HighlightClicks, settings => settings.HighlightClicks, (settings, value) => settings.HighlightClicks = value);
        BindChoice(video, UiLabels.Encoder,
            [(UiLabels.EncoderAutomatic, EncoderMode.Automatic), (UiLabels.EncoderSoftwareOnly, EncoderMode.SoftwareOnly)],
            settings => settings.Encoder, (settings, value) => settings.Encoder = value);

        var audio = AddSection(root, UiLabels.AudioOptions);
        _audioControls.Add(BindCheck(audio, UiLabels.CaptureSystemAudio, settings => settings.CaptureSystemAudio, (settings, value) => settings.CaptureSystemAudio = value));
        _microphoneEnabled = BindCheck(audio, UiLabels.CaptureMicrophone, settings => settings.CaptureMicrophone, (settings, value) => settings.CaptureMicrophone = value);
        _audioControls.Add(_microphoneEnabled);
        var microphone = BindChoice(audio, UiLabels.MicrophoneDevice,
            _microphoneChoices,
            settings => settings.MicrophoneDeviceId, (settings, value) => settings.MicrophoneDeviceId = value);
        if (_microphoneEnumerationFailed && _initialSettings.MicrophoneDeviceId is not null)
            _readers[^1] = settings => settings.MicrophoneDeviceId = _initialSettings.MicrophoneDeviceId;
        _microphoneControls.Add(microphone);
        _audioControls.Add(microphone);
        _audioFormat = BindChoice(audio, UiLabels.AudioFormat,
            [(UiLabels.Aac, AudioFormat.Aac), (UiLabels.Mp3, AudioFormat.Mp3)],
            settings => settings.AudioFormat, (settings, value) => settings.AudioFormat = value);
        _mp3Hint = new Label
        {
            Text = UiLabels.Mp3ConversionHelp,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(720, 0),
            Visible = false
        };
        AddFullWidth(audio, _mp3Hint);
        _audioControls.Add(_audioFormat);
        var aacBitrate = BindChoice(audio, UiLabels.AacBitrate,
            [(UiLabels.KilobitsPerSecond(96), 96), (UiLabels.KilobitsPerSecond(128), 128), (UiLabels.KilobitsPerSecond(160), 160), (UiLabels.KilobitsPerSecond(192), 192)],
            settings => settings.AacBitrateKbps, (settings, value) => settings.AacBitrateKbps = value);
        _aacControls.Add(aacBitrate);
        _audioControls.Add(aacBitrate);
        var mp3Bitrate = BindChoice(audio, UiLabels.Mp3Bitrate,
            [(UiLabels.KilobitsPerSecond(128), 128), (UiLabels.KilobitsPerSecond(192), 192), (UiLabels.KilobitsPerSecond(256), 256), (UiLabels.KilobitsPerSecond(320), 320)],
            settings => settings.Mp3BitrateKbps, (settings, value) => settings.Mp3BitrateKbps = value);
        _mp3Controls.Add(mp3Bitrate);
        _audioControls.Add(mp3Bitrate);
        _audioFormat.SelectedValueChanged += (_, _) => UpdateEnablement();
        _microphoneEnabled.CheckedChanged += (_, _) => UpdateEnablement();
        return page;
    }

    private TabPage BuildShortcutsTab()
    {
        var page = CreatePage(UiLabels.ShortcutsTab, out var root);
        var help = AddSection(root, UiLabels.ShortcutInstructions);
        AddFullWidth(help, new Label
        {
            Text = UiLabels.ShortcutEntryHelp,
            AutoSize = true,
            MaximumSize = new Size(760, 0)
        });

        var grid = AddSection(root, UiLabels.ShortcutAssignments);
        grid.ColumnStyles[0].SizeType = SizeType.Percent;
        grid.ColumnStyles[0].Width = 32;
        grid.ColumnStyles[1].SizeType = SizeType.Percent;
        grid.ColumnStyles[1].Width = 44;
        grid.ColumnStyles[2].SizeType = SizeType.Percent;
        grid.ColumnStyles[2].Width = 24;
        AddHeadingRow(grid, UiLabels.ShortcutColumnAction, UiLabels.ShortcutColumnKey, UiLabels.ShortcutColumnStatus);
        foreach (var assignment in ShortcutSettingsValidator.GetAssignments(_initialSettings))
        {
            var action = assignment.Action;
            var input = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, AccessibleName = UiLabels.ShortcutActionName(action), TabStop = true };
            var status = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Text = string.Empty, Margin = new Padding(4, 6, 4, 4) };
            input.KeyDown += (_, eventArgs) => CaptureShortcut(action, input, eventArgs);
            input.TextChanged += (_, _) => { if (!_loading) RefreshValidation(); };
            AddShortcutRow(grid, UiLabels.ShortcutActionName(action), input, status);
            _shortcutInputs.Add(action, input);
            _shortcutStatuses.Add(action, status);
        }

        var feedback = AddSection(root, UiLabels.ShortcutRegistrationStatus);
        AddFullWidth(feedback, _shortcutStatus);
        AddFullWidth(feedback, _shortcutFailureDetails);
        _printScreenPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _printScreenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _printScreenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _printScreenPanel.Controls.Add(_printScreenHint, 0, 0);
        _openKeyboardSettings.AutoSize = true;
        _openKeyboardSettings.Click += (_, _) => OpenKeyboardSettings();
        _printScreenPanel.Controls.Add(_openKeyboardSettings, 1, 0);
        AddFullWidth(feedback, _printScreenPanel);
        return page;
    }

    private TabPage CreatePage(string title, out TableLayoutPanel root)
    {
        var page = new TabPage(title) { AutoScroll = true, Padding = new Padding(10) };
        // 幅はページに合わせ、高さだけを中身に合わせる。両方を中身に合わせると、中の自動サイズのグループと幅を決め合えずに縮み切る。
        var pageRoot = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(2) };
        pageRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root = pageRoot;
        page.Controls.Add(pageRoot);
        return page;
    }

    private static TableLayoutPanel AddSection(TableLayoutPanel root, string title)
    {
        var body = CreateInputTable();
        var section = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10) };
        section.Controls.Add(body);
        var row = root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(section, 0, row);
        return body;
    }

    private static TableLayoutPanel CreateInputTable()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new Padding(4) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        return table;
    }

    private CheckBox BindCheck(TableLayoutPanel table, string label, Func<Settings, bool> getter, Action<Settings, bool> setter)
    {
        var input = new CheckBox { Text = label, AutoSize = true, Margin = new Padding(4, 5, 4, 5) };
        AddFullWidth(table, input);
        _loaders.Add(settings => input.Checked = getter(settings));
        _readers.Add(settings => setter(settings, input.Checked));
        return input;
    }

    private TextBox BindText(TableLayoutPanel table, string label, Func<Settings, string> getter, Action<Settings, string> setter, string? help = null)
    {
        var input = new TextBox { Dock = DockStyle.Fill };
        AddFieldRow(table, label, input, help: help);
        input.TextChanged += (_, _) => { if (!_loading) RefreshValidation(); };
        _loaders.Add(settings => input.Text = getter(settings));
        _readers.Add(settings => setter(settings, input.Text));
        return input;
    }

    private void BindDirectory(TableLayoutPanel table, string label, string settingName, Func<Settings, string> getter, Action<Settings, string> setter)
    {
        var input = new TextBox { Dock = DockStyle.Fill, AccessibleName = label };
        var browse = new Button { Text = UiLabels.Browse, AutoSize = true, Anchor = AnchorStyles.Left };
        browse.Click += (_, _) => BrowseForDirectory(input);
        AddFieldRow(table, label, input, browse);
        var error = new Label { Text = UiLabels.DirectoryRequired, AutoSize = true, ForeColor = SystemColors.ControlText, Visible = false, Margin = new Padding(4, 0, 4, 5) };
        var errorRow = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(error, 1, errorRow);
        table.SetColumnSpan(error, 2);
        _directoryIssues.Add(settingName, error);
        input.TextChanged += (_, _) => { if (!_loading) RefreshValidation(); };
        _loaders.Add(settings => input.Text = getter(settings));
        _readers.Add(settings => setter(settings, input.Text));
    }

    private NumericUpDown BindNumber(TableLayoutPanel table, string label, int minimum, int maximum, Func<Settings, int> getter, Action<Settings, int> setter, string unit)
    {
        var input = new NumericUpDown { Minimum = minimum, Maximum = maximum, Width = 150, Anchor = AnchorStyles.Left, ThousandsSeparator = false };
        var content = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, FlowDirection = FlowDirection.LeftToRight };
        content.Controls.Add(input);
        content.Controls.Add(new Label { Text = unit, AutoSize = true, Margin = new Padding(3, 7, 0, 0) });
        AddFieldRow(table, label, content);
        _loaders.Add(settings => input.Value = Math.Clamp(getter(settings), minimum, maximum));
        _readers.Add(settings => setter(settings, (int)input.Value));
        return input;
    }

    private ComboBox BindChoice<T>(
        TableLayoutPanel table,
        string label,
        IReadOnlyList<(string Label, T Value)> options,
        Func<Settings, T> getter,
        Action<Settings, T> setter,
        string? help = null)
    {
        var input = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, IntegralHeight = false };
        foreach (var option in options) input.Items.Add(new ChoiceOption<T>(option.Label, option.Value));
        input.DisplayMember = nameof(ChoiceOption<T>.Label);
        AddFieldRow(table, label, input, help: help);
        _loaders.Add(settings =>
        {
            var value = getter(settings);
            input.SelectedIndex = Enumerable.Range(0, input.Items.Count)
                .FirstOrDefault(index => EqualityComparer<T>.Default.Equals(((ChoiceOption<T>)input.Items[index]!).Value, value), -1);
            if (input.SelectedIndex < 0 && input.Items.Count > 0) input.SelectedIndex = 0;
        });
        _readers.Add(settings =>
        {
            if (input.SelectedItem is ChoiceOption<T> option) setter(settings, option.Value);
        });
        return input;
    }

    private void AddButtonRow(TableLayoutPanel table, string label, Action action)
    {
        var button = new Button { Text = label, AutoSize = true, Anchor = AnchorStyles.Left };
        button.Click += (_, _) => action();
        AddFullWidth(table, button);
    }

    private static void AddFullWidth(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 3);
        control.Margin = new Padding(4, 4, 4, 4);
    }

    private static void AddFieldRow(TableLayoutPanel table, string label, Control editor, Control? trailing = null, string? help = null)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 7, 8, 4) }, 0, row);
        table.Controls.Add(editor, 1, row);
        editor.Margin = new Padding(4, 3, 4, 3);
        if (trailing is null)
        {
            table.SetColumnSpan(editor, 2);
        }
        else
        {
            table.Controls.Add(trailing, 2, row);
            trailing.Margin = new Padding(4, 2, 4, 2);
        }
        if (help is not null)
        {
            var helpRow = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var hint = new Label { Text = help, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(720, 0), Margin = new Padding(4, 0, 4, 7) };
            table.Controls.Add(hint, 1, helpRow);
            table.SetColumnSpan(hint, 2);
        }
    }

    private static void AddShortcutRow(TableLayoutPanel table, string label, Control input, Control status)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 7, 6, 4) }, 0, row);
        table.Controls.Add(input, 1, row);
        table.Controls.Add(status, 2, row);
    }

    private static void AddHeadingRow(TableLayoutPanel table, string action, string key, string status)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (var (text, column) in new[] { (action, 0), (key, 1), (status, 2) })
            table.Controls.Add(new Label { Text = text, AutoSize = true, Font = SystemFonts.MessageBoxFont, Margin = new Padding(4, 4, 4, 6) }, column, row);
    }

    private void LoadSettings(Settings settings)
    {
        _loading = true;
        foreach (var loader in _loaders) loader(settings);
        foreach (var (action, input) in _shortcutInputs)
        {
            var notation = ShortcutSettingsValidator.GetAssignments(settings).First(assignment => assignment.Action == action).Notation;
            input.Text = HotkeyShortcut.TryParse(notation, out var shortcut) && shortcut is not null ? shortcut.ToDisplayString() : notation;
        }
        _loading = false;
        RefreshValidation();
        UpdateEnablement();
    }

    private Settings ReadSettings()
    {
        var result = _initialSettings.Clone();
        foreach (var reader in _readers) reader(result);
        SetShortcut(result, RecorderAction.ScreenshotRegion, _shortcutInputs[RecorderAction.ScreenshotRegion].Text);
        SetShortcut(result, RecorderAction.ScreenshotFullScreen, _shortcutInputs[RecorderAction.ScreenshotFullScreen].Text);
        SetShortcut(result, RecorderAction.ScreenshotWindow, _shortcutInputs[RecorderAction.ScreenshotWindow].Text);
        SetShortcut(result, RecorderAction.RecordingRegion, _shortcutInputs[RecorderAction.RecordingRegion].Text);
        SetShortcut(result, RecorderAction.RecordingFullScreen, _shortcutInputs[RecorderAction.RecordingFullScreen].Text);
        SetShortcut(result, RecorderAction.RecordingWindow, _shortcutInputs[RecorderAction.RecordingWindow].Text);
        SetShortcut(result, RecorderAction.PauseResume, _shortcutInputs[RecorderAction.PauseResume].Text);
        return result;
    }

    private void RefreshValidation()
    {
        if (_shortcutInputs.Count == 0) return;
        var draft = ReadSettings();
        var shortcutIssues = ShortcutSettingsValidator.Validate(draft)
            .GroupBy(issue => issue.Action)
            .ToDictionary(group => group.Key, group => group.First());
        var allIssues = SettingsValidator.Validate(draft);
        var invalidProperties = allIssues
            .Where(issue => issue.Kind == SettingsIssueKind.InvalidValue)
            .Select(issue => issue.SettingName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (name, status) in _directoryIssues) status.Visible = invalidProperties.Contains(name);
        var invalid = false;
        var hasShortcutProblem = false;
        var shortcutDetails = new List<string>();
        foreach (var (action, input) in _shortcutInputs)
        {
            var status = _shortcutStatuses[action];
            if (shortcutIssues.TryGetValue(action, out var issue))
            {
                var duplicate = issue.Kind == ShortcutValidationIssueKind.Duplicate;
                status.Text = duplicate ? UiLabels.ShortcutStatusDuplicate : UiLabels.ShortcutStatusUnavailable;
                status.ForeColor = SystemColors.ControlText;
                shortcutDetails.Add($"{UiLabels.ShortcutActionName(action)}: {(duplicate ? UiLabels.ShortcutDuplicate : UiLabels.ShortcutInvalid)}");
                hasShortcutProblem = true;
                invalid = true;
                continue;
            }
            var activeFailure = GetCurrentFailure(action, input.Text);
            status.Text = activeFailure is null ? string.Empty : UiLabels.ShortcutStatusUnavailable;
            status.ForeColor = activeFailure is null ? SystemColors.GrayText : SystemColors.ControlText;
            if (activeFailure is not null)
            {
                hasShortcutProblem = true;
                if (!activeFailure.PrintScreenSettingsEnabled)
                    shortcutDetails.Add($"{UiLabels.ShortcutActionName(action)}: {FailureLabel(activeFailure)}");
            }
        }
        _shortcutStatus.Text = hasShortcutProblem ? UiLabels.ShortcutRegistrationProblem : UiLabels.NoShortcutFailures;
        _shortcutFailureDetails.Text = string.Join(Environment.NewLine, shortcutDetails);
        _shortcutFailureDetails.Visible = shortcutDetails.Count > 0;
        var printScreenFailure = _hotkeyFailures.Values.Any(failure => failure.PrintScreenSettingsEnabled && GetCurrentFailure(failure.Action, _shortcutInputs[failure.Action].Text) is not null);
        _printScreenHint.Text = UiLabels.PrintScreenSnippingHint;
        _printScreenPanel.Visible = printScreenFailure;
        _openKeyboardSettings.Visible = printScreenFailure;
        _printScreenHint.Visible = printScreenFailure;

        var nonShortcutIssue = allIssues.FirstOrDefault(issue => issue.Kind == SettingsIssueKind.InvalidValue && !_directoryIssues.ContainsKey(issue.SettingName));
        _formStatus.Text = nonShortcutIssue is null ? string.Empty : ValidationLabel(nonShortcutIssue.SettingName);
        if (nonShortcutIssue is not null) invalid = true;
        if (allIssues.Any(issue => issue.Kind is SettingsIssueKind.InvalidShortcut or SettingsIssueKind.DuplicateShortcut)) invalid = true;
        if (_directoryIssues.Keys.Any(invalidProperties.Contains)) invalid = true;
        _saveButton.Enabled = !invalid;
    }

    private static (string Label, string? Value)[] LoadMicrophoneChoices(string? savedDeviceId, DailyLog log, out bool enumerationFailed)
    {
        var choices = new List<(string Label, string? Value)> { (UiLabels.DefaultDevice, null) };
        enumerationFailed = false;
        try
        {
            choices.AddRange(Recorder.GetSystemAudioCaptureDevices()
                .Select(device => ($"{device.FriendlyName} ({device.ID})", (string?)device.ID)));
        }
        catch (Exception exception)
        {
            enumerationFailed = true;
            log.Write($"Enumerating microphone devices failed: {exception}");
        }

        if (!enumerationFailed && savedDeviceId is not null && choices.All(choice => !string.Equals(choice.Value, savedDeviceId, StringComparison.Ordinal)))
            choices.Add(($"{UiLabels.SavedMicrophoneDevice} ({UiLabels.DeviceNotFound})", savedDeviceId));
        return choices.ToArray();
    }

    private HotkeyFailure? GetCurrentFailure(RecorderAction action, string notation)
    {
        if (!_hotkeyFailures.TryGetValue(action, out var failure)) return null;
        return HotkeyShortcut.TryParse(notation, out var current) && HotkeyShortcut.TryParse(failure.Notation, out var failed) && current == failed ? failure : null;
    }

    private static string FailureLabel(HotkeyFailure failure) => failure.Reason switch
    {
        HotkeyFailureReason.Duplicate => UiLabels.ShortcutDuplicate,
        HotkeyFailureReason.InvalidNotation => UiLabels.ShortcutInvalid,
        _ when failure.PrintScreenSettingsEnabled => UiLabels.SettingsApplyPrintScreenFailed,
        _ => UiLabels.ShortcutRegistrationFailed
    };

    private static string ValidationLabel(string propertyName) => propertyName switch
    {
        nameof(Settings.StillImageDirectory) or nameof(Settings.VideoDirectory) => UiLabels.DirectoryRequired,
        _ => UiLabels.SettingsValuesInvalid
    };

    private void CaptureShortcut(RecorderAction action, TextBox input, KeyEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
        if (eventArgs.KeyCode is Keys.Back or Keys.Delete)
        {
            input.Clear();
            return;
        }
        if (eventArgs.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;

        var modifiers = HotkeyModifiers.None;
        if (eventArgs.Control) modifiers |= HotkeyModifiers.Control;
        if (eventArgs.Alt) modifiers |= HotkeyModifiers.Alt;
        if (eventArgs.Shift) modifiers |= HotkeyModifiers.Shift;
        if (GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0) modifiers |= HotkeyModifiers.Windows;
        if (HotkeyShortcut.TryCreate(modifiers, (int)eventArgs.KeyCode, out var shortcut)) input.Text = shortcut!.ToDisplayString();
    }

    private void Save()
    {
        RefreshValidation();
        if (!_saveButton.Enabled) return;
        var draft = ReadSettings();
        try
        {
            if (!_saveSettings(draft))
            {
                _formStatus.Text = UiLabels.SettingsSaveFailed;
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            _log.Write($"Settings save failed: {exception}");
            _formStatus.Text = UiLabels.SettingsSaveFailed;
        }
    }

    private void RestoreDefaults()
    {
        if (MessageBox.Show(this, UiLabels.RestoreDefaultsConfirmation, UiLabels.RestoreDefaultsTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        LoadSettings(new Settings());
    }

    private void BrowseForDirectory(TextBox input)
    {
        using var dialog = new FolderBrowserDialog { Description = UiLabels.FolderPickerTitle, UseDescriptionForTitle = true, SelectedPath = Directory.Exists(input.Text) ? input.Text : string.Empty };
        if (dialog.ShowDialog(this) == DialogResult.OK) input.Text = dialog.SelectedPath;
    }

    private void OpenLogsFolder() => OpenFolder(Path.GetDirectoryName(_log.CurrentFilePath)!);

    private void OpenLicense()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "LICENSE");
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true, ArgumentList = { path } });
        }
        catch (Exception exception)
        {
            _log.Write($"Open license failed: {exception}");
            _formStatus.Text = UiLabels.LicenseOpenFailed;
        }
    }

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
            _formStatus.Text = string.Format(UiLabels.FolderOpenFailed, exception.Message);
        }
    }

    private void OpenKeyboardSettings()
    {
        try
        {
            HotkeyManager.OpenPrintScreenKeyboardSettings();
        }
        catch (Exception exception)
        {
            _log.Write($"Open keyboard settings failed: {exception}");
            _formStatus.Text = UiLabels.KeyboardSettingsOpenFailed;
        }
    }

    private void UpdateEnablement()
    {
        if (_imageFormat is null || _audioFormat is null || _microphoneEnabled is null) return;
        foreach (var control in _jpegControls) control.Enabled = (StillImageFormat?)SelectedValue<StillImageFormat>(_imageFormat) == StillImageFormat.Jpeg;
        foreach (var control in _pngControls) control.Enabled = (StillImageFormat?)SelectedValue<StillImageFormat>(_imageFormat) == StillImageFormat.Png;
        foreach (var control in _aacControls) control.Enabled = (AudioFormat?)SelectedValue<AudioFormat>(_audioFormat) == AudioFormat.Aac;
        foreach (var control in _mp3Controls) control.Enabled = (AudioFormat?)SelectedValue<AudioFormat>(_audioFormat) == AudioFormat.Mp3;
        foreach (var control in _microphoneControls) control.Enabled = _microphoneEnabled.Checked && !_microphoneEnumerationFailed;
        _mp3Hint.Visible = (AudioFormat?)SelectedValue<AudioFormat>(_audioFormat) == AudioFormat.Mp3;
        foreach (var control in _audioControls) control.Enabled = true;
        _videoTab.Enabled = !_isRecording();
    }

    private static T? SelectedValue<T>(ComboBox combo) where T : struct => combo.SelectedItem is ChoiceOption<T> option ? option.Value : null;

    private static void SetShortcut(Settings settings, RecorderAction action, string notation)
    {
        if (HotkeyShortcut.TryParse(notation, out var shortcut) && shortcut is not null) notation = shortcut.ToDisplayString();
        switch (action)
        {
            case RecorderAction.ScreenshotRegion: settings.ScreenshotRegionShortcut = notation; break;
            case RecorderAction.ScreenshotFullScreen: settings.ScreenshotFullScreenShortcut = notation; break;
            case RecorderAction.ScreenshotWindow: settings.ScreenshotWindowShortcut = notation; break;
            case RecorderAction.RecordingRegion: settings.RecordingRegionShortcut = notation; break;
            case RecorderAction.RecordingFullScreen: settings.RecordingFullScreenShortcut = notation; break;
            case RecorderAction.RecordingWindow: settings.RecordingWindowShortcut = notation; break;
            case RecorderAction.PauseResume: settings.PauseRecordingShortcut = notation; break;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
