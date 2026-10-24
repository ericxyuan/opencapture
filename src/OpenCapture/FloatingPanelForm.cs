using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenCapture
{
    // DWM moves this small alpha surface without copying the frozen desktop.
    // The preview and review share this exact background, border and shadow.
    internal class FloatingPanelForm : Form
    {
        private readonly bool clickThrough;
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
            IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr bitmap = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;
            try
            {
                bitmap = surface.GetHbitmap(Color.FromArgb(0));
                previous = SelectObject(memory, bitmap);
                NativeMethods.POINT location = new NativeMethods.POINT(Location);
                NativeMethods.POINT source = new NativeMethods.POINT(Point.Empty);
                NativeSize size = new NativeSize(surface.Width, surface.Height);
                Blend blend = new Blend();
                blend.Alpha = 255;
                blend.Format = 1;
                if (!UpdateLayeredWindow(Handle, screen, ref location, ref size, memory,
                    ref source, 0, ref blend, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memory, previous);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memory != IntPtr.Zero) DeleteDC(memory);
                if (screen != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeSize
        {
            internal int Width, Height;
            internal NativeSize(int width, int height) { Width = width; Height = height; }
        }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend
        { internal byte Operation, Flags, Alpha, Format; }
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(
            IntPtr window, IntPtr destination, ref NativeMethods.POINT location, ref NativeSize size,
            IntPtr sourceDc, ref NativeMethods.POINT source, uint key, ref Blend blend, uint flags);
    }
}
