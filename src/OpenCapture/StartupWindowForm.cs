using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace OpenCapture
{
    internal sealed class StartupWindowForm : Form
    {
        private const int SourceWidth = 1063;
        private const int SourceHeight = 844;
        private const int DismissHoldMilliseconds = 1200;

        private readonly Func<ShortcutSettings, string> applySettings;
        private readonly Bitmap background;
        private readonly Font bindingFont;
        private readonly Font listeningFont;
        private readonly Font actionFont;
        private readonly ComboBox recordingAvailability;
        private readonly System.Windows.Forms.Timer dismissTimer;
        private readonly Stopwatch dismissStopwatch;
        private ShortcutSettings settings;
        private int editingIndex;
        private int hoveredIndex;
        private bool resetHovered;
        private bool dismissPressed;
        private float dismissProgress;
        private bool updatingAvailability;

        internal StartupWindowForm(
            ShortcutSettings currentSettings,
            Func<ShortcutSettings, string> settingsApplier)
        {
            if (currentSettings == null)
            {
                throw new ArgumentNullException("currentSettings");
            }
            if (settingsApplier == null)
            {
                throw new ArgumentNullException("settingsApplier");
            }

            settings = currentSettings;
            applySettings = settingsApplier;
            background = OpenCaptureTheme.LoadScaledBitmap(
                OpenCaptureTheme.StartupImageResourceName,
                new Size(795, 633));
            bindingFont = OpenCaptureTheme.CreateFont(20.0f, FontStyle.Bold);
            listeningFont = OpenCaptureTheme.CreateFont(13.0f, FontStyle.Regular);
            actionFont = OpenCaptureTheme.CreateFont(17.0f, FontStyle.Bold);
            editingIndex = -1;
            hoveredIndex = -1;

            dismissStopwatch = new Stopwatch();
            dismissTimer = new System.Windows.Forms.Timer();
            dismissTimer.Interval = 16;
            dismissTimer.Tick += UpdateDismissProgress;

            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            ClientSize = new Size(795, 633);
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            KeyPreview = true;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "OpenCaptureStartupWindow";
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Text = "OpenCapture shortcuts";
            TopMost = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.Selectable,
                true);

            recordingAvailability = new ComboBox();
            recordingAvailability.DropDownStyle = ComboBoxStyle.DropDownList;
            recordingAvailability.FlatStyle = FlatStyle.Flat;
            recordingAvailability.DrawMode = DrawMode.OwnerDrawFixed;
            recordingAvailability.ItemHeight = 25;
            recordingAvailability.Font = OpenCaptureTheme.CreateFont(13.0f, FontStyle.Bold);
            recordingAvailability.BackColor = Color.White;
            recordingAvailability.ForeColor = Color.FromArgb(18, 22, 27);
            recordingAvailability.IntegralHeight = true;
            recordingAvailability.TabStop = false;
            Controls.Add(recordingAvailability);
            UpdateAvailabilityControl();
            recordingAvailability.SelectedIndexChanged += RecordingAvailabilityChanged;
            recordingAvailability.DrawItem += DrawAvailabilityItem;
        }

        internal ShortcutSettings Settings
        {
            get { return settings; }
        }

        protected override void OnShown(EventArgs eventArgs)
        {
            base.OnShown(eventArgs);
            Activate();
            Focus();
        }

        protected override void OnResize(EventArgs eventArgs)
        {
            base.OnResize(eventArgs);
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                return;
            }

            using (GraphicsPath path = OpenCaptureTheme.CreateRoundedRectangle(ClientRectangle, 22))
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
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (background != null)
            {
                graphics.DrawImage(background, ClientRectangle);
            }
            else
            {
                using (LinearGradientBrush fallback = new LinearGradientBrush(
                    ClientRectangle,
                    Color.FromArgb(114, 167, 247),
                    OpenCaptureTheme.Accent,
                    LinearGradientMode.Horizontal))
                {
                    graphics.FillRectangle(fallback, ClientRectangle);
                }
                using (SolidBrush lower = new SolidBrush(Color.White))
                {
                    graphics.FillRectangle(
                        lower,
                        0,
                        ClientSize.Height * 54 / 100,
                        ClientSize.Width,
                        ClientSize.Height * 46 / 100);
                }
            }

            // The original artwork contains the old chevrons and shortcut text.
            // Replace only its lower settings area with a clean aligned layout.
            using (SolidBrush settingsBackground = new SolidBrush(Color.White))
            {
                graphics.FillRectangle(
                    settingsBackground,
                    0,
                    330,
                    ClientSize.Width,
                    ClientSize.Height - 330);
            }

            DrawBinding(graphics, 0, settings.CaptureShortcut);
            DrawBinding(graphics, 1, settings.CopyColourShortcut);
            DrawBinding(graphics, 2, settings.CaptureZoneShortcut);
            DrawBinding(graphics, 3, settings.RecordingShortcut);
            DrawBinding(graphics, 4, settings.RecordingControlShortcut);
            DrawAvailabilityShell(graphics);
            DrawActionLabel(graphics, 0, "Start Capture");
            DrawActionLabel(graphics, 1, "Copy HEX");
            DrawActionLabel(graphics, 2, "Capture Zone");
            DrawActionLabel(graphics, 3, "Recording Mode");
            DrawActionLabel(graphics, 4, "Start / Stop Recording");
            DrawActionLabel(graphics, 5, "Recording Button Works");
            DrawResetButton(graphics);
            DrawDismissProgress(graphics);

            using (Pen border = new Pen(Color.FromArgb(100, OpenCaptureTheme.AccentDeep), 1.0f))
            {
                Rectangle outline = ClientRectangle;
                outline.Width--;
                outline.Height--;
                using (GraphicsPath path = OpenCaptureTheme.CreateRoundedRectangle(outline, 22))
                {
                    graphics.DrawPath(border, path);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs eventArgs)
        {
            base.OnMouseMove(eventArgs);
            int hovered = HitBinding(eventArgs.Location);
            bool overReset = GetResetBounds().Contains(eventArgs.Location);
            if (hovered != hoveredIndex)
            {
                hoveredIndex = hovered;
                Invalidate();
            }
            if (overReset != resetHovered)
            {
                resetHovered = overReset;
                Invalidate(GetResetBounds());
            }

            Cursor = GetDismissBounds().Contains(eventArgs.Location) || hovered >= 0 || overReset
                ? Cursors.Hand
                : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            base.OnMouseLeave(eventArgs);
            if (hoveredIndex != -1)
            {
                hoveredIndex = -1;
                Invalidate();
            }
            if (resetHovered)
            {
                resetHovered = false;
                Invalidate(GetResetBounds());
            }
        }

        protected override void OnMouseDown(MouseEventArgs eventArgs)
        {
            base.OnMouseDown(eventArgs);
            if (eventArgs.Button == MouseButtons.Left && GetDismissBounds().Contains(eventArgs.Location))
            {
                dismissPressed = true;
                dismissProgress = 0.0f;
                dismissStopwatch.Restart();
                dismissTimer.Start();
                Capture = true;
                Invalidate(GetDismissBounds());
                return;
            }
            if (eventArgs.Button == MouseButtons.Left && GetResetBounds().Contains(eventArgs.Location))
            {
                ResetKeybinds();
                return;
            }

            int hit = HitBinding(eventArgs.Location);
            if (hit < 0)
            {
                editingIndex = -1;
                Invalidate();
                return;
            }

            if (editingIndex == hit && hit != 0)
            {
                ApplyBinding(hit, ShortcutBinding.Mouse(eventArgs.Button));
                return;
            }

            editingIndex = hit;
            Focus();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs eventArgs)
        {
            base.OnMouseUp(eventArgs);
            if (!dismissPressed || eventArgs.Button != MouseButtons.Left)
            {
                return;
            }

            bool releasedOnDismiss = GetDismissBounds().Contains(eventArgs.Location);
            StopDismissProgress();
            if (releasedOnDismiss)
            {
                Close();
            }
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (editingIndex < 0)
            {
                if ((keyData & Keys.KeyCode) == Keys.Escape)
                {
                    Close();
                    return true;
                }
                return base.ProcessCmdKey(ref message, keyData);
            }

            Keys keyCode = keyData & Keys.KeyCode;
            if (keyCode == Keys.Escape)
            {
                editingIndex = -1;
                Invalidate();
                return true;
            }
            if (keyCode == Keys.Back || keyCode == Keys.Delete)
            {
                ShortcutBinding reset = editingIndex == 0
                    ? ShortcutSettings.DefaultCapture
                    : editingIndex == 1
                        ? ShortcutSettings.DefaultCopyColour
                        : editingIndex == 2
                            ? ShortcutSettings.DefaultCaptureZone
                            : editingIndex == 3
                                ? ShortcutSettings.DefaultRecording
                                : ShortcutSettings.DefaultRecordingControl;
                ApplyBinding(editingIndex, reset);
                return true;
            }
            if (keyCode == Keys.ControlKey || keyCode == Keys.LControlKey ||
                keyCode == Keys.RControlKey || keyCode == Keys.ShiftKey ||
                keyCode == Keys.LShiftKey || keyCode == Keys.RShiftKey ||
                keyCode == Keys.Menu || keyCode == Keys.LMenu || keyCode == Keys.RMenu)
            {
                return true;
            }

            try
            {
                ApplyBinding(
                    editingIndex,
                    ShortcutBinding.Keyboard(keyData & Keys.Modifiers, keyCode));
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    exception.Message,
                    "Shortcut not changed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                dismissTimer.Stop();
                dismissTimer.Tick -= UpdateDismissProgress;
                dismissTimer.Dispose();
                dismissStopwatch.Stop();
                bindingFont.Dispose();
                listeningFont.Dispose();
                actionFont.Dispose();
                recordingAvailability.SelectedIndexChanged -= RecordingAvailabilityChanged;
                recordingAvailability.DrawItem -= DrawAvailabilityItem;
                recordingAvailability.Font.Dispose();
                recordingAvailability.Dispose();
                if (background != null)
                {
                    background.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private void ApplyBinding(int index, ShortcutBinding binding)
        {
            if ((index == 0 || index == 3 || index == 4) &&
                binding.Kind != ShortcutInputKind.Keyboard)
            {
                return;
            }

            ShortcutSettings candidate = settings.WithBinding(index, binding);
            if (candidate.CaptureShortcut.Equals(candidate.CopyColourShortcut) ||
                candidate.CaptureShortcut.Equals(candidate.CaptureZoneShortcut) ||
                candidate.CopyColourShortcut.Equals(candidate.CaptureZoneShortcut) ||
                candidate.RecordingShortcut.Equals(candidate.CopyColourShortcut) ||
                candidate.RecordingShortcut.Equals(candidate.CaptureZoneShortcut) ||
                candidate.RecordingControlShortcut.Equals(candidate.CaptureShortcut) ||
                candidate.RecordingControlShortcut.Equals(candidate.CopyColourShortcut) ||
                candidate.RecordingControlShortcut.Equals(candidate.CaptureZoneShortcut) ||
                candidate.RecordingControlShortcut.Equals(candidate.RecordingShortcut) ||
                (candidate.RecordingAvailability == RecordingShortcutAvailability.AlwaysAvailable &&
                    candidate.RecordingShortcut.Equals(candidate.CaptureShortcut)))
            {
                MessageBox.Show(
                    this,
                    "Each OpenCapture action needs a different shortcut.",
                    "Shortcut already in use",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            string error = applySettings(candidate);
            if (!string.IsNullOrEmpty(error))
            {
                MessageBox.Show(
                    this,
                    error,
                    "Shortcut not changed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            settings = candidate;
            editingIndex = -1;
            UpdateAvailabilityControl();
            Invalidate();
        }

        private void RecordingAvailabilityChanged(object sender, EventArgs eventArgs)
        {
            if (updatingAvailability || recordingAvailability.SelectedIndex < 0)
            {
                return;
            }

            RecordingShortcutAvailability availability = recordingAvailability.SelectedIndex == 0
                ? RecordingShortcutAvailability.AfterCaptureShortcut
                : RecordingShortcutAvailability.AlwaysAvailable;
            ShortcutSettings candidate = settings.WithRecordingAvailability(availability);
            if (availability == RecordingShortcutAvailability.AlwaysAvailable &&
                candidate.RecordingShortcut.Equals(candidate.CaptureShortcut))
            {
                MessageBox.Show(
                    this,
                    "Choose a different recording shortcut before making it always available.",
                    "Shortcut already in use",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                UpdateAvailabilityControl();
                return;
            }

            string error = applySettings(candidate);
            if (!string.IsNullOrEmpty(error))
            {
                MessageBox.Show(
                    this,
                    error,
                    "Recording availability not changed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                UpdateAvailabilityControl();
                return;
            }

            settings = candidate;
            Invalidate();
        }

        private void UpdateAvailabilityControl()
        {
            updatingAvailability = true;
            recordingAvailability.BeginUpdate();
            recordingAvailability.Items.Clear();
            recordingAvailability.Items.Add("After " + settings.CaptureShortcut.DisplayText);
            recordingAvailability.Items.Add("Always Available");
            recordingAvailability.SelectedIndex =
                settings.RecordingAvailability == RecordingShortcutAvailability.AfterCaptureShortcut ? 0 : 1;
            recordingAvailability.EndUpdate();
            Rectangle innerBounds = GetAvailabilityBounds();
            innerBounds.Inflate(-5, -5);
            recordingAvailability.Bounds = innerBounds;
            updatingAvailability = false;
        }

        private void DrawAvailabilityItem(object sender, DrawItemEventArgs eventArgs)
        {
            eventArgs.DrawBackground();
            string text = eventArgs.Index >= 0 && eventArgs.Index < recordingAvailability.Items.Count
                ? recordingAvailability.Items[eventArgs.Index].ToString()
                : recordingAvailability.Text;
            Color textColor = (eventArgs.State & DrawItemState.Disabled) != 0
                ? OpenCaptureTheme.Disabled
                : Color.FromArgb(18, 22, 27);
            TextRenderer.DrawText(
                eventArgs.Graphics,
                text,
                recordingAvailability.Font,
                eventArgs.Bounds,
                textColor,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.SingleLine);
            eventArgs.DrawFocusRectangle();
        }

        private void DrawBinding(Graphics graphics, int index, ShortcutBinding binding)
        {
            Rectangle bounds = GetBindingBounds(index);
            bool active = editingIndex == index;
            bool hovered = hoveredIndex == index;
            Color fill = active
                ? Color.FromArgb(255, 230, 246, 255)
                : hovered
                    ? Color.FromArgb(255, 248, 253, 255)
                    : Color.White;

            using (GraphicsPath path = OpenCaptureTheme.CreateRoundedRectangle(bounds, 9))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen border = new Pen(
                active || hovered ? OpenCaptureTheme.AccentDeep : Color.FromArgb(150, 20, 25, 32),
                active ? 2.0f : 1.0f))
            {
                graphics.FillPath(brush, path);
                graphics.DrawPath(border, path);
            }

            string text = active ? "Press shortcut…" : binding.DisplayText;
            TextRenderer.DrawText(
                graphics,
                text,
                active ? listeningFont : bindingFont,
                bounds,
                Color.FromArgb(18, 22, 27),
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis);
        }

        private void DrawActionLabel(Graphics graphics, int index, string text)
        {
            Rectangle control = index < 5
                ? GetBindingBounds(index)
                : GetAvailabilityBounds();
            Rectangle label = new Rectangle(
                286,
                control.Top,
                ClientSize.Width - 326,
                control.Height);
            TextRenderer.DrawText(
                graphics,
                text,
                actionFont,
                label,
                Color.FromArgb(18, 22, 27),
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
        }

        private void DrawAvailabilityShell(Graphics graphics)
        {
            Rectangle bounds = GetAvailabilityBounds();
            using (GraphicsPath path = OpenCaptureTheme.CreateRoundedRectangle(bounds, 9))
            using (SolidBrush fill = new SolidBrush(Color.White))
            using (Pen border = new Pen(Color.FromArgb(150, 20, 25, 32), 1.0f))
            {
                graphics.FillPath(fill, path);
                graphics.DrawPath(border, path);
            }
        }

        private void DrawResetButton(Graphics graphics)
        {
            Rectangle bounds = GetResetBounds();
            Color fill = resetHovered
                ? Color.FromArgb(232, OpenCaptureTheme.Accent)
                : Color.FromArgb(205, OpenCaptureTheme.Accent);
            using (GraphicsPath path = OpenCaptureTheme.CreateRoundedRectangle(bounds, 9))
            using (SolidBrush backgroundBrush = new SolidBrush(fill))
            using (Pen border = new Pen(OpenCaptureTheme.AccentDeep, 1.0f))
            {
                graphics.FillPath(backgroundBrush, path);
                graphics.DrawPath(border, path);
            }
            TextRenderer.DrawText(
                graphics,
                "Reset Keybinds",
                listeningFont,
                bounds,
                Color.FromArgb(18, 22, 27),
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
        }

        private void ResetKeybinds()
        {
            ShortcutSettings candidate = ShortcutSettings.CreateDefaults();
            string error = applySettings(candidate);
            if (!string.IsNullOrEmpty(error))
            {
                MessageBox.Show(
                    this,
                    error,
                    "Keybinds were not reset",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            settings = candidate;
            editingIndex = -1;
            UpdateAvailabilityControl();
            Invalidate();
        }

        private void DrawDismissProgress(Graphics graphics)
        {
            if (dismissProgress <= 0.0f)
            {
                return;
            }

            Rectangle bounds = GetDismissBounds();
            bounds.Inflate(-5, -5);
            using (Pen progress = new Pen(OpenCaptureTheme.AccentDeep, 8.0f))
            {
                progress.StartCap = LineCap.Round;
                progress.EndCap = LineCap.Round;
                graphics.DrawArc(
                    progress,
                    bounds,
                    -90.0f,
                    Math.Min(360.0f, dismissProgress * 360.0f));
            }
        }

        private void UpdateDismissProgress(object sender, EventArgs eventArgs)
        {
            if (!dismissPressed)
            {
                StopDismissProgress();
                return;
            }

            Point clientPointer = PointToClient(Cursor.Position);
            if (!GetDismissBounds().Contains(clientPointer))
            {
                StopDismissProgress();
                return;
            }

            dismissProgress = Math.Min(
                1.0f,
                (float)dismissStopwatch.Elapsed.TotalMilliseconds / DismissHoldMilliseconds);
            Invalidate(GetDismissBounds());
            if (dismissProgress < 1.0f)
            {
                return;
            }

            try
            {
                ShortcutSettings.StartupWindowDismissedForever = true;
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    exception.Message,
                    "Startup preference was not saved",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            StopDismissProgress();
            Close();
        }

        private void StopDismissProgress()
        {
            dismissTimer.Stop();
            dismissStopwatch.Reset();
            dismissPressed = false;
            dismissProgress = 0.0f;
            Capture = false;
            Invalidate(GetDismissBounds());
        }

        private int HitBinding(Point point)
        {
            int index;
            for (index = 0; index < 5; index++)
            {
                if (GetBindingBounds(index).Contains(point))
                {
                    return index;
                }
            }
            return -1;
        }

        private Rectangle GetBindingBounds(int index)
        {
            return new Rectangle(72, 342 + index * 39, 190, 34);
        }

        private Rectangle GetAvailabilityBounds()
        {
            return new Rectangle(72, 537, 190, 34);
        }

        private Rectangle GetResetBounds()
        {
            return new Rectangle(72, 581, 190, 35);
        }

        private Rectangle GetDismissBounds()
        {
            return ScaleRectangle(new Rectangle(943, 26, 94, 94));
        }

        private Rectangle ScaleRectangle(Rectangle source)
        {
            return Rectangle.FromLTRB(
                source.Left * ClientSize.Width / SourceWidth,
                source.Top * ClientSize.Height / SourceHeight,
                source.Right * ClientSize.Width / SourceWidth,
                source.Bottom * ClientSize.Height / SourceHeight);
        }
    }
}
