using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenCapture
{
    // DWM moves this small alpha surface without copying the frozen desktop.
    // The preview and review share this exact background, border and shadow.
    internal class FloatingPanelForm : Form
    {
        private readonly bool clickThrough;
        private IntPtr surfaceDc;
        private IntPtr surfaceBitmap;
        private IntPtr surfaceBits;
        private IntPtr originalBitmap;
        private Size surfaceSize;
        internal FloatingPanelForm(bool ignoreMouse)
        {
            clickThrough = ignoreMouse;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x80000 | 0x80 | 0x08000000;
                if (clickThrough) value.ExStyle |= 0x20;
                return value;
            }
        }

        internal static void DrawChrome(Graphics graphics, Rectangle panel, Color accent)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle shadow = panel;
            shadow.Offset(3, 4);
            using (GraphicsPath shape = OpenCaptureTheme.CreateRoundedRectangle(panel, 9))
            using (GraphicsPath shadowShape = OpenCaptureTheme.CreateRoundedRectangle(shadow, 9))
            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
            using (SolidBrush white = new SolidBrush(Color.FromArgb(178, 255, 255, 255)))
            using (Pen border = new Pen(Color.FromArgb(225, accent), 1.0f))
            {
                graphics.FillPath(shadowBrush, shadowShape);
                graphics.FillPath(white, shape);
                graphics.DrawPath(border, shape);
            }
            graphics.SmoothingMode = SmoothingMode.None;
        }

        internal void Present(Bitmap surface)
        {
            if (IsDisposed) return;
            EnsureSurface(surface.Size);
            BitmapData pixels = surface.LockBits(new Rectangle(Point.Empty, surface.Size),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int rowBytes = checked(surface.Width * 4);
                if (pixels.Stride == rowBytes)
                    CopyMemory(surfaceBits, pixels.Scan0, new UIntPtr(checked((uint)(rowBytes * surface.Height))));
                else
                    for (int row = 0; row < surface.Height; row++)
                        CopyMemory(IntPtr.Add(surfaceBits, row * rowBytes),
                            IntPtr.Add(pixels.Scan0, row * pixels.Stride), new UIntPtr((uint)rowBytes));
            }
            finally { surface.UnlockBits(pixels); }

            NativeMethods.POINT location = new NativeMethods.POINT(Location);
            NativeMethods.POINT source = new NativeMethods.POINT(Point.Empty);
            NativeSize size = new NativeSize(surface.Width, surface.Height);
            Blend blend = new Blend();
            blend.Alpha = 255;
            blend.Format = 1;
            if (!UpdateLayeredWindow(Handle, IntPtr.Zero, ref location, ref size, surfaceDc,
                ref source, 0, ref blend, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private void EnsureSurface(Size size)
        {
            if (surfaceBitmap != IntPtr.Zero && surfaceSize == size) return;
            ReleaseSurface();
            try
            {
                surfaceDc = CreateCompatibleDC(IntPtr.Zero);
                if (surfaceDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                BitmapInfo info = new BitmapInfo();
                info.Size = (uint)Marshal.SizeOf(typeof(BitmapInfo));
                info.Width = size.Width;
                info.Height = -size.Height; // Top-down, premultiplied BGRA, like the managed surface.
                info.Planes = 1;
                info.BitCount = 32;
                surfaceBitmap = CreateDIBSection(surfaceDc, ref info, 0, out surfaceBits, IntPtr.Zero, 0);
                if (surfaceBitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                originalBitmap = SelectObject(surfaceDc, surfaceBitmap);
                if (originalBitmap == IntPtr.Zero || originalBitmap == new IntPtr(-1))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                surfaceSize = size;
            }
            catch { ReleaseSurface(); throw; }
        }

        private void ReleaseSurface()
        {
            if (surfaceDc != IntPtr.Zero && originalBitmap != IntPtr.Zero && originalBitmap != new IntPtr(-1))
                SelectObject(surfaceDc, originalBitmap);
            if (surfaceBitmap != IntPtr.Zero) DeleteObject(surfaceBitmap);
            if (surfaceDc != IntPtr.Zero) DeleteDC(surfaceDc);
            surfaceDc = surfaceBitmap = surfaceBits = originalBitmap = IntPtr.Zero;
            surfaceSize = Size.Empty;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseSurface();
            base.Dispose(disposing);
        }

        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
        {
            internal uint Size;
            internal int Width, Height;
            internal ushort Planes, BitCount;
            internal uint Compression, ImageSize;
            internal int XPixelsPerMeter, YPixelsPerMeter;
            internal uint ColoursUsed, ColoursImportant;
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeSize
        {
            internal int Width, Height;
            internal NativeSize(int width, int height) { Width = width; Height = height; }
        }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend
        { internal byte Operation, Flags, Alpha, Format; }
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(
            IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] private static extern void CopyMemory(
            IntPtr destination, IntPtr source, UIntPtr length);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(
            IntPtr window, IntPtr destination, ref NativeMethods.POINT location, ref NativeSize size,
            IntPtr sourceDc, ref NativeMethods.POINT source, uint key, ref Blend blend, uint flags);
    }
}
