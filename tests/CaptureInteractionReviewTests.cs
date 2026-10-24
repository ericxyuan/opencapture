using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace OpenCapture.Tests
{
    internal static class CaptureInteractionReviewTests
    {
        internal static void Run()
        {
            TestPreviewMotion();
            TestPendingCrop();
            TestReviewDecisions();
            TestReviewTimeoutAndCropPause();
            TestVideoCropping();
            TestPresentationAndDirtyRegions();
            TestColourClipboard(TestFloatingPreview());
            Console.WriteLine("Capture interaction review checks passed");
        }

        private static void TestPreviewMotion()
        {
            PointF target = new PointF(40, 20);
            PointF first = PreviewMotion.Advance(PointF.Empty, target, 0.016);
            Require(first.X > 0 && first.X < target.X && first.Y > 0 && first.Y < target.Y,
                "preview presentation trails rather than snapping to the pointer");
            PointF twoSteps = PreviewMotion.Advance(first, target, 0.016);
            PointF oneStep = PreviewMotion.Advance(PointF.Empty, target, 0.032);
            Require(Math.Abs(twoSteps.X - oneStep.X) < 0.001 && Math.Abs(twoSteps.Y - oneStep.Y) < 0.001,
                "preview response is independent of timer tick frequency");
            PointF reversed = PreviewMotion.Advance(first, new PointF(-40, -20), 0.016);
            Require(reversed.X < first.X && reversed.X > -40,
                "direction reversals retarget existing motion without overshoot");
            PointF large = PreviewMotion.Advance(PointF.Empty, new PointF(1000, 1000), 0.016);
            double distance = Math.Sqrt(Math.Pow(1000 - large.X, 2) + Math.Pow(1000 - large.Y, 2));
            Require(distance <= 72.001, "fast movement cannot leave the panel far behind");
            PointF settled = PointF.Empty;
            for (int i = 0; i < 40; i++) settled = PreviewMotion.Advance(settled, target, 0.016);
            Require(settled == target, "trailing timer reaches an exact settled position and can stop");
        }

        private static void TestPendingCrop()
        {
            using (PendingCapture pending = new PendingCapture(CaptureKind.Text,
                CreateTestImage(), "original text", DateTime.Now, null))
            {
                Require(pending.CanCrop && !pending.HasCrop, "fresh review begins uncropped");
                pending.ApplyCrop(new Rectangle(20, 10, 100, 70));
                Require(pending.Text == null, "text crop invalidates the old OCR result");
                pending.ApplyCrop(new Rectangle(5, 6, 40, 30));
                Require(pending.Crop == new Rectangle(25, 16, 40, 30),
                    "successive crops stay relative to original captured pixels");
                using (Bitmap cropped = pending.RenderImage())
                {
                    Require(cropped.Size == new Size(40, 30), "crop uses exact physical dimensions");
                    Require(cropped.GetPixel(2, 2).ToArgb() == pending.Image.GetPixel(27, 18).ToArgb(),
                        "review does not resample saved screenshot pixels");
                }
                Rectangle before = pending.Crop;
                bool rejected = false;
                try { pending.ApplyCrop(new Rectangle(1000, 1000, 10, 10)); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Require(rejected && pending.Crop == before, "empty crop cannot damage a capture");
            }
            Rectangle fit = CaptureCropForm.FitImage(new Size(160, 80), new Rectangle(10, 20, 100, 100));
            Require(fit == new Rectangle(10, 45, 100, 50), "crop editor preserves image aspect ratio");
            Require(CaptureCropForm.ToImagePoint(new Point(110, 95), fit, new Size(160, 80)) == new Point(159, 79),
                "crop editor clamps to the last source pixel");
        }

        private static void TestReviewDecisions()
        {
            Require(CaptureReviewForm.RemainingAt(0) == 1 && CaptureReviewForm.RemainingAt(1000) == 0.5 &&
                CaptureReviewForm.RemainingAt(2000) == 0 && CaptureReviewForm.RemainingAt(-10) == 1 &&
                CaptureReviewForm.RemainingAt(3000) == 0, "donut drains linearly and stays bounded");
            using (PendingCapture pending = new PendingCapture(CaptureKind.Screenshot,
                CreateTestImage(), null, DateTime.Now, null))
            using (CaptureReviewForm form = new CaptureReviewForm(pending))
            {
                int decisions = 0;
                bool accepted = true;
                form.Decided += delegate(object sender, CaptureReviewDecisionEventArgs args)
                { decisions++; accepted = args.Accepted; };
                Render(form, "OpenCapture-capture-review-preview.png");
                Require(!form.ActionBounds(0).IsEmpty && !form.ActionBounds(1).IsEmpty &&
                    !form.ActionBounds(2).IsEmpty, "image review has confirm, crop, discard");
                form.Finish(false);
                form.Finish(true);
                Require(decisions == 1 && !accepted, "discard cannot be followed by acceptance");
            }
            using (PendingCapture colour = new PendingCapture(CaptureKind.Colour, null, "#72B7FF", DateTime.Now, null))
            using (CaptureReviewForm form = new CaptureReviewForm(colour))
            {
                Render(form, "OpenCapture-colour-review-preview.png");
                Require(form.ActionBounds(1).IsEmpty, "colour review omits inapplicable crop");
                int decisions = 0;
                form.Decided += delegate(object sender, CaptureReviewDecisionEventArgs args)
                { decisions++; Require(args.Accepted, "click-away auto-confirms"); };
                Invoke(form, "OutsideClicked", new Point(form.Bounds.Right + 20, form.Bounds.Bottom + 20));
                Application.DoEvents();
                Require(decisions == 1, "click-away emits exactly one confirmation");
            }
            using (PendingCapture text = new PendingCapture(CaptureKind.Text, CreateTestImage(), "OCR sample", DateTime.Now, null))
            using (CaptureReviewForm form = new CaptureReviewForm(text))
            {
                Render(form, "OpenCapture-text-review-preview.png");
                int decisions = 0;
                form.Decided += delegate(object sender, CaptureReviewDecisionEventArgs args)
                { decisions++; Require(args.Accepted, "closing review accepts the pending capture"); };
                form.Show();
                form.Close();
                Require(decisions == 1, "normal close does not strand text capture");
            }
            using (Bitmap image = CreateTestImage())
            using (CaptureCropForm form = new CaptureCropForm(image))
                Render(form, "OpenCapture-crop-editor-preview.png");
        }

        private static void TestReviewTimeoutAndCropPause()
        {
            using (PendingCapture pending = new PendingCapture(CaptureKind.Screenshot, CreateTestImage(), null, DateTime.Now, null))
            using (CaptureReviewForm form = new CaptureReviewForm(pending))
            {
                int decisions = 0;
                Stopwatch elapsed = Stopwatch.StartNew();
                form.Decided += delegate(object sender, CaptureReviewDecisionEventArgs args)
                { decisions++; Require(args.Accepted, "timeout accepts the capture"); };
                form.Show();
                StopPhysicalClickWatcher(form);
                PumpUntil(delegate { return decisions > 0; }, 3500);
                Require(decisions == 1 && elapsed.ElapsedMilliseconds >= 1950 && elapsed.ElapsedMilliseconds < 3300,
                    "review dismisses once after two seconds");
            }
            using (PendingCapture pending = new PendingCapture(CaptureKind.Screenshot, CreateTestImage(), null, DateTime.Now, null))
            using (CaptureReviewForm review = new CaptureReviewForm(pending))
            {
                int decisions = 0;
                review.Decided += delegate { decisions++; };
                review.Show();
                StopPhysicalClickWatcher(review);
                Invoke(review, "EditCrop");
                PumpUntil(delegate { return false; }, 2300);
                CaptureCropForm crop = review.CropEditor;
                Require(review.IsEditing && decisions == 0 && crop != null && review.Visible,
                    "inline crop keeps review visible indefinitely beyond the two-second deadline");
                Require(crop.FormBorderStyle == FormBorderStyle.None && crop.Controls.Count == 0,
                    "inline crop has no editor chrome, toolbar or extra controls");
                Point a = new Point(CaptureCropForm.MarginPixels, CaptureCropForm.MarginPixels);
                Point b = new Point(a.X + 20, a.Y + 12);
                Invoke(crop, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, a.X, a.Y, 0));
                Invoke(crop, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, b.X, b.Y, 0));
                Invoke(crop, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, b.X, b.Y, 0));
                Require(pending.Crop == new Rectangle(20, 12, 140, 84), "resize tabs crop exact original pixels in place");
                review.EndCrop(true);
                StopPhysicalClickWatcher(review);
                Require(!review.IsEditing && review.CropEditor == null && decisions == 0 && pending.HasCrop,
                    "Done removes resize tabs and returns to normal review");
                Require(review.RemainingFraction > 0.9, "crop returns to a fresh two-second donut");
                review.Finish(false);
            }
        }

        private static void TestPresentationAndDirtyRegions()
        {
            using (Region dirty = new Region(new Rectangle(0, 0, 47, 47)))
            {
                dirty.Union(new Rectangle(3500, 900, 257, 189));
                long pixels = 0;
                foreach (Rectangle tile in DirtyPaintRegion.Tiles(dirty, new Rectangle(0, 0, 5120, 1600)))
                {
                    pixels += (long)tile.Width * tile.Height;
                    Require(tile.Width <= 640 && tile.Height <= 640, "paint buffers remain bounded");
                }
                Require(pixels == 47L * 47 + 257L * 189, "far-apart cursor/panel updates never repaint the space between");
            }
            using (Control control = new Control())
            using (PresentationClock frames = new PresentationClock(control))
            {
                control.CreateControl();
                int delivered = 0;
                EventHandler once = delegate { delivered++; frames.Stop(); };
                frames.Tick += once;
                frames.Start();
                Thread.Sleep(100);
                Application.DoEvents();
                Require(delivered == 1, "a busy interface receives one newest frame rather than a stale backlog");
                frames.Tick -= once;
                System.Collections.Generic.List<double> intervals = new System.Collections.Generic.List<double>();
                Stopwatch clock = Stopwatch.StartNew();
                double previous = 0;
                frames.Tick += delegate
                {
                    double now = clock.Elapsed.TotalMilliseconds;
                    if (previous > 0) intervals.Add(now - previous);
                    previous = now;
                };
                frames.Start();
                PumpUntil(delegate { return intervals.Count >= 35; }, 1500, 0);
                frames.Stop();
                Require(intervals.Count >= 35, "active frame clock continues at high cadence");
                intervals.Sort();
                double median = intervals[intervals.Count / 2];
                Console.WriteLine("PREVIEW_CLOCK_MEDIAN_MS=" + median.ToString("0.00"));
                Require(median < 20, "presentation clock avoids the coarse 30 FPS timer cadence");
                delivered = intervals.Count;
                PumpUntil(delegate { return false; }, 60);
                Require(intervals.Count == delivered && !frames.Enabled, "frame requests stop entirely after settling");
            }
        }

        private static string TestFloatingPreview()
        {
            Rectangle desktop = Screen.PrimaryScreen.Bounds;
            Bitmap pixels = new Bitmap(desktop.Width, desktop.Height, PixelFormat.Format32bppRgb);
            using (Graphics graphics = Graphics.FromImage(pixels)) graphics.Clear(Color.FromArgb(0x34, 0xA2, 0xF1));
            using (CapturedFrame frame = new CapturedFrame(pixels, desktop, Stopwatch.GetTimestamp(), DateTime.UtcNow))
            using (OverlayForm overlay = new OverlayForm(frame, ZoneDetector.Capture(),
                new ShortcutSettings(ShortcutSettings.DefaultCapture, ShortcutSettings.DefaultCopyColour,
                    ShortcutSettings.DefaultCaptureZone)))
            {
                // The fixture renders real native surfaces, but input is supplied
                // by the test so unrelated physical mouse reports cannot interfere.
                overlay.Enabled = false;
                overlay.Show();
                Application.DoEvents();
                foreach (string name in new string[] { "pointerZoneTimer", "zoneRefreshTimer", "liveOcrTimer" })
                    ((System.Windows.Forms.Timer)GetPrivate(overlay, name)).Stop();
                // Keep unrelated live geometry/OCR responses out of this presentation benchmark.
                FieldInfo state = typeof(OverlayForm).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic);
                state.SetValue(overlay, Enum.Parse(state.FieldType, "Pressed"));
                SetPrivate(overlay, "pressStartedOverDetectedZone", true);
                SetPrivate(overlay, "pressStartedTimestamp", Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10);
                SetPrivate(overlay, "hoverZone", null);
                Point start = new Point(desktop.Left + desktop.Width / 4, desktop.Top + desktop.Height / 3);
                SetPrivate(overlay, "pointerScreen", start);
                Invoke(overlay, "RetargetInformationPanel", true);
                overlay.Update();
                FloatingPanelForm preview = (FloatingPanelForm)GetPrivate(overlay, "floatingPreview");
                Require(preview != null && preview.Visible && preview.IsHandleCreated,
                    "real overlay presents its independently composited cursor panel");

                PresentationClock clock = (PresentationClock)GetPrivate(overlay, "panelMotionTimer");
                MethodInfo animate = typeof(OverlayForm).GetMethod("AnimateInformationPanel",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                EventHandler builtIn = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), overlay, animate);
                clock.Tick -= builtIn;
                System.Collections.Generic.List<double> costs = new System.Collections.Generic.List<double>();
                int reports = 0;
                clock.Tick += delegate(object sender, EventArgs args)
                {
                    long begin = Stopwatch.GetTimestamp();
                    animate.Invoke(overlay, new object[] { sender, args });
                    costs.Add((Stopwatch.GetTimestamp() - begin) * 1000.0 / Stopwatch.Frequency);
                };
                Stopwatch movement = Stopwatch.StartNew();
                long nextReport = 0;
                PumpUntil(delegate
                {
                    if (movement.ElapsedMilliseconds < 350 && movement.ElapsedMilliseconds >= nextReport)
                    {
                        Point previous = overlay.PrecisionPointer;
                        Rectangle previousTarget = (Rectangle)Invoke(overlay, "CurrentTargetBounds");
                        overlay.ApplyPhysicalMouseDelta(1, reports % 4 == 0 ? 1 : 0);
                        Require(overlay.PrecisionPointer.X == previous.X + 1,
                            "precision input updates immediately without waiting for panel animation: " +
                            previous.X + " to " + overlay.PrecisionPointer.X + " at " + overlay.KeyboardSensitivityPercent + "%");
                        Invoke(overlay, "InvalidatePointerChange", previous, previousTarget);
                        reports++;
                        nextReport = movement.ElapsedMilliseconds + 1;
                    }
                    return movement.ElapsedMilliseconds >= 350 && !clock.Enabled;
                }, 1200, 0);
                Require(reports > 30 && costs.Count > 10 && costs.Count < reports && !clock.Enabled,
                    "high-rate motion renders only newest positions and stops when settled");
                costs.Sort();
                double median = costs[costs.Count / 2];
                double percentile95 = costs[Math.Min(costs.Count - 1, costs.Count * 95 / 100)];
                Console.WriteLine("PREVIEW_FRAME_COST_MEDIAN_MS={0:0.00} P95_MS={1:0.00} INPUT_REPORTS={2} FRAME_UPDATES={3}",
                    median, percentile95, reports, costs.Count);
                Require(median < 8 && percentile95 < 25,
                    "native panel and dirty cursor painting fit comfortably within the frame budget");

                Invoke(overlay, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 10, 10, -120));
                PumpUntil(delegate { return !clock.Enabled; }, 600, 0);
                Require((int)GetPrivate(overlay, "panelSurfaceSensitivity") == 75,
                    "scroll updates the visible sensitivity of the floating preview");
                Invoke(overlay, "OnMouseDown", new MouseEventArgs(MouseButtons.Middle, 1, 10, 10, 0));
                PumpUntil(delegate { return !clock.Enabled; }, 600, 0);
                Require((int)GetPrivate(overlay, "panelSurfaceSensitivity") == 100,
                    "middle click immediately refreshes the preview at 100 percent");

                state.SetValue(overlay, Enum.Parse(state.FieldType, "Inspecting"));
                string sampledHex = null;
                bool imageOutput = false;
                overlay.ColorCopyRequested += delegate(object sender, ColorCopyEventArgs args) { sampledHex = args.HexCode; };
                overlay.SelectionCompleted += delegate { imageOutput = true; };
                object[] key = new object[] { new Message(), Keys.Control | Keys.C };
                Require((bool)typeof(OverlayForm).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(overlay, key), "real overlay routes Ctrl+C to colour copy");
                Require(!preview.Visible, "colour flash hides the settled preview immediately");
                Invoke(overlay, "FinishSuccessFlash", overlay, EventArgs.Empty);
                Require(sampledHex == "#34A2F1" && !imageOutput, "Ctrl+C returns the sampled HEX without an image output");
                return sampledHex;
            }
        }

        private static object GetPrivate(object target, string name)
        { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }

        private static void SetPrivate(object target, string name, object value)
        { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }

        private static void TestColourClipboard(string sampledHex)
        {
            IDataObject original = Clipboard.GetDataObject();
            uint testSequence = 0;
            try
            {
                Exception error;
                int copies = 0;
                using (PendingCapture pending = new PendingCapture(CaptureKind.Colour, null, sampledHex, DateTime.Now, null))
                using (CaptureReviewForm review = new CaptureReviewForm(pending))
                {
                    review.Decided += delegate(object sender, CaptureReviewDecisionEventArgs args)
                    {
                        if (!args.Accepted) return;
                        copies++;
                        Require(OpenCaptureApplication.TryCopyColourToClipboard(pending.Text, out error),
                            "the actual colour clipboard transaction succeeds after review acceptance");
                    };
                    review.Finish(true);
                    review.Finish(true);
                }
                Require(copies == 1, "colour review acceptance copies once, never twice");
                testSequence = GetClipboardSequenceNumber();
                Require(Clipboard.GetText(TextDataFormat.UnicodeText) == "#34A2F1" && !Clipboard.ContainsImage(),
                    "colour copy writes the exact HEX text, not an image");
                Console.WriteLine("HEX_CLIPBOARD_PASS");
            }
            finally
            {
                // Restore the original clipboard, but never replace a newer user copy.
                if (testSequence != 0 && GetClipboardSequenceNumber() == testSequence)
                {
                    if (original == null) Clipboard.Clear();
                    else Clipboard.SetDataObject(original, true, 5, 50);
                }
            }
        }

        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

        private static void TestVideoCropping()
        {
            string directory = Path.Combine(Path.GetTempPath(), "OpenCapture-review-video-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string source = Path.Combine(directory, "source.avi");
                string output = Path.Combine(directory, "cropped.avi");
                using (Bitmap frame = CreateTestImage())
                using (MemoryStream encoded = new MemoryStream())
                using (MjpegAviWriter writer = new MjpegAviWriter(source, frame.Width, frame.Height, 60))
                {
                    frame.Save(encoded, ImageFormat.Jpeg);
                    for (int i = 0; i < 3; i++) writer.WriteFrame(encoded.GetBuffer(), (int)encoded.Length);
                }
                using (MjpegAviReader input = new MjpegAviReader(source))
                {
                    Require(input.Width == 160 && input.Height == 96 && input.FrameCount == 3 && input.FramesPerSecond == 60,
                        "review reads original recording dimensions, count, and rate");
                    using (Bitmap first = input.ReadFrame())
                    using (PendingCapture pending = new PendingCapture(CaptureKind.Recording,
                        new Bitmap(first), null, DateTime.Now, source))
                    using (CaptureReviewForm form = new CaptureReviewForm(pending))
                    {
                        pending.VideoPath = null; // The borrowed test source is not a discardable staging file.
                        Render(form, "OpenCapture-recording-review-preview.png");
                    }
                }
                MjpegAviReader.CropVideo(source, output, new Rectangle(24, 16, 64, 40));
                using (MjpegAviReader cropped = new MjpegAviReader(output))
                {
                    Require(cropped.Width == 64 && cropped.Height == 40 && cropped.FrameCount == 3 && cropped.FramesPerSecond == 60,
                        "video crop preserves every frame and 60 FPS timing");
                    int count = 0;
                    Bitmap next;
                    while ((next = cropped.ReadFrame()) != null)
                    {
                        using (next) Require(next.Size == new Size(64, 40), "all video frames use requested crop");
                        count++;
                    }
                    Require(count == 3, "streaming export retains all recording frames");
                }
                byte[] existing = File.ReadAllBytes(output);
                bool collisionRejected = false;
                try { MjpegAviReader.CropVideo(source, output, new Rectangle(0, 0, 12, 12)); }
                catch (IOException) { collisionRejected = true; }
                byte[] preserved = File.ReadAllBytes(output);
                Require(collisionRejected && Convert.ToBase64String(existing) == Convert.ToBase64String(preserved),
                    "crop never overwrites or deletes an existing video");
                string discarded = Path.Combine(directory, "private-staged.avi");
                File.Copy(source, discarded);
                using (PendingCapture pending = new PendingCapture(CaptureKind.Recording,
                    null, null, DateTime.Now, discarded)) { }
                Require(!File.Exists(discarded) && File.Exists(source) && File.Exists(output),
                    "discard removes only its private recording");
            }
            finally { Directory.Delete(directory, true); }
        }

        private static Bitmap CreateTestImage()
        {
            Bitmap image = new Bitmap(160, 96, PixelFormat.Format32bppRgb);
            using (Graphics graphics = Graphics.FromImage(image))
            using (SolidBrush blue = new SolidBrush(OpenCaptureTheme.Accent))
            {
                graphics.Clear(Color.White);
                graphics.FillRectangle(blue, 16, 12, 100, 64);
                graphics.FillEllipse(Brushes.White, 36, 25, 30, 30);
            }
            return image;
        }

        private static Button FindButton(Form form, string text)
        {
            foreach (Control control in form.Controls)
                if (control is Button && control.Text == text) return (Button)control;
            return null;
        }

        private static void Render(Form form, string name)
        {
            // Showing first also creates the native child button handles, so this
            // snapshot verifies the real UI rather than only the owner's paint.
            form.Show();
            Application.DoEvents();
            CaptureReviewForm review = form as CaptureReviewForm;
            using (Bitmap rendered = review == null ? new Bitmap(form.Width, form.Height) : review.RenderSurface())
            {
                if (review == null) form.DrawToBitmap(rendered, new Rectangle(Point.Empty, rendered.Size));
                string path = Path.Combine(Path.GetTempPath(), name);
                rendered.Save(path, ImageFormat.Png);
                Console.WriteLine("Preview: " + path);
            }
            form.Hide();
        }

        private static void StopPhysicalClickWatcher(CaptureReviewForm form)
        {
            // Isolate synthetic timing tests from unrelated real desktop clicks.
            ClickAwayWatcher watcher = (ClickAwayWatcher)typeof(CaptureReviewForm).GetField("clickWatcher",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
            if (watcher != null) watcher.Dispose();
        }

        private static object Invoke(object target, string method, params object[] args)
        {
            return target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        }

        private static void PumpUntil(Func<bool> done, int maximumMilliseconds, int pauseMilliseconds = 10)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < maximumMilliseconds)
            {
                Application.DoEvents();
                Thread.Sleep(pauseMilliseconds);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
        }
    }
}
