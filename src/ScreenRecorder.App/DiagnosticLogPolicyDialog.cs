using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class DiagnosticLogPolicyDialog : Form
{
    private readonly Font _boldFont;
    private readonly Button _closeButton = new() { Text = UiLabels.Close, AutoSize = true, MinimumSize = new Size(96, 0), DialogResult = DialogResult.Cancel };
    private readonly RichTextBox _policyText = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle,
        DetectUrls = false,
        HideSelection = false,
        WordWrap = true,
        ScrollBars = RichTextBoxScrollBars.Vertical
    };

    public DiagnosticLogPolicyDialog()
    {
        Text = UiLabels.DiagnosticLogPolicyTitle;
        Name = nameof(DiagnosticLogPolicyDialog);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(480, 400);
        ClientSize = new Size(640, 540);

        _boldFont = new Font(Font, FontStyle.Bold);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var promise = new Label
        {
            Text = UiLabels.DiagnosticLogPolicyPromise,
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Font = _boldFont,
            Margin = new Padding(4, 4, 4, 12)
        };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        footer.Controls.Add(_closeButton);
        layout.Controls.Add(promise, 0, 0);
        layout.Controls.Add(_policyText, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
        CancelButton = _closeButton;
        AcceptButton = _closeButton;
        Shown += (_, _) => _closeButton.Focus();
        FormClosed += (_, _) => _boldFont.Dispose();

        AddHeading(UiLabels.DiagnosticLogPolicyLocationHeading);
        AddParagraph(string.Format(UiLabels.DiagnosticLogPolicyLocation, DiagnosticLog.LogsDirectory));
        AddHeading(UiLabels.DiagnosticLogPolicyLevelsHeading);
        AddParagraph(UiLabels.DiagnosticLogPolicyLevels);
        AddHeading(UiLabels.DiagnosticLogPolicyExcludedHeading);
        AddParagraph(UiLabels.DiagnosticLogPolicyExcluded);
        AddHeading(UiLabels.DiagnosticLogPolicyIncludedHeading);
        AddParagraph(UiLabels.DiagnosticLogPolicyIncluded);
        AddHeading(UiLabels.DiagnosticLogPolicyPathHeading);
        AddParagraph(UiLabels.DiagnosticLogPolicyPath);
        _policyText.Select(0, 0);
    }

    private void AddHeading(string heading)
    {
        var start = _policyText.TextLength;
        _policyText.AppendText(heading + Environment.NewLine);
        _policyText.Select(start, heading.Length);
        _policyText.SelectionFont = _boldFont;
        _policyText.Select(_policyText.TextLength, 0);
        _policyText.SelectionFont = _policyText.Font;
    }

    private void AddParagraph(string paragraph) => _policyText.AppendText(paragraph + Environment.NewLine + Environment.NewLine);
}
