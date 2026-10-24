using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace OpenCapture
{
    // Only the original captured pixels, outline and resize tabs are shown.
    // There is no editor window, header, toolbar, resampling or modal dialog.
    internal sealed class CaptureCropForm : Form
    {
        internal const int MarginPixels = 8;
        private readonly Bitmap image;
        private int dragging;
        private Point anchor;
        private Rectangle startingSelection;
        internal Rectangle Selection { get; private set; }
        internal event EventHandler SelectionChanged;
        internal event EventHandler CropFinished;
        internal event EventHandler CropCancelled;

        internal CaptureCropForm(Bitmap source, Rectangle desktopBounds, Rectangle initial)
        {
            image = source;
            Selection = initial;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Text = "OpenCapture crop handles";
            AccessibleName = Text;
            BackColor = Color.White;
            DoubleBuffered = true;
            Bounds = new Rectangle(desktopBounds.Left - MarginPixels, desktopBounds.Top - MarginPixels,
                source.Width + MarginPixels * 2, source.Height + MarginPixels * 2);
            UpdateShape();
        }

        internal CaptureCropForm(Bitmap source)
            : this(source, new Rectangle(Point.Empty, source.Size), new Rectangle(Point.Empty, source.Size)) { }

        private Rectangle DisplayedSelection
        { get { Rectangle value = Selection; value.Offset(MarginPixels, MarginPixels); return value; } }

        private void UpdateShape()
        {
            Rectangle visible = DisplayedSelection;
            Region shape = new Region(visible);
            foreach (Point handle in HandlePoints(visible))
                shape.Union(new Rectangle(handle.X - MarginPixels, handle.Y - MarginPixels, MarginPixels * 2 + 1, MarginPixels * 2 + 1));
            Region previous = Region;
            Region = shape;
            if (previous != null) previous.Dispose();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            Rectangle visible = DisplayedSelection;
            args.Graphics.DrawImage(image, visible, Selection, GraphicsUnit.Pixel);
            using (Pen edge = new Pen(OpenCaptureTheme.AccentDeep, 1.0f))
            {
                args.Graphics.DrawRectangle(edge, visible);
                foreach (Point handle in HandlePoints(visible))
                {
                    Rectangle tab = new Rectangle(handle.X - 4, handle.Y - 4, 8, 8);
                    args.Graphics.FillRectangle(Brushes.White, tab);
                    args.Graphics.DrawRectangle(edge, tab);
                }
            }
        }

        internal static Point[] HandlePoints(Rectangle bounds)
        {
            int x = bounds.Left + bounds.Width / 2;
            int y = bounds.Top + bounds.Height / 2;
            return new Point[] {
                new Point(bounds.Left, bounds.Top), new Point(x, bounds.Top), new Point(bounds.Right, bounds.Top),
                new Point(bounds.Right, y), new Point(bounds.Right, bounds.Bottom), new Point(x, bounds.Bottom),
                new Point(bounds.Left, bounds.Bottom), new Point(bounds.Left, y)
            };
        }

        private int HitHandle(Point point)
        {
            Point[] positions = HandlePoints(DisplayedSelection);
            int[] sides = new int[] { 1 | 4, 4, 2 | 4, 2, 2 | 8, 8, 1 | 8, 1 };
            for (int i = 0; i < positions.Length; i++)
                if (Math.Abs(point.X - positions[i].X) <= 8 && Math.Abs(point.Y - positions[i].Y) <= 8)
                    return sides[i];
            return DisplayedSelection.Contains(point) ? 16 : 0;
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            if (args.Button != MouseButtons.Left) return;
            dragging = HitHandle(args.Location);
            if (dragging == 0) return;
            anchor = args.Location;
            startingSelection = Selection;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            int handle = dragging == 0 ? HitHandle(args.Location) : dragging;
            Cursor = handle == 16 ? Cursors.SizeAll :
                handle == 1 || handle == 2 ? Cursors.SizeWE :
                handle == 4 || handle == 8 ? Cursors.SizeNS :
                handle == 5 || handle == 10 ? Cursors.SizeNWSE :
                handle == 6 || handle == 9 ? Cursors.SizeNESW : Cursors.Default;
            if (dragging == 0) return;
            Selection = ResizeSelection(startingSelection, dragging, args.X - anchor.X, args.Y - anchor.Y, image.Size);
            UpdateShape();
            Invalidate();
            EventHandler changed = SelectionChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            if (args.Button != MouseButtons.Left) return;
            OnMouseMove(args);
            dragging = 0;
            Capture = false;
        }

        internal static Rectangle ResizeSelection(Rectangle start, int sides, int dx, int dy, Size bounds)
        {
            if (sides == 16)
                return new Rectangle(Math.Max(0, Math.Min(bounds.Width - start.Width, start.X + dx)),
                    Math.Max(0, Math.Min(bounds.Height - start.Height, start.Y + dy)), start.Width, start.Height);
            int left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
            if ((sides & 1) != 0) left = Math.Max(0, Math.Min(right - 1, left + dx));
            if ((sides & 2) != 0) right = Math.Max(left + 1, Math.Min(bounds.Width, right + dx));
            if ((sides & 4) != 0) top = Math.Max(0, Math.Min(bottom - 1, top + dy));
            if ((sides & 8) != 0) bottom = Math.Max(top + 1, Math.Min(bounds.Height, bottom + dy));
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == Keys.Enter || keys == Keys.Escape)
            {
                EventHandler handler = keys == Keys.Enter ? CropFinished : CropCancelled;
                if (handler != null) handler(this, EventArgs.Empty);
                return true;
            }
            return base.ProcessCmdKey(ref message, keys);
        }

        internal static Rectangle FitImage(Size size, Rectangle available)
        {
            double scale = Math.Min(available.Width / (double)size.Width, available.Height / (double)size.Height);
            int width = Math.Max(1, (int)Math.Round(size.Width * scale));
            int height = Math.Max(1, (int)Math.Round(size.Height * scale));
            return new Rectangle(available.Left + (available.Width - width) / 2, available.Top + (available.Height - height) / 2, width, height);
        }

        internal static Point ToImagePoint(Point point, Rectangle displayed, Size source)
        {
            return new Point(
                Math.Max(0, Math.Min(source.Width - 1, (int)Math.Floor((point.X - displayed.Left) * source.Width / (double)displayed.Width))),
                Math.Max(0, Math.Min(source.Height - 1, (int)Math.Floor((point.Y - displayed.Top) * source.Height / (double)displayed.Height))));
        }
    }
}
