using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace OpenCapture
{
    internal sealed class TrayMenuForm : Form
    {
        private readonly Font menuFont;
        private readonly Font titleFont;
        private readonly Font detailFont;
        private readonly Bitmap trayArtwork;
        private readonly MenuButton[] items = new MenuButton[6];
        private uint menuDpi = 96;
        private bool mediaStatePending;
        private bool autostartEnabled;

        internal TrayMenuForm()
        {
            menuFont = OpenCaptureTheme.CreateFont(10.5f, FontStyle.Regular);
            titleFont = OpenCaptureTheme.CreateFont(12.0f, FontStyle.Bold);
            detailFont = OpenCaptureTheme.CreateFont(9.0f, FontStyle.Regular);
            trayArtwork = OpenCaptureTheme.LoadScaledBitmap(OpenCaptureTheme.TrayIconResourceName, new Size(40, 40));
            AutoScaleMode = AutoScaleMode.None;
            BackColor = SystemInformation.HighContrast ? SystemColors.Menu : Color.FromArgb(250, 252, 255);
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "OpenCaptureTrayMenu";
            AccessibleRole = AccessibleRole.MenuPopup;
            AccessibleName = "OpenCapture menu";
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "OpenCapture";
            TopMost = true;
            string[] labels = { "Edit shortcuts", "Start with Windows", "Clear captures", "Clear recordings", "Clear clipboard", "Quit OpenCapture" };
            for (int index = 0; index < items.Length; index++)
            {
                int action = index;
                MenuButton item = new MenuButton();
                item.Text = labels[index];
                item.Name = "TrayAction" + index;
                item.Font = menuFont;
                item.Danger = index >= 2;
                item.TabIndex = index;
                item.AccessibleRole = AccessibleRole.MenuItem;
                item.Click += delegate { ActivateItem(action); };
                items[index] = item;
                Controls.Add(item);
            }
            ApplyDpi(96);
        }

        internal event EventHandler QuitRequested;
        internal event EventHandler AutostartRequested;
        internal event EventHandler EditKeybindsRequested;
        internal event EventHandler ClearCapturesRequested;
        internal event EventHandler ClearRecordingsRequested;
        internal event EventHandler ClearClipboardRequested;

        internal bool AutostartEnabled
        {
            get { return autostartEnabled; }
            set
            {
                autostartEnabled = value;
                items[1].Detail = value ? "On" : "Off";
                items[1].AccessibleName = "Start with Windows: " + items[1].Detail;
                items[1].Invalidate();
            }
        }
        internal bool CapturesClearable { get { return items[2].Enabled; } set { items[2].Enabled = value; } }
        internal bool RecordingsClearable { get { return items[3].Enabled; } set { items[3].Enabled = value; } }
        internal bool ClipboardClearable { get { return items[4].Enabled; } set { items[4].Enabled = value; } }

        // Folder enumeration can be delayed by cloud storage. Keep it off the UI
        // thread and allow at most one scan, even if the menu is reopened rapidly.
        internal void RefreshMediaState(Func<bool> captures, Func<bool> recordings)
        {
            if (mediaStatePending || IsDisposed) return;
            mediaStatePending = true;
            CapturesClearable = RecordingsClearable = false;
            Invalidate();
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool hasCaptures = false;
                bool hasRecordings = false;
                try { hasCaptures = captures(); hasRecordings = recordings(); }
                catch (Exception exception) { ErrorLog.Write("Could not refresh tray media state", exception); }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        mediaStatePending = false;
                        CapturesClearable = hasCaptures;
                        RecordingsClearable = hasRecordings;
                        Invalidate();
                    });
                }
                catch (InvalidOperationException) { } // The app may quit while storage is responding.
            });
        }

        internal void ShowAt(Point screenPoint)
        {
            ApplyDpi(NativeMethods.GetDpiForPoint(screenPoint));
            Rectangle working = Screen.FromPoint(screenPoint).WorkingArea;
            Location = new Point(
                Math.Max(working.Left, Math.Min(screenPoint.X - Width, working.Right - Width)),
                Math.Max(working.Top, Math.Min(screenPoint.Y - Height, working.Bottom - Height)));
            Show();
            Activate();
            items[0].Focus();
        }

        private int ScalePixel(int value) { return NativeMethods.ScaleForDpi(value, menuDpi); }

        private void ApplyDpi(uint dpi)
        {
            menuDpi = dpi == 0 ? 96 : dpi;
            ClientSize = new Size(ScalePixel(304), ScalePixel(326));
            for (int index = 0; index < items.Length; index++)
            {
                int top = 70 + index * 38 + (index >= 2 ? 10 : 0) + (index == 5 ? 10 : 0);
                items[index].Bounds = new Rectangle(ScalePixel(10), ScalePixel(top), ScalePixel(284), ScalePixel(36));
                items[index].CornerRadius = ScalePixel(7);
                items[index].ContentInset = ScalePixel(12);
            }
            using (GraphicsPath path = OpenCaptureTheme.CreateRoundedRectangle(ClientRectangle, ScalePixel(13)))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnDeactivate(EventArgs args) { base.OnDeactivate(args); Hide(); }

        protected override void OnPaint(PaintEventArgs args)
        {
            Graphics graphics = args.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color ink = SystemInformation.HighContrast ? SystemColors.MenuText : Color.FromArgb(27, 43, 61);
            Color muted = SystemInformation.HighContrast ? SystemColors.MenuText : Color.FromArgb(91, 108, 128);
            if (trayArtwork != null)
                graphics.DrawImage(trayArtwork, new Rectangle(ScalePixel(17), ScalePixel(17), ScalePixel(36), ScalePixel(36)));
            TextRenderer.DrawText(graphics, "OpenCapture", titleFont,
                new Rectangle(ScalePixel(65), ScalePixel(15), ScalePixel(220), ScalePixel(23)), ink,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(graphics, mediaStatePending ? "Checking saved files..." : "Shortcuts and preferences", detailFont,
                new Rectangle(ScalePixel(65), ScalePixel(39), ScalePixel(220), ScalePixel(21)), muted,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            using (Pen divider = new Pen(SystemInformation.HighContrast ? SystemColors.MenuText : Color.FromArgb(221, 230, 240)))
            {
                graphics.DrawLine(divider, ScalePixel(20), ScalePixel(150), Width - ScalePixel(20), ScalePixel(150));
                graphics.DrawLine(divider, ScalePixel(20), ScalePixel(274), Width - ScalePixel(20), ScalePixel(274));
            }
            Rectangle outline = ClientRectangle;
            outline.Width--; outline.Height--;
            using (GraphicsPath path = OpenCaptureTheme.CreateRoundedRectangle(outline, ScalePixel(13)))
            using (Pen border = new Pen(SystemInformation.HighContrast ? SystemColors.MenuText : Color.FromArgb(170, 195, 222)))
                graphics.DrawPath(border, path);
        }

        private void ActivateItem(int index)
        {
            if (!items[index].Enabled) return;
            Hide();
            EventHandler handler = index == 0 ? EditKeybindsRequested : index == 1 ? AutostartRequested :
                index == 2 ? ClearCapturesRequested : index == 3 ? ClearRecordingsRequested :
                index == 4 ? ClearClipboardRequested : QuitRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape) { Hide(); return true; }
            if (keyData == Keys.Enter || keyData == Keys.Space)
            {
                foreach (MenuButton item in items)
                    if (item.Focused && item.Enabled) { item.PerformClick(); return true; }
            }
            if (keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Home || keyData == Keys.End)
            {
                int direction = keyData == Keys.Up || keyData == Keys.End ? -1 : 1;
                int selected = Array.FindIndex(items, delegate(MenuButton item) { return item.Focused; });
                if (keyData == Keys.Home) selected = -1;
                if (keyData == Keys.End) selected = items.Length;
                for (int attempt = 0; attempt < items.Length; attempt++)
                {
                    selected = (selected + direction + items.Length) % items.Length;
                    if (items[selected].Enabled) { items[selected].Focus(); break; }
                }
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                menuFont.Dispose(); titleFont.Dispose(); detailFont.Dispose();
                if (trayArtwork != null) trayArtwork.Dispose();
            }
        }

        private sealed class MenuButton : Button
        {
            internal bool Danger;
            internal string Detail;
            internal int CornerRadius = 7;
            internal int ContentInset = 12;
            private bool hovered;
            private bool pressed;

            internal MenuButton()
            {
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }
            protected override void OnMouseEnter(EventArgs args) { base.OnMouseEnter(args); hovered = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs args) { base.OnMouseLeave(args); hovered = pressed = false; Invalidate(); }
            protected override void OnMouseDown(MouseEventArgs args) { base.OnMouseDown(args); pressed = args.Button == MouseButtons.Left; Invalidate(); }
            protected override void OnMouseUp(MouseEventArgs args) { pressed = false; base.OnMouseUp(args); Invalidate(); }
            protected override void OnGotFocus(EventArgs args) { base.OnGotFocus(args); Invalidate(); }
            protected override void OnLostFocus(EventArgs args) { base.OnLostFocus(args); Invalidate(); }
            protected override void OnPaint(PaintEventArgs args)
            {
                bool active = Enabled && (hovered || Focused);
                bool contrast = SystemInformation.HighContrast;
                Color ink = !Enabled ? (contrast ? SystemColors.GrayText : Color.FromArgb(137, 148, 161)) :
                    contrast ? (active ? SystemColors.HighlightText : SystemColors.MenuText) :
                    Danger ? Color.FromArgb(173, 47, 57) : Color.FromArgb(27, 43, 61);
                if (active)
                {
                    args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    Rectangle area = ClientRectangle; area.Width--; area.Height--;
                    using (GraphicsPath path = OpenCaptureTheme.CreateRoundedRectangle(area, CornerRadius))
                    using (SolidBrush fill = new SolidBrush(contrast ? SystemColors.Highlight :
                        pressed ? Color.FromArgb(213, 231, 250) : Color.FromArgb(231, 241, 253)))
                        args.Graphics.FillPath(fill, path);
                }
                Rectangle text = ClientRectangle;
                text.Inflate(-ContentInset, 0);
                if (!string.IsNullOrEmpty(Detail)) text.Width -= ContentInset * 3;
                TextRenderer.DrawText(args.Graphics, Text, Font, text, ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                if (!string.IsNullOrEmpty(Detail))
                    TextRenderer.DrawText(args.Graphics, Detail, Font,
                        new Rectangle(Width - ContentInset * 4, 0, ContentInset * 3, Height), ink,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                if (Focused && ShowFocusCues)
                {
                    Rectangle focus = ClientRectangle; focus.Inflate(-3, -3);
                    ControlPaint.DrawFocusRectangle(args.Graphics, focus, ink, BackColor);
                }
            }
        }
    }
}
