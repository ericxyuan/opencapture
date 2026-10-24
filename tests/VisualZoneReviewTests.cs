using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

namespace OpenCapture.Tests
{
    internal static class VisualZoneReviewTests
    {
        internal static void Run()
        {
            TestRepeatedBackgroundQueries();
            TestChangedQueryAndDisposal();
            TestShellDeadlinePublicationOrdering();
        }

        private static void TestRepeatedBackgroundQueries()
        {
            Rectangle desktop = new Rectangle(-100, 30, 2000, 500);
            using (Bitmap bitmap = new Bitmap(
                desktop.Width,
                desktop.Height,
                PixelFormat.Format32bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.FromArgb(240, 242, 245));
                }

                using (CapturedFrame frame = new CapturedFrame(
                    bitmap,
                    desktop,
                    0,
                    DateTime.UtcNow))
                using (VisualZoneDetector detector = new VisualZoneDetector(frame))
                {
                    Require(detector.SampleStep == 1, "one million cells used for miss benchmark");
                    Point pointer = new Point(desktop.Left + 1000, desktop.Top + 250);
                    Rectangle firstBounds;
                    Stopwatch firstQuery = Stopwatch.StartNew();
                    bool firstFound = detector.TryFindZoneAt(pointer, desktop, out firstBounds);
                    firstQuery.Stop();
                    Require(!firstFound && firstBounds == desktop,
                        "uniform surface returns unchanged fallback");
                    int firstGeneration = ReadVisitGeneration(detector);
                    Require(firstGeneration > 0, "initial uniform query performs component discovery");

                    const int RepeatedQueries = 1000;
                    Stopwatch repeated = Stopwatch.StartNew();
                    for (int index = 0; index < RepeatedQueries; index++)
                    {
                        Rectangle repeatedBounds;
                        Require(!detector.TryFindZoneAt(pointer, desktop, out repeatedBounds) &&
                            repeatedBounds == firstBounds,
                            "repeated uniform query preserves negative result and fallback");
                    }
                    repeated.Stop();
                    Require(ReadVisitGeneration(detector) == firstGeneration,
                        "repeated negative query does not repeat flood discovery");
                    Console.WriteLine(
                        "VISUAL_FIRST_QUERY_MS={0:F3} VISUAL_REPEAT_COUNT={1} VISUAL_REPEATS_TOTAL_MS={2:F3}",
                        firstQuery.Elapsed.TotalMilliseconds,
                        RepeatedQueries,
                        repeated.Elapsed.TotalMilliseconds);

                    Rectangle ignored;
                    Point movedPointer = new Point(pointer.X + 1, pointer.Y);
                    detector.TryFindZoneAt(movedPointer, desktop, out ignored);
                    int movedGeneration = ReadVisitGeneration(detector);
                    Require(movedGeneration > firstGeneration,
                        "changed pointer recomputes an uncached background result");

                    Rectangle changedParent = new Rectangle(
                        desktop.Left + 1,
                        desktop.Top,
                        desktop.Width - 1,
                        desktop.Height);
                    Require(!detector.TryFindZoneAt(movedPointer, changedParent, out ignored) &&
                        ignored == changedParent,
                        "changed parent preserves its own fallback");
                    Require(ReadVisitGeneration(detector) > movedGeneration,
                        "changed parent recomputes an uncached background result");
                }
            }
        }

