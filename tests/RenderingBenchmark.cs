using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
namespace OpenCapture.Tests
{
    internal static class RenderingBenchmark
    {
        [STAThread] private static int Main()
        {
            try
            {
                NativeMethods.TryEnablePerMonitorV2DpiAwareness();
                Application.EnableVisualStyles();
                using (FloatingPanelForm panel = new FloatingPanelForm(true))
                using (Bitmap surface = new Bitmap(310, 194, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics graphics = Graphics.FromImage(surface)) graphics.Clear(Color.FromArgb(178,255,255,255));
                    panel.Show(); Application.DoEvents();
                    for (int i=0; i<30; i++) panel.Present(surface);
                    for (int round=0; round<5; round++)
                    {
                        Stopwatch time=Stopwatch.StartNew();
                        for (int i=0; i<240; i++) panel.Present(surface);
                        time.Stop();
                        Console.WriteLine("PRESENT_ROUND_{0}_MS={1:F2}",round,time.Elapsed.TotalMilliseconds);
                    }
                }
                using (PendingCapture capture = new PendingCapture(CaptureKind.Colour,null,"#72B7FF",DateTime.Now,null))
                using (CaptureReviewForm review = new CaptureReviewForm(capture))
                {
                    review.Show(); Application.DoEvents();
                    ((PresentationClock)typeof(CaptureReviewForm).GetField("countdown",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(review)).Stop();
                    Stopwatch clock=(Stopwatch)typeof(CaptureReviewForm).GetField("clock",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(review);
                    clock.Restart(); Thread.Sleep(20); clock.Stop();
                    MethodInfo refresh=typeof(CaptureReviewForm).GetMethod("RefreshSurface",BindingFlags.Instance|BindingFlags.NonPublic);
                    for (int i=0; i<30; i++) refresh.Invoke(review,null);
                    for (int round=0; round<5; round++)
                    {
                        Stopwatch time=Stopwatch.StartNew();
                        for (int i=0; i<240; i++) refresh.Invoke(review,null);
                        time.Stop();
                        Console.WriteLine("REVIEW_ROUND_{0}_MS={1:F2}",round,time.Elapsed.TotalMilliseconds);
                    }
                    review.Finish(false);
                }
                Console.WriteLine("PASS"); return 0;
            }
            catch(Exception error) { Console.WriteLine(error); return 1; }
        }
    }
}
