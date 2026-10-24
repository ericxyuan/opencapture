using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace OpenCapture.Tests
{
    internal sealed class HdrPatchForm : Form
    {
        internal static readonly Color[] PatchColours = new Color[]
        {
            Color.FromArgb(32, 32, 32),
            Color.FromArgb(64, 64, 64),
            Color.FromArgb(96, 96, 96),
            Color.FromArgb(128, 128, 128),
            Color.FromArgb(160, 160, 160),
            Color.FromArgb(192, 192, 192),
            Color.FromArgb(224, 224, 224),
            Color.FromArgb(255, 255, 255),
            Color.FromArgb(128, 64, 32),
            Color.FromArgb(32, 128, 64)
        };

        internal HdrPatchForm(Rectangle monitorBounds)
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Bounds = new Rectangle(
                monitorBounds.Left + 20,
                monitorBounds.Top + 20,
                PatchColours.Length * 32,
                32);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            int index;
            for (index = 0; index < PatchColours.Length; index++)
            {
                using (SolidBrush brush = new SolidBrush(PatchColours[index]))
                {
                    eventArgs.Graphics.FillRectangle(brush, index * 32, 0, 32, 32);
                }
            }
        }
    }

    internal static class HdrPipelineDiagnostic
    {
        [STAThread]
        private static int Main()
        {
            NativeMethods.TryEnablePerMonitorV2DpiAwareness();
            Application.EnableVisualStyles();
            foreach (Screen screen in Screen.AllScreens)
            {
                using (HdrPatchForm form = new HdrPatchForm(screen.Bounds))
                {
                    form.Show();
                    Application.DoEvents();
                    NativeMethods.TryDwmFlush();
                    Thread.Sleep(50);
                    Application.DoEvents();
                    NativeMethods.TryDwmFlush();

                    IntPtr dc = NativeMethods.GetDC(IntPtr.Zero);
                    try
                    {
                        Console.WriteLine("DISPLAY {0} {1}", screen.DeviceName, screen.Bounds);
                        using (Bitmap converted = new Bitmap(
                            screen.Bounds.Width,
                            screen.Bounds.Height,
                            PixelFormat.Format32bppRgb))
                        {
                            using (Graphics graphics = Graphics.FromImage(converted))
                            {
                                IntPtr destinationDc = graphics.GetHdc();
                                try
                                {
                                    NativeMethods.BitBlt(
                                        destinationDc,
                                        0,
                                        0,
                                        converted.Width,
                                        converted.Height,
                                        dc,
                                        screen.Bounds.Left,
                                        screen.Bounds.Top,
                                        NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
                                }
                                finally
                                {
                                    graphics.ReleaseHdc(destinationDc);
                                }
                            }

                            IList<HdrMonitorAdjustment> adjustments =
                                HdrDisplayCompensation.DiscoverActiveAdjustments();
                            int replaced = HdrDesktopCapture.ReplaceHdrOutputs(
                                converted,
                                screen.Bounds,
                                adjustments);
                            Console.WriteLine("  FP16_OUTPUTS_REPLACED={0}", replaced);
                        int index;
                        for (index = 0; index < HdrPatchForm.PatchColours.Length; index++)
                        {
                            Color expected = HdrPatchForm.PatchColours[index];
                            int x = form.Left + index * 32 + 16;
                            int y = form.Top + 16;
                            uint raw = GetPixel(dc, x, y);
                            Color captured = Color.FromArgb(
                                (int)(raw & 0xff),
                                (int)((raw >> 8) & 0xff),
                                (int)((raw >> 16) & 0xff));
                            Color toneMapped = converted.GetPixel(
                                x - screen.Bounds.Left,
                                y - screen.Bounds.Top);
                            Console.WriteLine(
                                "  IN #{0:X2}{1:X2}{2:X2} -> GDI #{3:X2}{4:X2}{5:X2} -> FP16_SDR #{6:X2}{7:X2}{8:X2}",
                                expected.R,
                                expected.G,
                                expected.B,
                                captured.R,
                                captured.G,
                                captured.B,
                                toneMapped.R,
                                toneMapped.G,
                                toneMapped.B);
                        }
                        }
                    }
                    finally
                    {
                        NativeMethods.ReleaseDC(IntPtr.Zero, dc);
                        form.Close();
                    }
                }
            }
            return 0;
        }

        [DllImport("gdi32.dll")]
        private static extern uint GetPixel(IntPtr deviceContext, int x, int y);
    }
}
