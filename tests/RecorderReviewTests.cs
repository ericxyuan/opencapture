using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;

namespace OpenCapture.Tests
{
    internal static class RecorderReviewTests
    {
        internal static void Run()
        {
            TestExistingOutputSurvivesRecordingFailure();
            TestFrameRateValidation();
            TestHdrPointerOnlyFrames();
            TestUnstartedRecorderDisposal();
            TestStopSignalSurvivesSlowWorker();
        }

        private static void TestExistingOutputSurvivesRecordingFailure()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "OpenCapture-recorder-collision-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "existing.avi");
            byte[] original = new byte[] { 7, 3, 1, 9, 0, 255 };
            File.WriteAllBytes(path, original);
            SynchronizationContext previousContext = SynchronizationContext.Current;
            try
            {
                // An output collision is rejected before any desktop or HDR resources
                // are opened. Deliver completion here without requiring a UI loop.
                SynchronizationContext.SetSynchronizationContext(null);
                using (ManualResetEvent completed = new ManualResetEvent(false))
                using (ScreenRecorder recorder = new ScreenRecorder(
                    new Rectangle(0, 0, 8, 8),
                    path))
                {
                    RecordingCompletedEventArgs result = null;
                    recorder.Completed += delegate(object sender, RecordingCompletedEventArgs args)
                    {
                        result = args;
                        completed.Set();
                    };
                    recorder.Start();
                    Require(completed.WaitOne(5000),
                        "a recording output collision completes promptly");
                    Require(result != null && result.Error is IOException && result.FrameCount == 0,
                        "a recording output collision reports failure without frames");
                    Require(File.Exists(path),
                        "a recording output collision preserves the existing file");
                    byte[] remaining = File.ReadAllBytes(path);
                    Require(remaining.Length == original.Length,
                        "a recording output collision preserves the existing file length");
                    for (int i = 0; i < original.Length; i++)
                    {
                        Require(remaining[i] == original[i],
                            "a recording output collision preserves every existing byte");
                    }
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                Directory.Delete(directory, true);
            }
        }

        private static void TestFrameRateValidation()
        {
            string path = Path.Combine(Path.GetTempPath(), "unused-recorder-test.avi");
            foreach (int fps in new int[] { -1, 0, 121 })
            {
                bool rejected = false;
                try
                {
                    using (ScreenRecorder recorder = new ScreenRecorder(
                        new Rectangle(0, 0, 8, 8), path, fps))
                    {
                    }
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    rejected = exception.ParamName == "fps";
                }
                Require(rejected, "invalid recorder frame rates are rejected before starting");
            }
            using (ScreenRecorder first = new ScreenRecorder(new Rectangle(0, 0, 8, 8), path, 1))
            using (ScreenRecorder last = new ScreenRecorder(new Rectangle(0, 0, 8, 8), path, 120))
            {
            }
        }

        private static void TestHdrPointerOnlyFrames()
        {
            Require(HdrDesktopCapture.CanReuseCachedFrame(0, true),
                "HDR pointer-only updates reuse converted pixels");
            Require(!HdrDesktopCapture.CanReuseCachedFrame(10, true),
                "HDR desktop updates require a new conversion");
            Require(!HdrDesktopCapture.CanReuseCachedFrame(0, false),
                "the first HDR frame is converted even for a pointer-only update");
            Require(!HdrDesktopCapture.CanReuseCachedFrame(10, false),
                "the first HDR desktop update requires conversion");
        }

        private static void TestUnstartedRecorderDisposal()
        {
            string path = Path.Combine(Path.GetTempPath(), "unused-recorder-disposal-test.avi");
            ScreenRecorder recorder = new ScreenRecorder(new Rectangle(0, 0, 8, 8), path);
            recorder.Dispose();
            recorder.Stop();
            recorder.Dispose();
            bool rejected = false;
            try
            {
                recorder.Start();
            }
            catch (ObjectDisposedException)
            {
                rejected = true;
            }
            Require(rejected, "a disposed recorder cannot start");
        }

        private static void TestStopSignalSurvivesSlowWorker()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "OpenCapture-recorder-lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "existing.avi");
            File.WriteAllBytes(path, new byte[] { 3, 5, 7 });
            SynchronizationContext previousContext = SynchronizationContext.Current;
            ScreenRecorder recorder = null;
            Thread worker = null;
            using (BlockingCompletionContext context = new BlockingCompletionContext())
            {
                try
                {
                    // Hold the real recording worker in its completion post for longer
                    // than Dispose's join bound, without opening any desktop resources.
                    SynchronizationContext.SetSynchronizationContext(context);
                    recorder = new ScreenRecorder(new Rectangle(0, 0, 8, 8), path);
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                    recorder.Completed += delegate { };
                    recorder.Start();
                    Require(context.Entered.WaitOne(5000),
                        "the slow-worker test reaches completion posting");
                    worker = (Thread)typeof(ScreenRecorder).GetField(
                        "worker", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(recorder);
                    ManualResetEvent stopSignal = (ManualResetEvent)typeof(ScreenRecorder).GetField(
                        "stopRequested", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(recorder);
                    recorder.Dispose();
                    Require(worker.IsAlive,
                        "recorder disposal retains its bounded join for slow workers");
                    Require(stopSignal.WaitOne(0),
                        "the stop signal remains open and signalled while a worker is alive");
                    recorder.Stop();
                    context.Continue.Set();
                    Require(worker.Join(5000), "the slow worker finishes after it is released");
                    bool handleClosed = false;
                    try
                    {
                        stopSignal.WaitOne(0);
                    }
                    catch (ObjectDisposedException)
                    {
                        handleClosed = true;
                    }
                    Require(handleClosed,
                        "the worker releases its stop signal after actual completion");
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                    context.Continue.Set();
                    if (recorder != null)
                    {
                        recorder.Dispose();
                    }
                    if (worker != null && worker.IsAlive)
                    {
                        worker.Join(5000);
                    }
                    Directory.Delete(directory, true);
                }
            }
        }

        private sealed class BlockingCompletionContext : SynchronizationContext, IDisposable
        {
            internal readonly ManualResetEvent Entered = new ManualResetEvent(false);
            internal readonly ManualResetEvent Continue = new ManualResetEvent(false);

            public override void Post(SendOrPostCallback callback, object state)
            {
                Entered.Set();
                Continue.WaitOne();
                callback(state);
            }

            public void Dispose()
            {
                Entered.Dispose();
                Continue.Dispose();
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("FAIL: " + message);
            }
        }
    }
}
