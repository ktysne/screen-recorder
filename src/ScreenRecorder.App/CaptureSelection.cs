using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

internal sealed class ScreenshotSelection(ScreenshotMode mode, Rectangle bounds, Bitmap? frozenImage, IntPtr window, string? windowTitle) : IDisposable
{
    private Bitmap? _frozenImage = frozenImage;
    public ScreenshotMode Mode { get; } = mode;
    public Rectangle Bounds { get; } = bounds;
    public Bitmap? FrozenImage => _frozenImage;
    public IntPtr Window { get; } = window;
    public string? WindowTitle { get; } = windowTitle;

    public Bitmap DetachFrozenImage() => Interlocked.Exchange(ref _frozenImage, null)
        ?? throw new InvalidOperationException("選択時の画面がありません。");

    public void Dispose() => Interlocked.Exchange(ref _frozenImage, null)?.Dispose();
}

internal static class CaptureSelection
{
    public static bool TryCreateWindowSelection(IntPtr window, out ScreenshotSelection? selection, out string reason)
    {
        selection = null;
        if (!TryGetWindowInfo(window, out var bounds, out var title, out reason)) return false;
        // 選択画面ではクリックでデスクトップも撮れるが、前面がデスクトップのときは撮りたいウィンドウを選ばせる。
        if (IsDesktopWindow(window))
        {
            reason = "前面がデスクトップです";
            return false;
        }
        selection = new ScreenshotSelection(ScreenshotMode.Window, bounds, null, window, title);
        return true;
    }

    private static bool IsDesktopWindow(IntPtr window)
    {
        var className = new System.Text.StringBuilder(256);
        return NativeMethods.GetClassName(window, className, className.Capacity) != 0
            && className.ToString() is "Progman" or "WorkerW";
    }

    private static bool TryGetWindowInfo(IntPtr window, out Rectangle bounds, out string title, out string reason)
    {
        bounds = Rectangle.Empty;
        title = string.Empty;
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
        {
            reason = "前面のウィンドウがありません";
            return false;
        }
        if (!NativeMethods.IsWindowVisible(window))
        {
            reason = "ウィンドウが表示されていません";
            return false;
        }
        if (NativeMethods.IsIconic(window))
        {
            reason = "ウィンドウが最小化されています";
            return false;
        }
        if (NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DwmaCloaked, out int cloaked, sizeof(int)) != 0 || cloaked != 0)
        {
            reason = "ウィンドウを撮影できません";
            return false;
        }

