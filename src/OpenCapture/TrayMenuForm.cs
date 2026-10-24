using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace OpenCapture
{
    internal sealed class TrayMenuForm : Form
    {
        private readonly Font menuFont;
        private readonly Bitmap trayArtwork;
        private int hoveredItem;

        internal TrayMenuForm()
        {
            menuFont = OpenCaptureTheme.CreateFont(16.5f, FontStyle.Bold);
            trayArtwork = OpenCaptureTheme.LoadScaledBitmap(
                OpenCaptureTheme.TrayIconResourceName,
                new Size(34, 34));
            hoveredItem = -1;

            AutoScaleMode = AutoScaleMode.None;
            BackColor = OpenCaptureTheme.Accent;
            ClientSize = new Size(410, 264);
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "OpenCaptureTrayMenu";
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "OpenCapture";
            TopMost = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint,
                true);
        }

        internal event EventHandler QuitRequested;
        internal event EventHandler AutostartRequested;
        internal event EventHandler EditKeybindsRequested;
        internal event EventHandler ClearCapturesRequested;
        internal event EventHandler ClearRecordingsRequested;
        internal event EventHandler ClearClipboardRequested;

        internal bool AutostartEnabled { get; set; }
        internal bool CapturesClearable { get; set; }
        internal bool RecordingsClearable { get; set; }
        internal bool ClipboardClearable { get; set; }

        internal void ShowAt(Point screenPoint)
        {
            Rectangle working = Screen.FromPoint(screenPoint).WorkingArea;
            int left = Math.Max(working.Left, Math.Min(screenPoint.X - Width, working.Right - Width));
            int top = Math.Max(working.Top, Math.Min(screenPoint.Y - Height, working.Bottom - Height));
            Location = new Point(left, top);
            hoveredItem = -1;
            Invalidate();
            Show();
            Activate();
        }

        protected override void OnDeactivate(EventArgs eventArgs)
        {
            base.OnDeactivate(eventArgs);
            Hide();
        }

        protected override void OnResize(EventArgs eventArgs)
        {
            base.OnResize(eventArgs);
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                return;
            }

            using (GraphicsPath path = OpenCaptureTheme.CreateRoundedRectangle(ClientRectangle, 25))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null)
                {
                    old.Dispose();
                }
            }
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            Graphics graphics = eventArgs.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using (LinearGradientBrush background = new LinearGradientBrush(
                ClientRectangle,
                Color.FromArgb(111, 166, 248),
                Color.FromArgb(83, 184, 253),
                LinearGradientMode.Horizontal))
            {
                graphics.FillRectangle(background, ClientRectangle);
            }

            if (trayArtwork != null)
            {
                graphics.DrawImageUnscaled(trayArtwork, ClientSize.Width - 48, 10);
            }

            int index;
            for (index = 0; index < 6; index++)
            {
                DrawItem(graphics, index);
            }

            Rectangle outline = ClientRectangle;
            outline.Width--;
            outline.Height--;
            using (GraphicsPath path = OpenCaptureTheme.CreateRoundedRectangle(outline, 25))
            using (Pen border = new Pen(Color.FromArgb(105, Color.White), 1.0f))
            {
                graphics.DrawPath(border, path);
            }
        }

        protected override void OnMouseMove(MouseEventArgs eventArgs)
        {
            base.OnMouseMove(eventArgs);
            int hit = HitTest(eventArgs.Location);
            if (hit != hoveredItem)
            {
                hoveredItem = hit;
                Cursor = hit >= 0 && IsEnabled(hit) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            base.OnMouseLeave(eventArgs);
            if (hoveredItem != -1)
            {
                hoveredItem = -1;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs eventArgs)
        {
            base.OnMouseUp(eventArgs);
            if (eventArgs.Button != MouseButtons.Left)
            {
                return;
            }

            int item = HitTest(eventArgs.Location);
            if (item < 0 || !IsEnabled(item))
            {
                return;
            }

            Hide();
            EventHandler handler = null;
            if (item == 0) handler = QuitRequested;
            if (item == 1) handler = AutostartRequested;
            if (item == 2) handler = EditKeybindsRequested;
            if (item == 3) handler = ClearCapturesRequested;
            if (item == 4) handler = ClearRecordingsRequested;
            if (item == 5) handler = ClearClipboardRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Escape)
            {
                Hide();
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                menuFont.Dispose();
                if (trayArtwork != null)
                {
                    trayArtwork.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private void DrawItem(Graphics graphics, int index)
        {
            Rectangle bounds = GetItemBounds(index);
            bool enabled = IsEnabled(index);
            if (index == hoveredItem && enabled)
            {
                using (GraphicsPath hoverPath = OpenCaptureTheme.CreateRoundedRectangle(bounds, 12))
                using (SolidBrush hover = new SolidBrush(Color.FromArgb(38, Color.White)))
                {
                    graphics.FillPath(hover, hoverPath);
                }
            }

            string label;
            Color color;
            if (index == 0)
            {
                label = "[ Quit ]";
                color = OpenCaptureTheme.Danger;
            }
            else if (index == 1)
            {
                label = "[ Autostart ]";
                color = AutostartEnabled ? OpenCaptureTheme.Enabled : Color.White;
            }
            else if (index == 2)
            {
                label = "[ Edit Keybinds ]";
                color = Color.White;
            }
            else if (index == 3)
            {
                label = "[ Clear Captures ]";
                color = enabled ? OpenCaptureTheme.Danger : OpenCaptureTheme.Disabled;
            }
            else if (index == 4)
            {
                label = "[ Clear Recordings ]";
                color = enabled ? OpenCaptureTheme.Danger : OpenCaptureTheme.Disabled;
            }
            else
            {
                label = "[ Clear Clipboard ]";
                color = enabled ? OpenCaptureTheme.Danger : OpenCaptureTheme.Disabled;
            }

            TextRenderer.DrawText(
                graphics,
                label,
                menuFont,
                bounds,
                color,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
        }

        private bool IsEnabled(int index)
        {
            if (index == 3) return CapturesClearable;
            if (index == 4) return RecordingsClearable;
            if (index == 5) return ClipboardClearable;
            return index >= 0 && index < 6;
        }

        private int HitTest(Point point)
        {
            int index;
            for (index = 0; index < 6; index++)
            {
                if (GetItemBounds(index).Contains(point))
                {
                    return index;
                }
            }
            return -1;
        }

        private Rectangle GetItemBounds(int index)
        {
            return new Rectangle(20, 13 + index * 40, ClientSize.Width - 40, 38);
        }
    }
}