        private static void TestChangedQueryAndDisposal()
        {
            Rectangle desktop = new Rectangle(25, -40, 640, 360);
            using (Bitmap bitmap = new Bitmap(
                desktop.Width,
                desktop.Height,
                PixelFormat.Format32bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                using (SolidBrush card = new SolidBrush(Color.FromArgb(112, 172, 218)))
                {
                    graphics.Clear(Color.FromArgb(242, 244, 248));
                    graphics.FillRectangle(card, 50, 50, 160, 100);
                }

                using (CapturedFrame frame = new CapturedFrame(
                    bitmap,
                    desktop,
                    0,
                    DateTime.UtcNow))
                using (VisualZoneDetector detector = new VisualZoneDetector(frame))
                {
                    Point cardPointer = new Point(desktop.Left + 80, desktop.Top + 80);
                    Rectangle cardBounds;
                    Require(detector.TryFindZoneAt(cardPointer, desktop, out cardBounds),
                        "card query finds a visual region");
                    Rectangle repeatedBounds;
                    Require(detector.TryFindZoneAt(cardPointer, desktop, out repeatedBounds) &&
                        repeatedBounds == cardBounds,
                        "repeated successful query preserves exact bounds");

                    Point backgroundPointer = new Point(desktop.Right - 20, desktop.Bottom - 20);
                    Rectangle backgroundBounds;
                    Require(!detector.TryFindZoneAt(backgroundPointer, desktop, out backgroundBounds) &&
                        backgroundBounds == desktop,
                        "changed point does not reuse another point's positive result");
                    Require(detector.TryFindZoneAt(cardPointer, desktop, out repeatedBounds) &&
                        repeatedBounds == cardBounds,
                        "returning to a card retains existing region-cache behavior");

                    detector.Dispose();
                    bool rejectedDisposedQuery = false;
                    try
                    {
                        detector.TryFindZoneAt(cardPointer, desktop, out repeatedBounds);
                    }
                    catch (ObjectDisposedException)
                    {
                        rejectedDisposedQuery = true;
                    }
                    Require(rejectedDisposedQuery,
                        "cached result never bypasses disposed detector check");
                }
            }
        }

        private static int ReadVisitGeneration(VisualZoneDetector detector)
        {
            FieldInfo field = typeof(VisualZoneDetector).GetField(
                "visitGeneration",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Require(field != null, "component traversal counter exists");
            return (int)field.GetValue(detector);
        }

        private static void TestShellDeadlinePublicationOrdering()
        {
            // Avoid constructing a provider here: its normal constructor starts
            // live shell/UIA scans. This regression only exercises publication.
            ShellZoneProvider provider = (ShellZoneProvider)
                FormatterServices.GetUninitializedObject(typeof(ShellZoneProvider));
            object publishLock = new object();
            SetProviderField(provider, "_publishLock", publishLock);
            SetProviderField(provider, "_collectedZones", new List<ShellZone>());
            SetProviderField(provider, "_snapshot",
                new ReadOnlyCollection<ShellZone>(new ShellZone[0]));
            SetProviderField(provider, "_deadlineTimestamp", long.MaxValue);

            MethodInfo deadline = typeof(ShellZoneProvider).GetMethod(
                "DeadlineElapsed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo publish = typeof(ShellZoneProvider).GetMethod(
                "PublishDiscoveredZones",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Require(deadline != null && publish != null,
                "shell publication and completion methods exist");

            using (provider)
            using (ManualResetEvent deadlineStarted = new ManualResetEvent(false))
            using (ManualResetEvent deadlineFinished = new ManualResetEvent(false))
            {
                Exception workerError = null;
                Thread deadlineWorker = new Thread(delegate()
                {
                    deadlineStarted.Set();
                    try
                    {
                        deadline.Invoke(provider, new object[] { null });
                    }
                    catch (Exception exception)
                    {
                        workerError = exception;
                    }
                    finally
                    {
                        deadlineFinished.Set();
                    }
                });
                deadlineWorker.IsBackground = true;
                ReadOnlyCollection<ShellZone> finalSnapshot = null;
                try
                {
                    lock (publishLock)
                    {
                        deadlineWorker.Start();
                        Require(deadlineStarted.WaitOne(2000), "deadline worker starts");
                        Require(!deadlineFinished.WaitOne(100) && !provider.IsComplete,
                            "deadline cannot publish completion during an active publication");

                        publish.Invoke(provider, new object[]
                        {
                            new ShellZone[] { CreateSyntheticShellZone(10) }
                        });
                        finalSnapshot = provider.Snapshot;
                        Require(finalSnapshot.Count == 1 && !provider.IsComplete,
                            "active publication finishes before deadline completion");
                    }
                }
                finally
                {
                    Require(deadlineWorker.Join(2000), "deadline worker finishes after publication");
                }

                Require(workerError == null && provider.IsComplete,
                    "deadline publishes completion after final snapshot");
                Require(ReferenceEquals(provider.Snapshot, finalSnapshot),
                    "completed provider exposes the final published snapshot");
                publish.Invoke(provider, new object[]
                {
                    new ShellZone[] { CreateSyntheticShellZone(80) }
                });
                Require(ReferenceEquals(provider.Snapshot, finalSnapshot) &&
                    provider.Snapshot.Count == 1,
                    "no snapshot can be published after completion");
                provider.Dispose();
                publish.Invoke(provider, new object[]
                {
                    new ShellZone[] { CreateSyntheticShellZone(160) }
                });
                Require(ReferenceEquals(provider.Snapshot, finalSnapshot),
                    "disposed provider rejects subsequent publication");
            }
        }

        private static ShellZone CreateSyntheticShellZone(int left)
        {
            return new ShellZone(
                new Rectangle(left, 10, 40, 30),
                ShellZoneKind.TaskbarAppButton,
                "Synthetic app",
                "SyntheticApp",
                "SyntheticClass",
                1,
                new IntPtr(1),
                100);
        }

        private static void SetProviderField(
            ShellZoneProvider provider,
            string name,
            object value)
        {
            FieldInfo field = typeof(ShellZoneProvider).GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Require(field != null, "shell field exists: " + name);
            field.SetValue(provider, value);
        }

        private static void Require(bool condition, string description)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Visual zone review regression: " + description);
            }
        }
    }
}
