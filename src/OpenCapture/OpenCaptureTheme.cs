using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OpenCapture
{
    internal static class OpenCaptureTheme
    {
        internal const string FontResourceName = "OpenCapture.Resources.ProximaNovaA.otf";
        internal const string StartupImageResourceName = "OpenCapture.Resources.StartupWindow.png";
        internal const string TrayIconResourceName = "OpenCapture.Resources.TrayIcon.png";

        internal static readonly Color Accent = Color.FromArgb(92, 179, 255);
        internal static readonly Color AccentLight = Color.FromArgb(132, 196, 255);
        internal static readonly Color AccentDeep = Color.FromArgb(68, 143, 239);
        internal static readonly Color Danger = Color.FromArgb(255, 38, 45);
        internal static readonly Color Enabled = Color.FromArgb(0, 242, 142);
        internal static readonly Color Disabled = Color.FromArgb(86, 88, 92);

        private static readonly object SyncRoot = new object();
        private static PrivateFontCollection privateFonts;
        private static byte[] fontBytes;
        private static GCHandle pinnedFont;
        private static bool fontLoadAttempted;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        internal static Font CreateFont(float size, FontStyle style)
        {
            EnsureFontLoaded();
            try
            {
                if (privateFonts != null && privateFonts.Families.Length > 0)
                {
                    FontFamily family = privateFonts.Families[0];
                    FontStyle supported = family.IsStyleAvailable(style)
                        ? style
                        : FontStyle.Regular;
                    return new Font(family, size, supported, GraphicsUnit.Point);
                }
            }
            catch
            {
            }

            return new Font("Segoe UI", size, style, GraphicsUnit.Point);
        }

        internal static Bitmap LoadBitmap(string resourceName)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    return null;
                }

                using (Image image = Image.FromStream(stream, true, true))
                {
                    return new Bitmap(image);
                }
            }
        }

        internal static Bitmap LoadScaledBitmap(string resourceName, Size size)
        {
            if (size.Width <= 0 || size.Height <= 0)
            {
                throw new ArgumentOutOfRangeException("size");
            }

            using (Bitmap source = LoadBitmap(resourceName))
            {
                if (source == null)
                {
                    return null;
                }

                Bitmap scaled = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
                using (Graphics graphics = Graphics.FromImage(scaled))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.DrawImage(source, new Rectangle(Point.Empty, size));
                }
                return scaled;
            }
        }

        internal static Icon CreateTrayIcon()
        {
            using (Bitmap source = LoadScaledBitmap(TrayIconResourceName, new Size(32, 32)))
            using (Bitmap iconBitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(iconBitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;

                if (source != null)
                {
                    graphics.DrawImage(source, new Rectangle(0, 0, 32, 32));
                }
                else
                {
                    using (SolidBrush background = new SolidBrush(Accent))
                    using (Pen white = new Pen(Color.White, 2.0f))
                    {
                        graphics.FillEllipse(background, 1, 1, 30, 30);
                        graphics.DrawLine(white, 16, 5, 16, 27);
                        graphics.DrawLine(white, 5, 16, 27, 16);
                        graphics.DrawRectangle(white, 12, 12, 8, 8);
                    }
                }

                IntPtr handle = iconBitmap.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(handle).Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        internal static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
        {
            int safeRadius = Math.Max(1, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            int diameter = safeRadius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void EnsureFontLoaded()
        {
            lock (SyncRoot)
            {
                if (fontLoadAttempted)
                {
                    return;
                }
                fontLoadAttempted = true;

                try
                {
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(FontResourceName))
                    {
                        if (stream == null || stream.Length <= 0 || stream.Length > int.MaxValue)
                        {
                            return;
                        }

                        fontBytes = new byte[(int)stream.Length];
                        int offset = 0;
                        while (offset < fontBytes.Length)
                        {
                            int read = stream.Read(fontBytes, offset, fontBytes.Length - offset);
                            if (read <= 0)
                            {
                                throw new EndOfStreamException("The embedded OpenCapture font was incomplete.");
                            }
                            offset += read;
                        }
                    }

                    pinnedFont = GCHandle.Alloc(fontBytes, GCHandleType.Pinned);
                    privateFonts = new PrivateFontCollection();
                    privateFonts.AddMemoryFont(pinnedFont.AddrOfPinnedObject(), fontBytes.Length);
                    if (privateFonts.Families.Length == 0)
                    {
                        privateFonts.Dispose();
                        privateFonts = null;
                    }
                }
                catch
                {
                    if (privateFonts != null)
                    {
                        privateFonts.Dispose();
                        privateFonts = null;
                    }
                    if (pinnedFont.IsAllocated)
                    {
                        pinnedFont.Free();
                    }
                    fontBytes = null;
                }
            }
        }
    }
}
