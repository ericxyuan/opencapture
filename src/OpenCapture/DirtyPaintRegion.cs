using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace OpenCapture
{
    internal static class DirtyPaintRegion
    {
        internal static Region Read(IntPtr window)
        {
            IntPtr region = CreateRectRgn(0, 0, 0, 0);
            try { return GetUpdateRgn(window, region, false) > 1 ? Region.FromHrgn(region) : null; }
            finally { if (region != IntPtr.Zero) DeleteObject(region); }
        }

        internal static IList<Rectangle> Tiles(Region region, Rectangle clip)
        {
            List<Rectangle> output = new List<Rectangle>();
            RectangleF[] scans;
            using (Matrix matrix = new Matrix())
                scans = region == null ? new RectangleF[] { clip } : region.GetRegionScans(matrix);
            foreach (RectangleF scan in scans)
            {
                Rectangle bounded = Rectangle.Intersect(clip, Rectangle.Ceiling(scan));
                for (int y = bounded.Top; y < bounded.Bottom; y += 640)
                    for (int x = bounded.Left; x < bounded.Right; x += 640)
                        output.Add(new Rectangle(x, y, Math.Min(640, bounded.Right - x), Math.Min(640, bounded.Bottom - y)));
            }
            return output;
        }

        [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
        [DllImport("user32.dll")] private static extern int GetUpdateRgn(IntPtr window, IntPtr region, bool erase);
    }
}
