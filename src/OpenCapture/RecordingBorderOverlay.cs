using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenCapture
{
    internal sealed class RecordingBorderOverlay : IDisposable
    {
        private const int Thickness = 3;
        private readonly List<RecordingBorderPiece> pieces;
        private readonly RecordingStatusForm status;

        internal RecordingBorderOverlay(Rectangle selection)
            : this(selection, ShortcutSettings.DefaultRecordingControl.DisplayText)
        {
        }

        internal RecordingBorderOverlay(Rectangle selection, string recordingControlText)
        {
            pieces = new List<RecordingBorderPiece>(4)
            {
                new RecordingBorderPiece(new Rectangle(selection.Left, selection.Top - Thickness, selection.Width, Thickness)),
                new RecordingBorderPiece(new Rectangle(selection.Left, selection.Bottom, selection.Width, Thickness)),
                new RecordingBorderPiece(new Rectangle(selection.Left - Thickness, selection.Top, Thickness, selection.Height)),
                new RecordingBorderPiece(new Rectangle(selection.Right, selection.Top, Thickness, selection.Height))
            };
            status = new RecordingStatusForm(selection, recordingControlText);
        }

        internal void Show()
        {
            foreach (RecordingBorderPiece piece in pieces)
            {
                piece.Show();
            }
            status.Show();
        }

        internal void UpdateStorage(long currentBytes)
        {
            status.UpdateStorage(currentBytes);
        }

        internal IList<Rectangle> PieceBounds
        {
            get
            {
                List<Rectangle> bounds = new List<Rectangle>(pieces.Count);
                foreach (RecordingBorderPiece piece in pieces)
                {
                    bounds.Add(piece.Bounds);
                }
                return bounds;
            }
        }

        internal Rectangle StatusBounds
        {
            get { return status.Bounds; }
        }

        internal bool IsStatusExcludedFromCapture
        {
            get { return status.IsExcludedFromCapture; }
        }

        internal string StatusInstruction
        {
            get { return status.InstructionText; }
        }

        public void Dispose()
        {
            if (!status.IsDisposed)
            {
                status.Close();
                status.Dispose();
            }
            foreach (RecordingBorderPiece piece in pieces)
            {
                if (!piece.IsDisposed)
                {
                    piece.Close();
                    piece.Dispose();
                }
            }
            pieces.Clear();
        }

        private sealed class RecordingBorderPiece : Form
        {
            private const uint SWP_NOACTIVATE = 0x0010;
            private const uint SWP_SHOWWINDOW = 0x0040;
            private static readonly IntPtr HwndTopmost = new IntPtr(-1);
            private readonly Rectangle targetBounds;

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetWindowPos(
                IntPtr window,
                IntPtr insertAfter,
                int x,
                int y,
                int width,
                int height,
                uint flags);

            internal RecordingBorderPiece(Rectangle bounds)
            {
                targetBounds = bounds;
                AutoScaleMode = AutoScaleMode.None;
                BackColor = Color.FromArgb(238, 42, 52);
                FormBorderStyle = FormBorderStyle.None;
                MinimumSize = Size.Empty;
                MaximumSize = Size.Empty;
                ShowIcon = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                Bounds = bounds;
            }

            protected override void OnShown(EventArgs eventArgs)
            {
                base.OnShown(eventArgs);
                SetWindowPos(
                    Handle,
                    HwndTopmost,
                    targetBounds.Left,
                    targetBounds.Top,
                    targetBounds.Width,
                    targetBounds.Height,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }

            protected override bool ShowWithoutActivation
            {
                get { return true; }
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
                    parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                    parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                    return parameters;
                }
            }
        }

        private sealed class RecordingStatusForm : Form
        {
            private const int MinimumPanelWidth = 250;
            private const int PanelHeight = 58;
            private const int PanelGap = 8;
            private const uint SWP_NOACTIVATE = 0x0010;
            private const uint SWP_SHOWWINDOW = 0x0040;
            private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
            private static readonly IntPtr HwndTopmost = new IntPtr(-1);

            private readonly Rectangle targetBounds;
            private readonly Font rateFont;
            private readonly Font usedFont;
            private readonly string rateText;
            private readonly string recordingControlText;
            private readonly int recordingControlWidth;
            private long currentBytes;

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetWindowPos(
                IntPtr window,
                IntPtr insertAfter,
                int x,
                int y,
                int width,
                int height,
                uint flags);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetWindowDisplayAffinity(
                IntPtr window,
                out uint affinity);

            internal RecordingStatusForm(Rectangle selection, string controlText)
            {
                rateFont = OpenCaptureTheme.CreateFont(9.5f, FontStyle.Bold);
                usedFont = OpenCaptureTheme.CreateFont(9.0f, FontStyle.Regular);
                recordingControlText = string.IsNullOrWhiteSpace(controlText)
                    ? ShortcutSettings.DefaultRecordingControl.DisplayText
                    : controlText;
                recordingControlWidth = Math.Max(
                    48,
                    Math.Min(
                        130,
                        TextRenderer.MeasureText(
                            recordingControlText,
                            usedFont,
                            Size.Empty,
                            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 12));
                int panelWidth = Math.Max(MinimumPanelWidth, 188 + recordingControlWidth);
                targetBounds = ChooseBounds(selection, panelWidth);
                rateText = "≈ " + OverlayForm.FormatEstimatedStorage(
                    OverlayForm.EstimateRecordingStorageBytesPerMinute(
                        selection.Width,
                        selection.Height)) + "/min";

                AutoScaleMode = AutoScaleMode.None;
                BackColor = Color.FromArgb(246, 249, 252);
                FormBorderStyle = FormBorderStyle.None;
                MinimumSize = Size.Empty;
                MaximumSize = Size.Empty;
                Opacity = 0.94;
                ShowIcon = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                Bounds = targetBounds;
                AccessibleName = "OpenCapture recording storage";
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.UserPaint |
                    ControlStyles.OptimizedDoubleBuffer,
                    true);
            }

            internal void UpdateStorage(long bytes)
            {
                currentBytes = Math.Max(0L, bytes);
                Invalidate();
            }

            internal bool IsExcludedFromCapture
            {
                get
                {
                    uint affinity;
                    return IsHandleCreated &&
                        GetWindowDisplayAffinity(Handle, out affinity) &&
                        affinity == WDA_EXCLUDEFROMCAPTURE;
                }
            }

            internal string InstructionText
            {
                get { return recordingControlText + " to stop"; }
            }

            protected override void OnShown(EventArgs eventArgs)
            {
                base.OnShown(eventArgs);
                SetWindowPos(
                    Handle,
                    HwndTopmost,
                    targetBounds.Left,
                    targetBounds.Top,
                    targetBounds.Width,
                    targetBounds.Height,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
                SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
                ApplyRoundedRegion();
            }

            protected override void OnPaint(PaintEventArgs eventArgs)
            {
                Graphics graphics = eventArgs.Graphics;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (SolidBrush background = new SolidBrush(Color.FromArgb(246, 249, 252)))
                using (Pen accent = new Pen(Color.FromArgb(238, 42, 52), 1.5f))
                using (SolidBrush rateBrush = new SolidBrush(Color.FromArgb(31, 38, 46)))
                using (SolidBrush usedBrush = new SolidBrush(Color.FromArgb(65, 72, 81)))
                using (SolidBrush keyBackground = new SolidBrush(Color.White))
                using (Pen keyBorder = new Pen(Color.FromArgb(220, 238, 42, 52), 1.0f))
                using (SolidBrush stopBrush = new SolidBrush(Color.FromArgb(238, 42, 52)))
                using (StringFormat keyFormat = new StringFormat(
                    StringFormat.GenericTypographic))
                using (GraphicsPath path = OpenCaptureTheme.CreateRoundedRectangle(
                    new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1),
                    10))
                {
                    graphics.FillPath(background, path);
                    graphics.DrawPath(accent, path);
                    graphics.DrawString(rateText, rateFont, rateBrush, 12.0f, 8.0f);
                    graphics.DrawString(
                        OverlayForm.FormatCurrentStorage(currentBytes) + " used",
                        usedFont,
                        usedBrush,
                        12.0f,
                        30.0f);
                    Rectangle key = new Rectangle(126, 28, recordingControlWidth, 21);
                    using (GraphicsPath keyPath =
                        OpenCaptureTheme.CreateRoundedRectangle(key, 5))
                    {
                        graphics.FillPath(keyBackground, keyPath);
                        graphics.DrawPath(keyBorder, keyPath);
                    }
                    keyFormat.Alignment = StringAlignment.Center;
                    keyFormat.LineAlignment = StringAlignment.Center;
                    keyFormat.FormatFlags = StringFormatFlags.NoWrap;
                    graphics.DrawString(
                        recordingControlText,
                        usedFont,
                        rateBrush,
                        new RectangleF(key.X, key.Y, key.Width, key.Height),
                        keyFormat);
                    int stopLeft = key.Right + 9;
                    graphics.FillRectangle(stopBrush, stopLeft, 35, 7, 7);
                    graphics.DrawString("Stop", usedFont, usedBrush, stopLeft + 13.0f, 30.0f);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    rateFont.Dispose();
                    usedFont.Dispose();
                }
                base.Dispose(disposing);
            }

            protected override bool ShowWithoutActivation
            {
                get { return true; }
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
                    parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                    parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                    return parameters;
                }
            }

            private void ApplyRoundedRegion()
            {
                using (GraphicsPath path = OpenCaptureTheme.CreateRoundedRectangle(
                    new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
                    10))
                {
                    Region = new Region(path);
                }
            }

            private static Rectangle ChooseBounds(Rectangle selection, int panelWidth)
            {
                Rectangle monitor = Screen.FromRectangle(selection).Bounds;
                int x = Clamp(selection.Left, monitor.Left, monitor.Right - panelWidth);
                if (selection.Top - PanelGap - PanelHeight >= monitor.Top)
                {
                    return new Rectangle(
                        x,
                        selection.Top - PanelGap - PanelHeight,
                        panelWidth,
                        PanelHeight);
                }
                if (selection.Bottom + PanelGap + PanelHeight <= monitor.Bottom)
                {
                    return new Rectangle(
                        x,
                        selection.Bottom + PanelGap,
                        panelWidth,
                        PanelHeight);
                }

                int y = Clamp(selection.Top, monitor.Top, monitor.Bottom - PanelHeight);
                if (selection.Right + PanelGap + panelWidth <= monitor.Right)
                {
                    return new Rectangle(
                        selection.Right + PanelGap,
                        y,
                        panelWidth,
                        PanelHeight);
                }
                if (selection.Left - PanelGap - panelWidth >= monitor.Left)
                {
                    return new Rectangle(
                        selection.Left - PanelGap - panelWidth,
                        y,
                        panelWidth,
                        PanelHeight);
                }

                return new Rectangle(
                    Clamp(selection.Left + PanelGap, monitor.Left, monitor.Right - panelWidth),
                    Clamp(selection.Top + PanelGap, monitor.Top, monitor.Bottom - PanelHeight),
                    panelWidth,
                    PanelHeight);
            }

            private static int Clamp(int value, int minimum, int maximum)
            {
                if (maximum < minimum)
                {
                    return minimum;
                }
                return Math.Max(minimum, Math.Min(maximum, value));
            }
        }
    }
}
