using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace OpenCapture
{
    internal sealed class CaptureReviewForm : FloatingPanelForm
    {
        internal const int ReviewMilliseconds = 2000;
        private readonly PendingCapture capture;
        private readonly Font titleFont;
        private readonly Font detailFont;
        private readonly PresentationClock countdown;
        private readonly Stopwatch clock = new Stopwatch();
        private ClickAwayWatcher clickWatcher;
        private CaptureCropForm cropEditor;
        private Bitmap thumbnail;
        private Bitmap backgroundSurface;
        private Bitmap reviewSurface;
        private bool backgroundDirty = true;
        private bool editing;
        private bool decided;
        private bool resourcesDisposed;
        private int pressedAction = -1;
        private int hoverAction = -1;
        private Rectangle cropBeforeEditing;
        private readonly long recordingBytes;
        private readonly Rectangle panel = new Rectangle(0, 0, 250, 181);

        internal event EventHandler<CaptureReviewDecisionEventArgs> Decided;
        internal PendingCapture CaptureResult { get { return capture; } }
        internal bool IsEditing { get { return editing; } }
        internal CaptureCropForm CropEditor { get { return cropEditor; } }
        internal double RemainingFraction { get { return editing ? 1.0 : RemainingAt(clock.Elapsed.TotalMilliseconds); } }

        internal CaptureReviewForm(PendingCapture pending) : base(false)
        {
            capture = pending;
            if (pending.Kind == CaptureKind.Recording && pending.VideoPath != null)
                try { recordingBytes = new System.IO.FileInfo(pending.VideoPath).Length; }
                catch (Exception exception) { ErrorLog.Write("Could not read review recording size", exception); }
            titleFont = OpenCaptureTheme.CreateFont(10.0f, FontStyle.Bold);
            detailFont = OpenCaptureTheme.CreateFont(9.0f, FontStyle.Regular);
            countdown = new PresentationClock(this);
            countdown.Interval = 16;
            countdown.Tick += TickCountdown;
            ClientSize = new Size(panel.Width + 7, panel.Height + 8);
            KeyPreview = true;
            AccessibleName = "Review capture: Confirm, Crop, Discard";
            Text = "Review capture";
            RefreshThumbnail();
        }

        internal Rectangle ActionBounds(int index)
        {
            if (!capture.CanCrop)
                return index == 0 ? new Rectangle(12, 145, 110, 24) :
                    index == 2 ? new Rectangle(128, 145, 110, 24) : Rectangle.Empty;
            return new Rectangle(12 + index * 77, 145, 72, 24);
        }

        private int HitAction(Point point)
        {
            for (int index = 0; index < 3; index++)
                if (ActionBounds(index).Contains(point)) return index;
            return -1;
        }

        internal void ShowNear(Point point)
        {
            Rectangle safe = Screen.FromPoint(point).WorkingArea;
            safe.Inflate(-8, -8);
            Location = OverlayForm.ChoosePanelRectangle(safe, point, Rectangle.Empty, Size, 18, 10).Location;
            Show();
        }

        protected override void OnShown(EventArgs args)
        {
            base.OnShown(args);
            StartCountdown();
        }

        private void StartCountdown()
        {
            clock.Restart();
            countdown.Start();
            if (clickWatcher != null) clickWatcher.Dispose();
            clickWatcher = new ClickAwayWatcher(OutsideClicked);
            RefreshSurface();
        }

        private void OutsideClicked(Point point)
        {
            if (!decided && !editing && !Bounds.Contains(point) && IsHandleCreated)
                BeginInvoke((MethodInvoker)delegate { if (!editing) Finish(true); });
        }

        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            int action = HitAction(args.Location);
            Cursor = action >= 0 ? Cursors.Hand : Cursors.Default;
            if (action != hoverAction) { hoverAction = action; backgroundDirty = true; RefreshSurface(); }
        }

        protected override void OnMouseLeave(EventArgs args)
        {
            base.OnMouseLeave(args);
            if (hoverAction < 0) return;
            hoverAction = -1;
            backgroundDirty = true;
            RefreshSurface();
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            if (args.Button == MouseButtons.Left) pressedAction = HitAction(args.Location);
        }

        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            int action = pressedAction;
            pressedAction = -1;
            if (args.Button != MouseButtons.Left || action < 0 || HitAction(args.Location) != action) return;
            if (action == 0) Finish(true);
            else if (action == 2) Finish(false);
            else if (editing) EndCrop(true);
            else EditCrop();
        }

        private void TickCountdown(object sender, EventArgs args)
        {
            if (editing || decided) return;
            if (clock.ElapsedMilliseconds >= ReviewMilliseconds &&
                Control.MouseButtons == MouseButtons.None && pressedAction < 0) { Finish(true); return; }
            RefreshSurface();
        }

        internal static double RemainingAt(double elapsedMilliseconds)
        { return Math.Max(0.0, Math.Min(1.0, 1.0 - elapsedMilliseconds / ReviewMilliseconds)); }

        internal void Finish(bool accepted)
        {
            if (decided || IsDisposed) return;
            if (editing) EndCrop(true);
            decided = true;
            countdown.Stop();
            if (clickWatcher != null) { clickWatcher.Dispose(); clickWatcher = null; }
            Hide();
            EventHandler<CaptureReviewDecisionEventArgs> handler = Decided;
            if (handler != null) handler(this, new CaptureReviewDecisionEventArgs(accepted));
            if (!IsDisposed) Close();
        }

        internal void FinishForShutdown() { Finish(true); }

        protected override void OnFormClosing(FormClosingEventArgs args)
        {
            if (!decided) { args.Cancel = true; Finish(true); return; }
            base.OnFormClosing(args);
        }

        private void EditCrop()
        {
            if (editing || decided || !capture.CanCrop) return;
            editing = true;
            backgroundDirty = true;
            cropBeforeEditing = capture.Crop;
            countdown.Stop();
            if (clickWatcher != null) { clickWatcher.Dispose(); clickWatcher = null; }
            try
            {
                cropEditor = new CaptureCropForm(capture.Image, capture.DesktopBounds, capture.Crop);
                cropEditor.SelectionChanged += CropChanged;
                cropEditor.CropFinished += CropFinished;
                cropEditor.CropCancelled += CropCancelled;
                cropEditor.FormClosed += CropClosed;
                cropEditor.Show();
                Rectangle safe = Screen.FromPoint(Cursor.Position).WorkingArea;
                safe.Inflate(-8, -8);
                Rectangle target = new Rectangle(capture.DesktopBounds.Left + capture.Crop.Left,
                    capture.DesktopBounds.Top + capture.Crop.Top, capture.Crop.Width, capture.Crop.Height);
                Location = OverlayForm.ChoosePanelRectangle(safe, Cursor.Position, target, Size, 18, 10).Location;
                BringToFront();
                RefreshSurface();
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not show crop handles", exception);
                EndCrop(false);
            }
        }

        private void CropChanged(object sender, EventArgs args)
        {
            capture.SetCrop(cropEditor.Selection);
            RefreshThumbnail();
            RefreshSurface();
        }
        private void CropFinished(object sender, EventArgs args) { EndCrop(true); }
        private void CropCancelled(object sender, EventArgs args) { EndCrop(false); }
        private void CropClosed(object sender, FormClosedEventArgs args) { EndCrop(true); }

        internal void EndCrop(bool apply)
        {
            if (!editing) return;
            editing = false;
            if (cropEditor != null)
            {
                cropEditor.SelectionChanged -= CropChanged;
                cropEditor.CropFinished -= CropFinished;
                cropEditor.CropCancelled -= CropCancelled;
                cropEditor.FormClosed -= CropClosed;
                cropEditor.Dispose();
                cropEditor = null;
            }
            if (!apply) capture.SetCrop(cropBeforeEditing);
            RefreshThumbnail();
            if (!decided && !IsDisposed) StartCountdown();
        }

        private void RefreshThumbnail()
        {
            backgroundDirty = true;
            if (thumbnail != null) thumbnail.Dispose();
            thumbnail = null;
            if (capture.Image == null) return;
            Rectangle fit = CaptureCropForm.FitImage(capture.Crop.Size, new Rectangle(0, 0, 121, 121));
            thumbnail = new Bitmap(fit.Width, fit.Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(thumbnail))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(capture.Image, new Rectangle(Point.Empty, thumbnail.Size), capture.Crop, GraphicsUnit.Pixel);
            }
        }

        internal Bitmap RenderSurface()
        {
            Bitmap surface = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(surface)) DrawReview(graphics);
                return surface;
            }
            catch { surface.Dispose(); throw; }
        }

        private void RefreshSurface()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (reviewSurface == null || reviewSurface.Size != ClientSize)
            {
                if (reviewSurface != null) reviewSurface.Dispose();
                if (backgroundSurface != null) backgroundSurface.Dispose();
                reviewSurface = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
                backgroundSurface = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
                backgroundDirty = true;
            }
            if (backgroundDirty)
            {
                using (Graphics graphics = Graphics.FromImage(backgroundSurface))
                {
                    graphics.Clear(Color.Transparent);
                    DrawReviewContent(graphics);
                }
                backgroundDirty = false;
            }
            using (Graphics graphics = Graphics.FromImage(reviewSurface))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(backgroundSurface, 0, 0);
                graphics.CompositingMode = CompositingMode.SourceOver;
                DrawCountdown(graphics, RemainingFraction);
            }
            Present(reviewSurface);
        }

        protected override void OnPaint(PaintEventArgs args) { DrawReview(args.Graphics); }

        private void DrawReview(Graphics graphics)
        {
            DrawReviewContent(graphics);
            DrawCountdown(graphics, RemainingFraction);
        }

        private void DrawReviewContent(Graphics graphics)
        {
            FloatingPanelForm.DrawChrome(graphics, panel, OpenCaptureTheme.AccentDeep);
            Rectangle preview = new Rectangle(12, 12, 121, 121);
            if (thumbnail != null)
                graphics.DrawImageUnscaled(thumbnail, preview.Left + (preview.Width - thumbnail.Width) / 2,
                    preview.Top + (preview.Height - thumbnail.Height) / 2);
            else if (capture.Kind == CaptureKind.Colour)
                using (SolidBrush colour = new SolidBrush(ColorTranslator.FromHtml(capture.Text)))
                    graphics.FillRectangle(colour, preview);
            using (Pen edge = new Pen(Color.FromArgb(220, 20, 27, 34), 1)) graphics.DrawRectangle(edge, preview);

            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            string title = capture.Kind == CaptureKind.Screenshot ? "Screenshot" :
                capture.Kind == CaptureKind.Recording ? "Recording" : capture.Kind == CaptureKind.Text ? "Text capture" : "HEX colour";
            using (SolidBrush text = new SolidBrush(Color.FromArgb(20, 27, 34)))
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.FormatFlags = StringFormatFlags.NoWrap;
                graphics.DrawString(title, titleFont, text, new RectangleF(148, 12, 90, 24), format);
                string detail = capture.Kind == CaptureKind.Colour ? capture.Text :
                    capture.Crop.Width + " × " + capture.Crop.Height;
                graphics.DrawString(detail, detailFont, text, new RectangleF(148, 44, 90, 20), format);
                if (capture.Kind == CaptureKind.Recording)
                    graphics.DrawString(OverlayForm.FormatCurrentStorage(recordingBytes), detailFont, text,
                        new RectangleF(148, 65, 90, 20), format);
            }
            string[] labels = new string[] { "Confirm", editing ? "Done" : "Crop", "Discard" };
            for (int index = 0; index < 3; index++)
            {
                Rectangle action = ActionBounds(index);
                if (action.IsEmpty) continue;
                using (GraphicsPath shape = OpenCaptureTheme.CreateRoundedRectangle(action, 5))
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(index == hoverAction ? 170 : 75, Color.White)))
                using (Pen edge = new Pen(Color.FromArgb(190, OpenCaptureTheme.AccentDeep), 1))
                using (SolidBrush text = new SolidBrush(Color.FromArgb(20, 27, 34)))
                using (StringFormat format = new StringFormat())
                {
                    graphics.FillPath(fill, shape);
                    graphics.DrawPath(edge, shape);
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    graphics.DrawString(labels[index], detailFont, text, action, format);
                }
            }
        }

        internal static void DrawCountdown(Graphics graphics, double fraction)
        {
            RectangleF ring = new RectangleF(205, 98, 25, 25);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen track = new Pen(Color.FromArgb(95, OpenCaptureTheme.AccentDeep), 2))
            using (Pen remaining = new Pen(OpenCaptureTheme.AccentDeep, 2))
            {
                graphics.DrawEllipse(track, ring);
                // GDI+ can reject near-degenerate arcs. Below half a degree the
                // remaining stroke is subpixel; a complete ring uses an ellipse.
                float sweep = (float)(360.0 * Math.Max(0, Math.Min(1, fraction)));
                if (sweep >= 359.5f) graphics.DrawEllipse(remaining, ring);
                else if (sweep >= 0.5f) graphics.DrawArc(remaining, ring, 270.0f - sweep, sweep);
            }
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == Keys.Escape && editing) { EndCrop(false); return true; }
            if (keys == Keys.Enter && editing) { EndCrop(true); return true; }
            if (keys == Keys.Escape) { Finish(false); return true; }
            if (keys == Keys.Enter) { Finish(true); return true; }
            return base.ProcessCmdKey(ref message, keys);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed)
            {
                resourcesDisposed = true;
                countdown.Dispose();
                if (clickWatcher != null) clickWatcher.Dispose();
                if (cropEditor != null) cropEditor.Dispose();
                if (thumbnail != null) thumbnail.Dispose();
                if (backgroundSurface != null) backgroundSurface.Dispose();
                if (reviewSurface != null) reviewSurface.Dispose();
                titleFont.Dispose();
                detailFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class CaptureReviewDecisionEventArgs : EventArgs
    {
        internal readonly bool Accepted;
        internal CaptureReviewDecisionEventArgs(bool accepted) { Accepted = accepted; }
    }
}
