using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenCapture.Tests
{
    internal static class PerformanceReviewTests
    {
        internal static void Run()
        {
            using (Bitmap obsoleteCrop = new Bitmap(1, 1))
            {
                OcrRecognition obsolete = WindowsOcrEngine.RecognizeDetailed(obsoleteCrop, delegate { return false; });
                Require(ReferenceEquals(obsolete, OcrRecognition.Empty),
                    "an obsolete OCR request returns before preparing images or invoking Windows OCR");
            }
            // WinForms lazily creates shared brushes/fonts on its first window.
            using (FloatingPanelForm warmup = new FloatingPanelForm(true))
            using (Bitmap pixels = new Bitmap(8, 8, PixelFormat.Format32bppPArgb))
            {
                warmup.Show();
                Application.DoEvents();
                warmup.Present(pixels);
            }
            Application.DoEvents();
            uint before = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
            using (FloatingPanelForm panel = new FloatingPanelForm(true))
            using (Bitmap surface = new Bitmap(65, 33, PixelFormat.Format32bppPArgb))
            {
                panel.ClientSize = surface.Size;
                panel.Show();
                Application.DoEvents();
                using (Graphics graphics = Graphics.FromImage(surface)) graphics.Clear(Color.FromArgb(128, 80, 160, 240));
                panel.Present(surface);
                IntPtr initial = GetPointer(panel, "surfaceBitmap");
                IntPtr bits = GetPointer(panel, "surfaceBits");
                Require(Marshal.ReadInt32(bits) == unchecked((int)0x80285078),
                    "native surface preserves premultiplied alpha and BGRA channels");
                uint active = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                Stopwatch timer = Stopwatch.StartNew();
                for (int index = 0; index < 400; index++) panel.Present(surface);
                timer.Stop();
                Console.WriteLine("LAYERED_PRESENT_400_MS={0:0.2}", timer.Elapsed.TotalMilliseconds);
                Require(GetPointer(panel, "surfaceBitmap") == initial,
                    "animation reuses its native bitmap instead of allocating per frame");
                Require(GetGuiResources(Process.GetCurrentProcess().Handle, 0) <= active + 1,
                    "repeated presentation does not grow GDI handle use");
                using (Bitmap resized = new Bitmap(17, 9, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics graphics = Graphics.FromImage(resized)) graphics.Clear(Color.Blue);
                    panel.Present(resized);
                    Require(Marshal.ReadInt32(GetPointer(panel, "surfaceBits")) == Color.Blue.ToArgb(),
                        "resizing replaces the surface and copies the new pixels");
                }
                panel.Present(surface);
                Require(Marshal.ReadInt32(GetPointer(panel, "surfaceBits")) == unchecked((int)0x80285078),
                    "returning to the previous size cannot leave stale pixels");
            }
            Application.DoEvents();
            uint after = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
            Console.WriteLine("PANEL_GDI_BEFORE={0} AFTER={1}", before, after);
            for (int cycle = 0; cycle < 8; cycle++)
            {
                using (FloatingPanelForm panel = new FloatingPanelForm(true))
                using (Bitmap pixels = new Bitmap(65, 33, PixelFormat.Format32bppPArgb))
                {
                    panel.Show();
                    Application.DoEvents();
                    panel.Present(pixels);
                }
            }
            Application.DoEvents();
            uint repeated = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
            Console.WriteLine("PANEL_GDI_AFTER_EIGHT_REOPENS={0}", repeated);
            Require(repeated <= after + 2,
                "closing a floating panel releases its native drawing resources");

            using (PendingCapture capture = new PendingCapture(CaptureKind.Colour, null, "#72B7FF", DateTime.Now, null))
            using (CaptureReviewForm review = new CaptureReviewForm(capture))
            using (Bitmap ring = new Bitmap(257, 189, PixelFormat.Format32bppPArgb))
            using (Graphics graphics = Graphics.FromImage(ring))
            {
                // Exercise the tiny arcs immediately before expiry, including the
                // exact endpoints that must never be passed as degenerate arcs.
                for (int index = 0; index <= 720; index++) CaptureReviewForm.DrawCountdown(graphics, index / 720.0);
                CaptureReviewForm.DrawCountdown(graphics, 0.00003);
                CaptureReviewForm.DrawCountdown(graphics, 0.99997);
                review.Show();
                Application.DoEvents();
                ((PresentationClock)typeof(CaptureReviewForm).GetField("countdown", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(review)).Stop();
                MethodInfo refresh = typeof(CaptureReviewForm).GetMethod("RefreshSurface", BindingFlags.Instance | BindingFlags.NonPublic);
                object initial = typeof(CaptureReviewForm).GetField("reviewSurface", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(review);
                Stopwatch timer = Stopwatch.StartNew();
                for (int index = 0; index < 240; index++) refresh.Invoke(review, null);
                timer.Stop();
                Console.WriteLine("REVIEW_REFRESH_240_MS={0:0.2}", timer.Elapsed.TotalMilliseconds);
                Require(ReferenceEquals(initial, typeof(CaptureReviewForm).GetField("reviewSurface", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(review)),
                    "countdown reuses its managed drawing surface");
                review.Finish(false);
            }
            Console.WriteLine("PERFORMANCE_REVIEW_PASS");
        }

        private static IntPtr GetPointer(object target, string name)
        { return (IntPtr)typeof(FloatingPanelForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
    }
}