        var hasFrameBounds = NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DwmaExtendedFrameBounds, out NativeMethods.NativeRect frame, Marshal.SizeOf<NativeMethods.NativeRect>()) == 0
            && frame.Right > frame.Left && frame.Bottom > frame.Top;
        var rectangle = hasFrameBounds ? frame : default;
        if (!hasFrameBounds && !NativeMethods.GetWindowRect(window, out rectangle))
        {
            reason = "ウィンドウの撮影範囲を取得できません";
            return false;
        }
        bounds = Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            reason = "ウィンドウの撮影範囲が空です";
            return false;
        }

        var length = NativeMethods.GetWindowTextLength(window);
        var titleBuffer = new System.Text.StringBuilder(Math.Max(1, length + 1));
        NativeMethods.GetWindowText(window, titleBuffer, titleBuffer.Capacity);
        title = titleBuffer.ToString();
        reason = string.Empty;
        return true;
    }

    public static async Task<ScreenshotSelection?> SelectAsync(ScreenshotMode mode, bool freezeDesktop = true)
    {
        var snapshots = new List<DisplaySnapshot>();
        try
        {
            foreach (var screen in Screen.AllScreens)
                snapshots.Add(new DisplaySnapshot(screen.Bounds, freezeDesktop ? DesktopCapture.Capture(screen.Bounds) : null));
        }
        catch
        {
            foreach (var snapshot in snapshots) snapshot.Dispose();
            throw;
        }
        var displays = snapshots.ToArray();
        if (displays.Length == 0) return null;
        var session = new CaptureSelectionSession(mode, displays, freezeDesktop);
        var forms = new List<CaptureSelectionForm>();
        try
        {
            foreach (var display in displays) forms.Add(new CaptureSelectionForm(session, display));
            session.SetForms(forms.ToArray());
            foreach (var form in forms) _ = form.Handle;
            foreach (var form in forms) form.Show();
            session.UpdatePointer();
            forms[0].Activate();
            return await session.Completion;
        }
        finally
        {
            foreach (var form in forms)
            {
                if (!form.IsDisposed) form.Close();
                form.Dispose();
            }
            foreach (var display in displays) display.Dispose();
        }
    }

    internal sealed class DisplaySnapshot(Rectangle bounds, Bitmap? image) : IDisposable
    {
        public Rectangle Bounds { get; } = bounds;
        public Bitmap? Image { get; } = image;
        public void Dispose() => Image?.Dispose();
    }

    private sealed class CaptureSelectionSession(ScreenshotMode mode, DisplaySnapshot[] displays, bool freezeDesktop)
    {
        private readonly TaskCompletionSource<ScreenshotSelection?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CaptureSelectionForm[] _forms = [];
        private Point? _dragStart;
        private Rectangle? _dragDisplay;
        private IntPtr _highlightedWindow;
        private Rectangle _highlightedBounds;

        public Task<ScreenshotSelection?> Completion => _completion.Task;
        public bool IsCompleted => _completion.Task.IsCompleted;
        public ScreenshotMode Mode => mode;
        public bool FreezeDesktop => freezeDesktop;
        public Point? DragStart => _dragStart;
        public Rectangle? CurrentRegion => _dragStart is { } start && _dragDisplay is { } display
            ? ScreenshotRegion.Normalize(start, Cursor.Position, display)
            : null;
        public Rectangle? HighlightedWindowBounds => _highlightedWindow == IntPtr.Zero ? null : _highlightedBounds;

        public void SetForms(CaptureSelectionForm[] forms) => _forms = forms;

        public void BeginRegion(Point start, Rectangle display)
        {
            _dragStart = start;
            _dragDisplay = display;
            InvalidateForms();
        }

        public void UpdatePointer()
        {
            try
            {
                if (mode == ScreenshotMode.Window) UpdateWindowHighlight();
                InvalidateForms();
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        public void EndRegion(Point end)
        {
            if (_dragStart is not { } start || _dragDisplay is not { } display) return;
            var bounds = ScreenshotRegion.Normalize(start, end, display);
            if (bounds is null)
            {
                _dragStart = null;
                _dragDisplay = null;
                InvalidateForms();
                return;
            }

            var snapshot = displays.First(item => item.Bounds == display).Image;
            Bitmap? frozen = null;
            if (snapshot is not null)
            {
                frozen = new Bitmap(bounds.Value.Width, bounds.Value.Height, PixelFormat.Format32bppArgb);
                try
                {
                    using var graphics = Graphics.FromImage(frozen);
                    graphics.DrawImage(snapshot,
                        new Rectangle(Point.Empty, bounds.Value.Size),
                        new Rectangle(bounds.Value.X - display.X, bounds.Value.Y - display.Y, bounds.Value.Width, bounds.Value.Height),
                        GraphicsUnit.Pixel);
                }
                catch
                {
                    frozen.Dispose();
                    throw;
                }
            }
            Complete(new ScreenshotSelection(ScreenshotMode.Region, bounds.Value, frozen, IntPtr.Zero, null));
        }

        public void SelectWindowAtPointer()
        {
            var target = FindWindowUnderCursor();
            if (target is null) return;
            Complete(new ScreenshotSelection(ScreenshotMode.Window, target.Value.Bounds, null, target.Value.Window, target.Value.Title));
        }

        public void Cancel() => Complete(null);

        private void UpdateWindowHighlight()
        {
            var target = FindWindowUnderCursor();
            _highlightedWindow = target?.Window ?? IntPtr.Zero;
            _highlightedBounds = target?.Bounds ?? Rectangle.Empty;
        }

        private (IntPtr Window, Rectangle Bounds, string Title)? FindWindowUnderCursor()
        {
            if (!NativeMethods.GetCursorPos(out var pointer)) return null;
            var ownProcessId = (uint)Environment.ProcessId;
            var excluded = _forms.Select(form => form.Handle).ToHashSet();
            (IntPtr Window, Rectangle Bounds, string Title)? match = null;
            Exception? failure = null;
            NativeMethods.EnumWindows((window, parameter) =>
            {
                try
                {
                    NativeMethods.GetWindowThreadProcessId(window, out var processId);
                    if (processId == ownProcessId || excluded.Contains(window) || !CaptureSelection.TryGetWindowInfo(window, out var bounds, out var title, out _)) return true;
                    if (!bounds.Contains(new Point(pointer.X, pointer.Y))) return true;
                    match = (window, bounds, title);
                    return false;
                }
                catch (Exception exception)
                {
                    failure = exception;
                    return false;
                }
            }, IntPtr.Zero);
            if (failure is not null) throw new InvalidOperationException("ウィンドウを判定できませんでした。", failure);
            return match;
        }

        private void Complete(ScreenshotSelection? selection)
        {
            if (_completion.TrySetResult(selection)) CloseForms();
        }

        public void Fail(Exception exception)
        {
            if (_completion.TrySetException(exception)) CloseForms();
        }

        private void CloseForms()
        {
            foreach (var form in _forms)
                if (!form.IsDisposed) form.Close();
        }

        private void InvalidateForms()
        {
            foreach (var form in _forms)
                if (!form.IsDisposed) form.Invalidate();
        }
    }

    private sealed class CaptureSelectionForm : Form
    {
        private const int GuideMargin = 18;
        private readonly CaptureSelectionSession session;
        private readonly DisplaySnapshot display;

        public CaptureSelectionForm(CaptureSelectionSession session, DisplaySnapshot display)
        {
            this.session = session;
            this.display = display;
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = display.Bounds;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            TabStop = true;
            BackColor = Color.Black;
            if (!session.FreezeDesktop) Opacity = 0.34;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            FormClosing += (_, _) => { if (!session.IsCompleted) session.Cancel(); };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                session.Cancel();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                session.Cancel();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            try
            {
                if (session.Mode == ScreenshotMode.Region) session.BeginRegion(PointToScreen(e.Location), display.Bounds);
                else session.SelectWindowAtPointer();
            }
            catch (Exception exception)
            {
                session.Fail(exception);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            session.UpdatePointer();
            if (session.Mode == ScreenshotMode.Region && e.Button == MouseButtons.Left) Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (session.Mode == ScreenshotMode.Region && e.Button == MouseButtons.Left)
            {
                try { session.EndRegion(PointToScreen(e.Location)); }
                catch (Exception exception) { session.Fail(exception); }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (session.FreezeDesktop && display.Image is not null)
            {
                e.Graphics.DrawImageUnscaled(display.Image, Point.Empty);
                using (var dim = new SolidBrush(Color.FromArgb(145, Color.Black))) e.Graphics.FillRectangle(dim, ClientRectangle);
                if (session.Mode == ScreenshotMode.Region && session.CurrentRegion is { } region) DrawBrightRegion(e.Graphics, region);
                if (session.Mode == ScreenshotMode.Window && session.HighlightedWindowBounds is { } frame) DrawWindowFrame(e.Graphics, frame);
            }
            else if (session.Mode == ScreenshotMode.Region && session.CurrentRegion is { } liveRegion)
            {
                DrawRegionOutline(e.Graphics, liveRegion);
            }
            else if (session.Mode == ScreenshotMode.Window && session.HighlightedWindowBounds is { } liveFrame)
            {
                DrawWindowFrame(e.Graphics, liveFrame);
            }

            DrawGuide(e.Graphics);
            if (session.Mode == ScreenshotMode.Region && session.DragStart is not null && display.Bounds.Contains(Cursor.Position) && session.CurrentRegion is { } sizeRegion)
                DrawSize(e.Graphics, sizeRegion);
        }

        private void DrawBrightRegion(Graphics graphics, Rectangle bounds)
        {
            var visible = Rectangle.Intersect(bounds, display.Bounds);
            if (visible.IsEmpty || display.Image is null) return;
            var local = new Rectangle(visible.X - display.Bounds.X, visible.Y - display.Bounds.Y, visible.Width, visible.Height);
            graphics.DrawImage(display.Image, local, local, GraphicsUnit.Pixel);
            DrawRegionOutline(graphics, bounds);
        }

        private void DrawRegionOutline(Graphics graphics, Rectangle bounds)
        {
            var visible = Rectangle.Intersect(bounds, display.Bounds);
            if (visible.IsEmpty) return;
            visible.Offset(-display.Bounds.X, -display.Bounds.Y);
            using var pen = new Pen(Color.White, 2);
            graphics.DrawRectangle(pen, visible.X, visible.Y, Math.Max(0, visible.Width - 1), Math.Max(0, visible.Height - 1));
        }

        private void DrawWindowFrame(Graphics graphics, Rectangle bounds)
        {
            var visible = Rectangle.Intersect(bounds, display.Bounds);
            if (visible.IsEmpty) return;
            visible.Offset(-display.Bounds.X, -display.Bounds.Y);
            using var pen = new Pen(Color.Gold, 3);
            graphics.DrawRectangle(pen, visible.X, visible.Y, Math.Max(0, visible.Width - 1), Math.Max(0, visible.Height - 1));
        }

        private void DrawGuide(Graphics graphics)
        {
            var text = session.Mode == ScreenshotMode.Region
                ? "ドラッグで範囲を選択　Esc または右クリックで取り消し"
                : "クリックでウィンドウを選択　Esc または右クリックで取り消し";
            using var font = new Font("Yu Gothic UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
            var size = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding);
            var box = new Rectangle(GuideMargin, GuideMargin, size.Width + 24, size.Height + 14);
            using var background = new SolidBrush(Color.FromArgb(220, 20, 20, 20));
            graphics.FillRectangle(background, box);
            TextRenderer.DrawText(graphics, text, font, new Point(box.X + 12, box.Y + 7), Color.White, TextFormatFlags.NoPadding);
        }

        private void DrawSize(Graphics graphics, Rectangle bounds)
        {
            var text = $"{bounds.Width} × {bounds.Height}";
            using var font = new Font("Yu Gothic UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding);
            var cursor = Cursor.Position;
            var left = cursor.X + 18;
            var top = cursor.Y + 18;
            if (left + measured.Width + 20 > display.Bounds.Right) left = cursor.X - measured.Width - 30;
            if (top + measured.Height + 14 > display.Bounds.Bottom) top = cursor.Y - measured.Height - 24;
            left = Math.Clamp(left, display.Bounds.Left, Math.Max(display.Bounds.Left, display.Bounds.Right - measured.Width - 20));
            top = Math.Clamp(top, display.Bounds.Top, Math.Max(display.Bounds.Top, display.Bounds.Bottom - measured.Height - 14));
            var box = new Rectangle(left - display.Bounds.X, top - display.Bounds.Y, measured.Width + 20, measured.Height + 14);
            using var background = new SolidBrush(Color.FromArgb(230, 20, 20, 20));
            graphics.FillRectangle(background, box);
            TextRenderer.DrawText(graphics, text, font, new Point(box.X + 10, box.Y + 7), Color.White, TextFormatFlags.NoPadding);
        }
    }
}
