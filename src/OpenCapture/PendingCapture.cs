using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace OpenCapture
{
    internal enum CaptureKind { Screenshot, Colour, Text, Recording }

    internal sealed class PendingCapture : IDisposable
    {
        internal readonly CaptureKind Kind;
        internal readonly DateTime CapturedAt;
        internal readonly Bitmap Image;
        internal string Text;
        internal string VideoPath;
        internal Rectangle DesktopBounds;
        private bool disposed;
        internal Rectangle Crop { get; private set; }
        internal bool HasCrop { get { return Image != null && Crop != new Rectangle(Point.Empty, Image.Size); } }
        internal bool CanCrop { get { return Image != null && Image.Width > 1 && Image.Height > 1; } }

        internal PendingCapture(CaptureKind kind, Bitmap image, string text, DateTime capturedAt, string videoPath)
        {
            Kind = kind;
            Image = image;
            Text = text;
            CapturedAt = capturedAt;
            VideoPath = videoPath;
            Crop = image == null ? Rectangle.Empty : new Rectangle(Point.Empty, image.Size);
            DesktopBounds = Crop;
        }

        internal Bitmap RenderImage()
        {
            return Image == null ? null : Image.Clone(Crop, PixelFormat.Format32bppRgb);
        }

        internal void ApplyCrop(Rectangle relativeCrop)
        {
            Rectangle next = Rectangle.Intersect(new Rectangle(Point.Empty, Crop.Size), relativeCrop);
            if (next.Width <= 0 || next.Height <= 0) throw new ArgumentOutOfRangeException("relativeCrop");
            Crop = new Rectangle(Crop.X + next.X, Crop.Y + next.Y, next.Width, next.Height);
            if (Kind == CaptureKind.Text) Text = null;
        }

        internal void SetCrop(Rectangle absoluteCrop)
        {
            Rectangle next = Rectangle.Intersect(new Rectangle(Point.Empty, Image.Size), absoluteCrop);
            if (next.Width <= 0 || next.Height <= 0) throw new ArgumentOutOfRangeException("absoluteCrop");
            if (next == Crop) return;
            Crop = next;
            if (Kind == CaptureKind.Text) Text = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (Image != null) Image.Dispose();
            // Only the private unfinished recording belongs to this transaction.
            // Saved/renamed user recordings are never removed by review dismissal.
            if (!string.IsNullOrEmpty(VideoPath))
            {
                try { File.Delete(VideoPath); }
                catch (Exception exception) { ErrorLog.Write("Could not remove discarded recording", exception); }
                VideoPath = null;
            }
        }
    }
}
