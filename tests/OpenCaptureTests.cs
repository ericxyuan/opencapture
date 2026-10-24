using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace OpenCapture.Tests
{
    internal static class TestRunner
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                NativeMethods.TryEnablePerMonitorV2DpiAwareness();
                Application.EnableVisualStyles();
                if (Array.IndexOf(args, "--ownership-only") >= 0)
                {
                    TestCaptureFileNames();
                    TestOwnershipSaveCleanup();
                    StorageReviewTests.Run();
                    Console.WriteLine("PASS");
                    return 0;
                }
                if (Array.IndexOf(args, "--review-only") >= 0)
                {
                    RunReviewTests();
                    Console.WriteLine("PASS");
                    return 0;
                }
                // Measure OCR before desktop/UI fixtures queue accessibility and
                // live-recognition work; their background load skews its deadline.
                TestWindowsOcr();
                RunReviewTests();
                TestThemeShortcutsAndMenus();
                TestCrashRecoveryPolicy();
                TestStorageEstimates();
                TestBufferedOverlayText();
                TestCaptureFileNames();
                TestOwnershipSaveCleanup();
                TestCopiedTextFileNames();
                TestVideoFilesAndRecorder();
                TestRecordingBorderThickness();
                TestGestureTiming();
                TestHdrDisplayCompensation();
                TestVisualColourZones();
                TestSemanticCandidateValidation();
                TestApplicationZones();
                IList<ShellZone> warmedShellZones = TestShellZones();
                Rectangle expected = NativeMethods.GetVirtualScreenBounds();
                Require(expected.Width > 0 && expected.Height > 0, "virtual desktop bounds");

                Stopwatch captureTimer = Stopwatch.StartNew();
                using (CapturedFrame frame = CaptureEngine.CaptureVirtualDesktop(false))
                {
                    captureTimer.Stop();
                    Console.WriteLine(
                        "CAPTURE_MS={0} HDR_OUTPUTS={1}",
                        captureTimer.ElapsedMilliseconds,
                        frame.HdrOutputsToneMapped);
                    Require(frame.DesktopBounds == expected, "captured bounds match virtual desktop");
                    Require(frame.Bitmap.Width == expected.Width, "captured width");
                    Require(frame.Bitmap.Height == expected.Height, "captured height");

                    Point center = new Point(
                        expected.Left + expected.Width / 2,
                        expected.Top + expected.Height / 2);
                    Color sampled = frame.GetPixelAtDesktopPoint(center);
                    Require(sampled.A == 255, "captured pixels are opaque");
                    IList<HdrMonitorAdjustment> liveHdr =
                        HdrDisplayCompensation.DiscoverActiveAdjustments();
                    if (liveHdr.Count > 0)
                    {
                        Require(frame.HdrOutputsToneMapped > 0,
                            "native FP16 HDR desktop capture is active");
                    }

                    Rectangle cropBounds = new Rectangle(center.X, center.Y, 17, 13);
                    using (Bitmap crop = frame.Crop(cropBounds))
                    {
                        Require(crop.Width == 17 && crop.Height == 13, "crop dimensions");
                    }

                    ZoneDetector detector = ZoneDetector.Capture(frame, warmedShellZones);
                    Require(detector.Zones.Count > 0, "zones discovered");
                    Zone centerZone = detector.FindZoneAt(center);
                    Require(centerZone != null, "zone lookup");
                    ShellZone taskbarApp = FindShellZone(
                        warmedShellZones,
                        ShellZoneKind.TaskbarAppButton);
                    Require(taskbarApp != null, "prewarmed taskbar app available");
                    Point appPoint = new Point(
                        taskbarApp.Bounds.Left + taskbarApp.Bounds.Width / 2,
                        taskbarApp.Bounds.Top + taskbarApp.Bounds.Height / 2);
                    MethodInfo findFallback = typeof(ZoneDetector).GetMethod(
                        "FindWindowOrMonitorAt",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Zone fallbackAtApp = findFallback == null
                        ? null
                        : (Zone)findFallback.Invoke(detector, new object[] { appPoint });
                    Rectangle liveRootBounds;
                    bool liveRootBoundsFound = NativeMethods.TryGetExtendedFrameBounds(
                        taskbarApp.RootWindowHandle,
                        out liveRootBounds);
                    Zone immediateAppZone = detector.FindZoneAt(appPoint);
                    Console.WriteLine(
                        "TASKBAR_EXPECTED={0} ROOT={1} ROOT_BOUNDS={2}:{3} FALLBACK={4} ACTUAL={5}",
                        taskbarApp,
                        taskbarApp.RootWindowHandle,
                        liveRootBoundsFound,
                        liveRootBounds,
                        fallbackAtApp == null ? "<null>" : fallbackAtApp.ToString() +
                            " top=" + fallbackAtApp.TopLevelWindowHandle,
                        immediateAppZone == null ? "<null>" : immediateAppZone.ToString());
                    Zone refinedAppZone = immediateAppZone;
                    Stopwatch refinementWait = Stopwatch.StartNew();
                    while ((refinedAppZone == null ||
                            refinedAppZone.Kind != ZoneKind.ShellElement) &&
                           refinementWait.ElapsedMilliseconds < 1200)
                    {
                        Thread.Sleep(25);
                        refinedAppZone = detector.FindZoneAt(appPoint);
                    }
                    if (fallbackAtApp != null && fallbackAtApp.Kind != ZoneKind.Monitor &&
                        fallbackAtApp.TopLevelWindowHandle != taskbarApp.RootWindowHandle)
                    {
                        // A full-screen app can legitimately cover the live taskbar.
                        // Do not change the user's desktop just to expose a test icon.
                        Require(refinedAppZone != null && refinedAppZone.Kind != ZoneKind.ShellElement &&
                            refinedAppZone.TopLevelWindowHandle == fallbackAtApp.TopLevelWindowHandle,
                            "visible app takes priority over an occluded taskbar icon");
                        Console.WriteLine("SKIP visible taskbar hit test: another window covers the taskbar");
                    }
                    else
                    {
                        Require(
                            refinedAppZone != null &&
                            refinedAppZone.Kind == ZoneKind.ShellElement,
                            "taskbar app resolves to its semantic zone");
                    }
                    TestStaleShellZoneReplacement(frame, warmedShellZones);

                    string previewPath = Path.Combine(
                        Path.GetTempPath(),
                        "OpenCapture-overlay-preview.png");
                    ShortcutSettings testShortcuts = new ShortcutSettings(
                        ShortcutSettings.DefaultCapture,
                        ShortcutSettings.DefaultCopyColour,
                        ShortcutSettings.DefaultCaptureZone);
                    MethodInfo processShortcut = typeof(OverlayForm).GetMethod(
                        "ProcessCmdKey",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Require(processShortcut != null, "overlay shortcut routing exists");
                    using (OverlayForm overlay = new OverlayForm(
                        frame,
                        detector,
                        testShortcuts))
                    using (Bitmap preview = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb))
                    {
                        bool selectionRaised = false;
                        string copiedHex = null;
                        overlay.SelectionCompleted += delegate { selectionRaised = true; };
                        overlay.ColorCopyRequested += delegate(object sender, ColorCopyEventArgs eventArgs)
                        {
                            copiedHex = eventArgs.HexCode;
                        };
                        overlay.CreateControl();
                        overlay.DrawToBitmap(preview, new Rectangle(0, 0, preview.Width, preview.Height));
                        preview.Save(previewPath, ImageFormat.Png);
                        Console.WriteLine("PREVIEW=" + previewPath);

                        object[] passiveCopyArguments = new object[]
                        {
                            new Message(),
                            Keys.Control | Keys.C
                        };
                        Require((bool)processShortcut.Invoke(overlay, passiveCopyArguments),
                            "Ctrl+C is handled during passive inspection");
                        Require(copiedHex == null, "colour copy waits for success flash");
                        using (Bitmap flashPreview = new Bitmap(
                            frame.Width,
                            frame.Height,
                            PixelFormat.Format32bppArgb))
                        {
                            overlay.DrawToBitmap(
                                flashPreview,
                                new Rectangle(0, 0, flashPreview.Width, flashPreview.Height));
                            string flashPath = Path.Combine(
                                Path.GetTempPath(),
                                "OpenCapture-success-flash-preview.png");
                            flashPreview.Save(flashPath, ImageFormat.Png);
                            Console.WriteLine("FLASH_PREVIEW=" + flashPath);
                        }
                        MethodInfo finishFlash = typeof(OverlayForm).GetMethod(
                            "FinishSuccessFlash",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        Require(finishFlash != null, "success-flash completion exists");
                        finishFlash.Invoke(overlay, new object[] { overlay, EventArgs.Empty });
                        Color corner = frame.GetPixelAtDesktopPoint(frame.DesktopBounds.Location);
                        string expectedHex = string.Format(
                            "#{0:X2}{1:X2}{2:X2}",
                            corner.R,
                            corner.G,
                            corner.B);
                        Require(copiedHex == expectedHex, "colour-copy command returns shown HEX");
                        Require(!selectionRaised, "colour-copy command produces no image selection");
                    }

                    using (ZoneDetector textDetector = ZoneDetector.Capture(
                        frame,
                        new List<ShellZone>()))
                    using (OverlayForm textOverlay = new OverlayForm(
                        frame,
                        textDetector,
                        testShortcuts))
                    {
                        Rectangle copiedTextSelection = Rectangle.Empty;
                        bool imageSelectionRaised = false;
                        textOverlay.TextCopyRequested += delegate(
                            object sender,
                            TextCopyRequestEventArgs eventArgs)
                        {
                            copiedTextSelection = eventArgs.Selection;
                        };
                        textOverlay.SelectionCompleted += delegate
                        {
                            imageSelectionRaised = true;
                        };
                        textOverlay.CreateControl();
                        Rectangle expectedTextSelection = new Rectangle(
                            frame.DesktopBounds.Left + 10,
                            frame.DesktopBounds.Top + 10,
                            160,
                            72);
                        MethodInfo finishTextFlash = typeof(OverlayForm).GetMethod(
                            "FinishSuccessFlash",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        FieldInfo manualSelectionField = typeof(OverlayForm).GetField(
                            "manualSelection",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        FieldInfo stateField = typeof(OverlayForm).GetField(
                            "state",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        Require(manualSelectionField != null && stateField != null,
                            "active drag state is testable");
                        manualSelectionField.SetValue(textOverlay, expectedTextSelection);
                        stateField.SetValue(
                            textOverlay,
                            Enum.Parse(stateField.FieldType, "Dragging"));
                        object[] activeCopyArguments = new object[]
                        {
                            new Message(),
                            Keys.Control | Keys.C
                        };
                        Require((bool)processShortcut.Invoke(textOverlay, activeCopyArguments),
                            "Ctrl+C is handled while moving a manual area");
                        Require(copiedTextSelection.IsEmpty,
                            "text copy waits for the success flash");
                        finishTextFlash.Invoke(
                            textOverlay,
                            new object[] { textOverlay, EventArgs.Empty });
                        Require(copiedTextSelection == expectedTextSelection,
                            "text copy returns the active manual area");
                        Require(!imageSelectionRaised,
                            "text copy does not enter the screenshot clipboard path");
                    }

                    TestRecordingOverlay(frame, warmedShellZones);
                }

                Console.WriteLine("PASS");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.ToString());
                return 1;
            }
        }

        private static void RunReviewTests()
        {
            PerformanceReviewTests.Run();
            TrayMenuReviewTests.Run();
            StorageReviewTests.Run();
            RecorderReviewTests.Run();
            VisualZoneReviewTests.Run();
            CaptureInteractionReviewTests.Run();
            ShortcutBinding longControl = ShortcutBinding.Keyboard(
                Keys.Control | Keys.Alt | Keys.Shift, Keys.LaunchApplication2);
            string originalText = "OpenCapture — Recording — " + longControl.DisplayText + " to stop";
            Require(originalText.Length > 63, "long recording shortcut exercises tray tooltip limit");
            using (NotifyIcon icon = new NotifyIcon())
            {
                icon.Text = OpenCaptureApplication.GetRecordingTrayTooltip(longControl);
                Require(icon.Text.Length == 63, "long recording shortcut fits the Windows tray tooltip");
                Require(icon.Text.StartsWith("OpenCapture — Recording — "),
                    "truncated recording tooltip keeps recording status");
                string defaultText = OpenCaptureApplication.GetRecordingTrayTooltip(
                    ShortcutSettings.DefaultRecordingControl);
                Require(defaultText == "OpenCapture — Recording — Space to stop",
                    "ordinary recording tooltip retains complete instruction");
            }
        }

        private static void TestCrashRecoveryPolicy()
        {
            int parentProcessId;
            int depth;
            Require(
                CrashRecovery.TryParseRecoveryArguments(
                    new string[]
                    {
                        "--recover-after=1234",
                        "--recovery-depth=2"
                    },
                    out parentProcessId,
                    out depth) &&
                parentProcessId == 1234 &&
                depth == 2,
                "crash recovery command preserves parent and retry depth");
            Require(
                CrashRecovery.IsRestartAllowed(0, false) &&
                CrashRecovery.IsRestartAllowed(2, false) &&
                !CrashRecovery.IsRestartAllowed(3, false),
                "crash recovery is bounded to three rapid attempts");
            Require(
                !CrashRecovery.IsRestartAllowed(0, true),
                "explicit Quit suppresses crash recovery");
        }

        private static void TestApplicationZones()
        {
            Point pointer = new Point(160, 140);
            IList<ApplicationZone> candidates = new ApplicationZone[]
            {
                new ApplicationZone(new Rectangle(20, 20, 900, 700), false),
                new ApplicationZone(new Rectangle(100, 100, 240, 120), false),
                new ApplicationZone(new Rectangle(130, 120, 100, 42), true),
                new ApplicationZone(new Rectangle(500, 500, 80, 40), true)
            };
            IList<ApplicationZone> matches =
                ApplicationZoneProvider.SelectCandidatesAt(candidates, pointer);

            Require(matches.Count == 3,
                "application-zone lookup keeps only regions below the pointer");
            Require(matches[0].Bounds == new Rectangle(130, 120, 100, 42),
                "small application control is preferred to enclosing panels");
            Require(matches[0].IsActionable,
                "application control preserves semantic actionability");
        }

        private static void Require(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Failed: " + name);
            }
            Console.WriteLine("OK " + name);
        }

        private static void TestBufferedOverlayText()
        {
            Bitmap source = new Bitmap(800, 500, PixelFormat.Format32bppRgb);
            using (Graphics graphics = Graphics.FromImage(source))
            using (Font sourceTextFont = new Font(
                FontFamily.GenericSansSerif,
                20.0f,
                FontStyle.Bold,
                GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.FromArgb(210, 226, 240));
                graphics.DrawString(
                    "LIVE OCR HIGHLIGHT",
                    sourceTextFont,
                    Brushes.Black,
                    new PointF(212.0f, 220.0f));
            }

            using (CapturedFrame frame = new CapturedFrame(
                source,
                new Rectangle(0, 0, source.Width, source.Height),
                0,
                DateTime.UtcNow))
            {
                source = null;
                ZoneDetector detector = ZoneDetector.Capture(
                    frame,
                    new List<ShellZone>());
                using (OverlayForm overlay = new OverlayForm(frame, detector))
                using (Bitmap rendered = new Bitmap(
                    frame.Width,
                    frame.Height,
                    PixelFormat.Format32bppArgb))
                {
                    overlay.CreateControl();
                    MethodInfo panelMethod = typeof(OverlayForm).GetMethod(
                        "GetPanelRectangle",
                        BindingFlags.Instance | BindingFlags.NonPublic,
                        null,
                        Type.EmptyTypes,
                        null);
                    Require(panelMethod != null, "precision panel geometry is available");
                    Rectangle compactPanel = (Rectangle)panelMethod.Invoke(overlay, null);
                    Require(compactPanel.Width == 250 && compactPanel.Height == 159,
                        "precision panel removes unused right-side space");
                    Rectangle panelSafeArea = new Rectangle(8, 8, 1584, 884);
                    Rectangle lowSelection = new Rectangle(28, 695, 900, 185);
                    Rectangle avoidedSelection = lowSelection;
                    avoidedSelection.Inflate(10, 10);
                    Rectangle repositionedPanel =
                        OverlayForm.ChoosePanelRectangle(
                            panelSafeArea,
                            new Point(30, 880),
                            lowSelection,
                            new Size(250, 159),
                            18,
                            10);
                    Require(
                        panelSafeArea.Contains(repositionedPanel) &&
                        !repositionedPanel.IntersectsWith(avoidedSelection),
                        "precision panel moves outside a selection near the screen edge");
                    Rectangle fullSelectionPanel =
                        OverlayForm.ChoosePanelRectangle(
                            panelSafeArea,
                            new Point(800, 450),
                            panelSafeArea,
                            new Size(250, 159),
                            18,
                            10);
                    Require(panelSafeArea.Contains(fullSelectionPanel),
                        "precision panel remains on-screen when overlap is unavoidable");
                    Rectangle monitorBounds = new Rectangle(0, 0, 1600, 900);
                    Rectangle workAreaBounds = new Rectangle(0, 0, 1600, 852);
                    Zone monitorZone = new Zone(
                        monitorBounds,
                        ZoneKind.Monitor,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        0);
                    Zone fullScreenWindow = new Zone(
                        monitorBounds,
                        ZoneKind.TopLevelWindow,
                        new IntPtr(1),
                        new IntPtr(1),
                        0);
                    Zone maximizedWindow = new Zone(
                        new Rectangle(0, 0, 1600, 852),
                        ZoneKind.TopLevelWindow,
                        new IntPtr(2),
                        new IntPtr(2),
                        0);
                    Require(
                        OverlayForm.IsWholeScreenZone(
                            monitorZone,
                            monitorBounds,
                            workAreaBounds) &&
                        OverlayForm.IsWholeScreenZone(
                            fullScreenWindow,
                            monitorBounds,
                            workAreaBounds) &&
                        OverlayForm.IsWholeScreenZone(
                            maximizedWindow,
                            monitorBounds,
                            workAreaBounds),
                        "monitor and taskbar-excluded full-screen zones are suppressed");
                    Zone ordinaryWindow = new Zone(
                        new Rectangle(140, 90, 1100, 650),
                        ZoneKind.TopLevelWindow,
                        new IntPtr(3),
                        new IntPtr(3),
                        0);
                    Require(
                        !OverlayForm.IsWholeScreenZone(
                            ordinaryWindow,
                            monitorBounds,
                            workAreaBounds),
                        "ordinary windows remain selectable");
                    Zone shadowedMaximizedWindow = new Zone(
                        new Rectangle(-8, 0, 1616, 852),
                        ZoneKind.TopLevelWindow,
                        new IntPtr(4),
                        new IntPtr(4),
                        0);
                    Zone nearlyMaximizedWindow = new Zone(
                        new Rectangle(20, 20, 1560, 812),
                        ZoneKind.TopLevelWindow,
                        new IntPtr(5),
                        new IntPtr(5),
                        0);
                    Require(
                        OverlayForm.IsWholeScreenZone(
                            shadowedMaximizedWindow,
                            monitorBounds,
                            workAreaBounds) &&
                        !OverlayForm.IsWholeScreenZone(
                            nearlyMaximizedWindow,
                            monitorBounds,
                            workAreaBounds),
                        "DWM-framed maximized zones are suppressed without hiding inset windows");
                    Rectangle cursorPanel = OverlayForm.ChoosePanelRectangle(
                        panelSafeArea,
                        new Point(700, 400),
                        Rectangle.Empty,
                        new Size(250, 159),
                        18,
                        10);
                    Require(
                        cursorPanel.Location == new Point(718, 418),
                        "precision panel returns beside the cursor without a zone");
                    Rectangle largeHoveredZone = new Rectangle(
                        100,
                        80,
                        1120,
                        680);
                    Rectangle interiorCursorPanel =
                        OverlayForm.ChoosePanelRectangle(
                            panelSafeArea,
                            new Point(700, 400),
                            largeHoveredZone,
                            new Size(250, 159),
                            18,
                            10,
                            true);
                    Require(
                        interiorCursorPanel.Location == new Point(718, 418),
                        "large zone uses normal panel position for an interior cursor");
                    Rectangle nearEdgeCursorPanel =
                        OverlayForm.ChoosePanelRectangle(
                            panelSafeArea,
                            new Point(112, 400),
                            largeHoveredZone,
                            new Size(250, 159),
                            18,
                            10,
                            true);
                    Require(
                        nearEdgeCursorPanel.Location != new Point(130, 418) &&
                        !nearEdgeCursorPanel.IntersectsWith(largeHoveredZone),
                        "zone edge keeps the precision panel outside");
                    Rectangle compactHoveredZone = new Rectangle(
                        450,
                        230,
                        400,
                        260);
                    Rectangle compactZonePanel =
                        OverlayForm.ChoosePanelRectangle(
                            panelSafeArea,
                            new Point(650, 360),
                            compactHoveredZone,
                            new Size(250, 159),
                            18,
                            10,
                            true);
                    Rectangle expandedCompactZone = compactHoveredZone;
                    expandedCompactZone.Inflate(10, 10);
                    Require(
                        !compactZonePanel.IntersectsWith(expandedCompactZone),
                        "normal panel position cannot cover too much of a compact zone");
                    long crosshairOverlayStyle =
                        NativeMethods.WS_EX_TRANSPARENT |
                        NativeMethods.WS_EX_LAYERED |
                        NativeMethods.WS_EX_NOACTIVATE;
                    Require(
                        ZoneDetector.IsTransparentOverlayStyle(
                            crosshairOverlayStyle) &&
                        !ZoneDetector.IsTransparentOverlayStyle(
                            NativeMethods.WS_EX_LAYERED) &&
                        !ZoneDetector.IsTransparentOverlayStyle(
                            NativeMethods.WS_EX_NOACTIVATE),
                        "click-through overlay windows are excluded from zones");
                    Require(
                        overlay.KeyboardSensitivityPercent == 100,
                        "keyboard cursor sensitivity resets to normal for each overlay");
                    Point initialPointer = overlay.PrecisionPointer;
                    Require(
                        overlay.MovePointerWithKeyboard(Keys.Right) &&
                        overlay.PrecisionPointer.X == initialPointer.X + 1,
                        "right arrow moves the precision cursor at normal sensitivity");
                    Require(
                        overlay.MovePointerWithKeyboard(Keys.S) &&
                        overlay.PrecisionPointer.Y == initialPointer.Y + 1,
                        "WASD moves the precision cursor");
                    overlay.AdjustKeyboardSensitivity(-120 * 2);
                    Require(overlay.KeyboardSensitivityPercent == 50,
                        "mouse wheel changes physical mouse sensitivity");
                    int beforeKeyboardMove = overlay.PrecisionPointer.X;
                    overlay.MovePointerWithKeyboard(Keys.D);
                    Require(overlay.PrecisionPointer.X == beforeKeyboardMove + 1,
                        "keyboard nudges remain one precise screen pixel");
                    int beforeScaledMouse = overlay.PrecisionPointer.X;
                    overlay.ApplyPhysicalMouseDelta(4, 0);
                    Require(overlay.PrecisionPointer.X == beforeScaledMouse + 2,
                        "lower sensitivity requires more physical mouse movement");
                    overlay.ResetMouseSensitivity();
                    Require(overlay.KeyboardSensitivityPercent == 100,
                        "middle-click sensitivity reset returns to normal");
                    overlay.DrawToBitmap(
                        rendered,
                        new Rectangle(0, 0, rendered.Width, rendered.Height));

                    Rectangle hexArea = new Rectangle(166, 30, 140, 24);
                    Rectangle detailsArea = new Rectangle(166, 95, 140, 70);
                    Require(CountDarkPixels(rendered, hexArea) > 8,
                        "buffered precision panel renders its HEX text");
                    int screenshotDetailPixels = CountDarkPixels(rendered, detailsArea);
                    Require(screenshotDetailPixels > 16,
                        "buffered precision panel renders dimensions and coordinates");

                    string previewPath = Path.Combine(
                        Path.GetTempPath(),
                        "OpenCapture-buffered-panel-preview.png");
                    rendered.Save(previewPath, ImageFormat.Png);
                    Console.WriteLine("BUFFERED_PANEL_PREVIEW=" + previewPath);

                    overlay.ToggleCaptureMode();
                    Rectangle recordingPanel = (Rectangle)panelMethod.Invoke(overlay, null);
                    Require(recordingPanel.Height == 173,
                        "recording panel adds storage without a start-control hint");
                    using (Bitmap recordingRendered = new Bitmap(
                        frame.Width,
                        frame.Height,
                        PixelFormat.Format32bppArgb))
                    {
                        overlay.DrawToBitmap(
                            recordingRendered,
                            new Rectangle(
                                0,
                                0,
                                recordingRendered.Width,
                                recordingRendered.Height));
                        Require(
                            CountDarkPixels(recordingRendered, detailsArea) >
                                screenshotDetailPixels + 8,
                            "recording panel adds current storage beneath its rate");
                        string recordingPreviewPath = Path.Combine(
                            Path.GetTempPath(),
                            "OpenCapture-buffered-recording-panel-preview.png");
                        recordingRendered.Save(recordingPreviewPath, ImageFormat.Png);
                        Console.WriteLine(
                            "RECORDING_PANEL_PREVIEW=" + recordingPreviewPath);
                    }
                }

                using (ZoneDetector liveTextDetector = ZoneDetector.Capture(
                    frame,
                    new List<ShellZone>()))
                using (OverlayForm liveTextOverlay = new OverlayForm(
                    frame,
                    liveTextDetector))
                using (Bitmap liveTextRendered = new Bitmap(
                    frame.Width,
                    frame.Height,
                    PixelFormat.Format32bppArgb))
                {
                    liveTextOverlay.CreateControl();
                    Rectangle selection = new Rectangle(200, 200, 360, 100);
                    OcrRecognition recognition = new OcrRecognition(
                        "LIVE OCR HIGHLIGHT",
                        new List<OcrTextRegion>
                        {
                            new OcrTextRegion(
                                "LIVE OCR HIGHLIGHT",
                                new RectangleF(12, 20, 190, 28),
                                0)
                        }.AsReadOnly());
                    double animationDuration =
                        OverlayForm.CalculateLiveOcrAnimationDurationMilliseconds(
                            recognition.Regions);
                    Require(animationDuration >= 6500.0,
                        "live text sweep is deliberately slow and smooth");
                    FieldInfo stateField = typeof(OverlayForm).GetField(
                        "state",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    FieldInfo manualSelectionField = typeof(OverlayForm).GetField(
                        "manualSelection",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    FieldInfo liveRecognitionField = typeof(OverlayForm).GetField(
                        "liveOcrRecognition",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    FieldInfo liveAnimationStartedField = typeof(OverlayForm).GetField(
                        "liveOcrAnimationStartedTimestamp",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    FieldInfo liveAnimationTimerField = typeof(OverlayForm).GetField(
                        "liveOcrAnimationTimer",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    MethodInfo applyLiveResult = typeof(OverlayForm).GetMethod(
                        "ApplyLiveOcrResult",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    MethodInfo panelMethod = typeof(OverlayForm).GetMethod(
                        "GetPanelRectangle",
                        BindingFlags.Instance | BindingFlags.NonPublic,
                        null,
                        Type.EmptyTypes,
                        null);
                    MethodInfo ocrPanelMethod = typeof(OverlayForm).GetMethod(
                        "GetOcrPreviewPanelRectangle",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Require(
                        stateField != null && manualSelectionField != null &&
                        liveRecognitionField != null && applyLiveResult != null &&
                        liveAnimationStartedField != null &&
                        liveAnimationTimerField != null,
                        "live OCR overlay state is available");
                    Require(ocrPanelMethod == null,
                        "live OCR text preview panel is removed");
                    stateField.SetValue(
                        liveTextOverlay,
                        Enum.Parse(stateField.FieldType, "Dragging"));
                    manualSelectionField.SetValue(liveTextOverlay, selection);
                    applyLiveResult.Invoke(
                        liveTextOverlay,
                        new object[] { 0, selection, recognition });
                    Rectangle livePanel = (Rectangle)panelMethod.Invoke(
                        liveTextOverlay,
                        null);
                    Require(livePanel.Height == 159,
                        "live OCR keeps the precision panel compact");
                    Require(
                        (long)liveAnimationStartedField.GetValue(liveTextOverlay) > 0 &&
                        ((System.Windows.Forms.Timer)liveAnimationTimerField.GetValue(
                            liveTextOverlay)).Enabled,
                        "text highlight animation starts with the first OCR result");
                    long firstAnimationStart =
                        (long)liveAnimationStartedField.GetValue(liveTextOverlay);
                    applyLiveResult.Invoke(
                        liveTextOverlay,
                        new object[] { 0, selection, recognition });
                    Require(
                        (long)liveAnimationStartedField.GetValue(liveTextOverlay) ==
                            firstAnimationStart,
                        "continuous OCR results do not restart the highlight sweep");
                    liveTextOverlay.DrawToBitmap(
                        liveTextRendered,
                        new Rectangle(
                            0,
                            0,
                            liveTextRendered.Width,
                            liveTextRendered.Height));
                    Color highlighted = liveTextRendered.GetPixel(350, 245);
                    Color selectionOnly = liveTextRendered.GetPixel(500, 230);
                    Require(highlighted.ToArgb() != selectionOnly.ToArgb(),
                        "recognised screen text receives a distinct flowing highlight");
                    int highlightContrast =
                        Math.Abs(highlighted.R - selectionOnly.R) +
                        Math.Abs(highlighted.G - selectionOnly.G) +
                        Math.Abs(highlighted.B - selectionOnly.B);
                    Require(highlightContrast >= 35,
                        "recognised text highlight remains clearly visible");
                    Require(
                        MinimumLuminance(
                            liveTextRendered,
                            new Rectangle(212, 220, 190, 28)) < 90,
                        "moving sweep increases the underlying text contrast");
                    string livePreviewPath = Path.Combine(
                        Path.GetTempPath(),
                        "OpenCapture-live-ocr-preview.png");
                    liveTextRendered.Save(livePreviewPath, ImageFormat.Png);
                    Console.WriteLine("LIVE_OCR_PREVIEW=" + livePreviewPath);

                    MethodInfo completeSelection = typeof(OverlayForm).GetMethod(
                        "CompleteSelection",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    completeSelection.Invoke(liveTextOverlay, new object[] { selection });
                    Require(liveRecognitionField.GetValue(liveTextOverlay) == null,
                        "normal screenshot capture removes the live OCR highlight");
                }

                using (ZoneDetector movingTextDetector = ZoneDetector.Capture(
                    frame,
                    new List<ShellZone>()))
                using (OverlayForm movingTextOverlay = new OverlayForm(
                    frame,
                    movingTextDetector))
                {
                    movingTextOverlay.CreateControl();
                    FieldInfo stateField = typeof(OverlayForm).GetField(
                        "state",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    FieldInfo manualSelectionField = typeof(OverlayForm).GetField(
                        "manualSelection",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    FieldInfo requestedBoundsField = typeof(OverlayForm).GetField(
                        "liveOcrRequestedBounds",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    FieldInfo attemptedBoundsField = typeof(OverlayForm).GetField(
                        "liveOcrLastAttemptedBounds",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    MethodInfo trackSelection = typeof(OverlayForm).GetMethod(
                        "TrackLiveOcrSelection",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Require(
                        stateField != null && manualSelectionField != null &&
                        requestedBoundsField != null &&
                        attemptedBoundsField != null && trackSelection != null,
                        "continuous OCR scheduling state is available");
                    stateField.SetValue(
                        movingTextOverlay,
                        Enum.Parse(stateField.FieldType, "Dragging"));
                    Rectangle firstMovingArea = new Rectangle(40, 40, 120, 42);
                    manualSelectionField.SetValue(
                        movingTextOverlay,
                        firstMovingArea);
                    trackSelection.Invoke(
                        movingTextOverlay,
                        new object[] { firstMovingArea });
                    Require(
                        (Rectangle)attemptedBoundsField.GetValue(movingTextOverlay) ==
                            firstMovingArea,
                        "OCR begins directly while the selection is moving");
                    Rectangle expandedMovingArea = new Rectangle(40, 40, 220, 70);
                    manualSelectionField.SetValue(
                        movingTextOverlay,
                        expandedMovingArea);
                    trackSelection.Invoke(
                        movingTextOverlay,
                        new object[] { expandedMovingArea });
                    Require(
                        (Rectangle)requestedBoundsField.GetValue(movingTextOverlay) ==
                            expandedMovingArea,
                        "the newest moving area is queued during an active scan");
                }
            }
            if (source != null)
            {
                source.Dispose();
            }
        }

        private static void TestStorageEstimates()
        {
            long screenshot = OverlayForm.EstimateScreenshotStorageBytes(1920, 1080);
            long recording = OverlayForm.EstimateRecordingStorageBytesPerMinute(1920, 1080);
            Require(screenshot > 512 * 1024 && screenshot < 5L * 1024 * 1024,
                "screenshot storage estimate is plausible");
            Require(recording > 500L * 1024 * 1024,
                "60 FPS recording storage estimate reflects MJPEG output");
            Require(
                OverlayForm.FormatEstimatedStorage(screenshot).EndsWith("MB"),
                "screenshot estimate is formatted in readable units");
            Require(
                OverlayForm.FormatEstimatedStorage(recording).EndsWith("GB"),
                "recording estimate is formatted in readable units");
            Require(OverlayForm.FormatCurrentStorage(0) == "0 KB",
                "recording storage begins at zero kilobytes");
        }

        private static int CountDarkPixels(Bitmap bitmap, Rectangle bounds)
        {
            Rectangle clipped = Rectangle.Intersect(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                bounds);
            int count = 0;
            int y;
            for (y = clipped.Top; y < clipped.Bottom; y++)
            {
                int x;
                for (x = clipped.Left; x < clipped.Right; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    if (pixel.R < 95 && pixel.G < 95 && pixel.B < 95)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private static int MinimumLuminance(Bitmap bitmap, Rectangle bounds)
        {
            Rectangle clipped = Rectangle.Intersect(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                bounds);
            int minimum = 255;
            int y;
            for (y = clipped.Top; y < clipped.Bottom; y++)
            {
                int x;
                for (x = clipped.Left; x < clipped.Right; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    int luminance = (pixel.R * 299 + pixel.G * 587 +
                        pixel.B * 114) / 1000;
                    minimum = Math.Min(minimum, luminance);
                }
            }
            return minimum;
        }

        private static void TestHdrDisplayCompensation()
        {
            Require(HdrDesktopCapture.ScRgbToSrgbByte(0.0f) == 0,
                "FP16 tone mapping anchors black");
            Require(HdrDesktopCapture.ScRgbToSrgbByte(1.0f) == 255,
                "FP16 tone mapping anchors SDR white");
            Require(
                Math.Abs(HdrDesktopCapture.ScRgbToSrgbByte(
                    (float)SrgbToLinear(128.0 / 255.0)) - 128) <= 1,
                "FP16 linear-light conversion restores an SDR midtone");
            Require(HdrDesktopCapture.ScRgbToSrgbByte(4.0f) == 255,
                "FP16 HDR highlights map safely to SDR white");

            IList<HdrMonitorAdjustment> liveAdjustments =
                HdrDisplayCompensation.DiscoverActiveAdjustments();
            Require(liveAdjustments != null,
                "per-display HDR discovery completes");
            foreach (HdrMonitorAdjustment adjustment in liveAdjustments)
            {
                Require(adjustment.Bounds.Width > 0 && adjustment.Bounds.Height > 0,
                    "live HDR display has physical bounds");
                Require(adjustment.SdrWhiteLevel > 0,
                    "live HDR display has a valid SDR reference white");
                Console.WriteLine(
                    "HDR_DISPLAY={0} BOUNDS={1} SDR_WHITE={2:0.##}nits MULTIPLIER={3:0.###}",
                    adjustment.DeviceName,
                    adjustment.Bounds,
                    adjustment.SdrWhiteLevel * 0.08,
                    adjustment.WhiteLevelMultiplier);
            }
            if (liveAdjustments.Count > 0)
            {
                using (HdrDesktopCapture.RecordingSession recordingSession =
                    HdrDesktopCapture.CreateRecordingSession(
                        liveAdjustments[0].Bounds))
                {
                    Require(
                        recordingSession.ActiveOutputCount > 0,
                        "continuous recorder opens native FP16 HDR duplication");
                }
            }
        }

        private static double SrgbToLinear(double encoded)
        {
            return encoded <= 0.04045
                ? encoded / 12.92
                : Math.Pow((encoded + 0.055) / 1.055, 2.4);
        }

        private static void TestThemeShortcutsAndMenus()
        {
            using (ShellChangeWatcher watcher = new ShellChangeWatcher())
            {
                Require(watcher.IsOperational,
                    "lightweight shell change notifications are available");
                watcher.MarkChanged();
                watcher.MarkChanged();
                Require(watcher.ConsumeChanged(),
                    "shell changes coalesce into one cache refresh");
                Require(!watcher.ConsumeChanged(),
                    "shell change invalidation clears after consumption");
            }

            using (Font font = OpenCaptureTheme.CreateFont(12.0f, FontStyle.Regular))
            {
                Require(
                    font.FontFamily.Name.IndexOf("Proxima", StringComparison.OrdinalIgnoreCase) >= 0,
                    "embedded Proxima Nova font loads");
            }

            using (Bitmap startup = OpenCaptureTheme.LoadBitmap(
                OpenCaptureTheme.StartupImageResourceName))
            using (Bitmap trayIcon = OpenCaptureTheme.LoadBitmap(
                OpenCaptureTheme.TrayIconResourceName))
            {
                Require(startup != null && startup.Width == 1063 && startup.Height == 844,
                    "startup artwork is embedded");
                Require(trayIcon != null && trayIcon.Width > 0 && trayIcon.Height > 0,
                    "tray artwork is embedded");
            }

            ShortcutBinding parsed;
            Require(
                ShortcutBinding.TryParse(
                    ShortcutSettings.DefaultCapture.Serialize(),
                    out parsed) && parsed.Equals(ShortcutSettings.DefaultCapture),
                "capture shortcut round-trips");
            Require(
                ShortcutSettings.DefaultCopyColour.Matches(Keys.Control | Keys.C),
                "copy-colour shortcut matches");
            Require(
                ShortcutSettings.DefaultCaptureZone.Matches(MouseButtons.Left),
                "tap shortcut matches");
            Require(
                ShortcutSettings.DefaultRecording.Equals(ShortcutSettings.DefaultCapture),
                "recording shortcut preserves the existing default");
            Require(
                ShortcutSettings.DefaultRecordingControl.Matches(Keys.Space),
                "recording start/stop control defaults to Space");
            Require(
                !OpenCaptureApplication.ShouldActivateGlobalShortcuts(true) &&
                OpenCaptureApplication.ShouldActivateGlobalShortcuts(false),
                "all global shortcuts stay suspended until the keybind editor closes");
            ShortcutSettings alwaysRecording = new ShortcutSettings(
                ShortcutSettings.DefaultCapture,
                ShortcutSettings.DefaultCopyColour,
                ShortcutSettings.DefaultCaptureZone,
                ShortcutBinding.Keyboard(Keys.Control | Keys.Shift, Keys.R),
                ShortcutBinding.Keyboard(Keys.None, Keys.F8),
                RecordingShortcutAvailability.AlwaysAvailable);
            Require(
                alwaysRecording.RecordingShortcut.Matches(Keys.Control | Keys.Shift | Keys.R) &&
                alwaysRecording.RecordingControlShortcut.Matches(Keys.F8) &&
                alwaysRecording.RecordingAvailability == RecordingShortcutAvailability.AlwaysAvailable,
                "recording mode, start/stop, and availability are configurable");

            using (Bitmap clipboardImage = new Bitmap(3, 2, PixelFormat.Format32bppRgb))
            {
                DataObject clipboardData = CaptureEngine.CreateScreenshotClipboardData(clipboardImage);
                Require(
                    clipboardData.GetDataPresent(CaptureEngine.ClipboardScreenshotFormat),
                    "screenshot clipboard data carries the private marker");
                Require(
                    clipboardData.GetDataPresent(DataFormats.Bitmap),
                    "screenshot clipboard data carries the image");
            }

            string clipboardVideo = Path.Combine(Path.GetTempPath(), "OpenCapture clipboard test.avi");
            DataObject recordingClipboardData =
                CaptureEngine.CreateRecordingClipboardData(clipboardVideo);
            Require(
                recordingClipboardData.GetDataPresent(CaptureEngine.ClipboardRecordingFormat),
                "recording clipboard data carries the private marker");
            Require(
                recordingClipboardData.GetDataPresent(DataFormats.FileDrop) &&
                recordingClipboardData.GetFileDropList().Count == 1 &&
                string.Equals(
                    recordingClipboardData.GetFileDropList()[0],
                    Path.GetFullPath(clipboardVideo),
                    StringComparison.OrdinalIgnoreCase),
                "recording clipboard data carries the completed video file");

            string trayPreviewPath = Path.Combine(
                Path.GetTempPath(),
                "OpenCapture-tray-menu-preview.png");
            using (TrayMenuForm menu = new TrayMenuForm())
            using (Bitmap preview = new Bitmap(menu.Width, menu.Height, PixelFormat.Format32bppArgb))
            {
                menu.AutostartEnabled = true;
                menu.CapturesClearable = true;
                menu.RecordingsClearable = false;
                menu.ClipboardClearable = false;
                menu.CreateControl();
                menu.DrawToBitmap(preview, new Rectangle(0, 0, preview.Width, preview.Height));
                preview.Save(trayPreviewPath, ImageFormat.Png);
            }
            Console.WriteLine("TRAY_PREVIEW=" + trayPreviewPath);

            string startupPreviewPath = Path.Combine(
                Path.GetTempPath(),
                "OpenCapture-startup-window-preview.png");
            ShortcutSettings resetApplied = null;
            using (StartupWindowForm startupWindow = new StartupWindowForm(
                new ShortcutSettings(
                    ShortcutSettings.DefaultCapture,
                    ShortcutSettings.DefaultCopyColour,
                    ShortcutSettings.DefaultCaptureZone,
                    ShortcutSettings.DefaultRecording,
                    ShortcutBinding.Keyboard(Keys.None, Keys.F8),
                    ShortcutSettings.DefaultRecordingAvailability),
                delegate(ShortcutSettings candidate)
                {
                    resetApplied = candidate;
                    return null;
                }))
            using (Bitmap preview = new Bitmap(795, 633, PixelFormat.Format32bppArgb))
            {
                startupWindow.CreateControl();
                MethodInfo bindingBoundsMethod = typeof(StartupWindowForm).GetMethod(
                    "GetBindingBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Require(bindingBoundsMethod != null, "startup shortcut layout is available");
                Rectangle firstBinding = (Rectangle)bindingBoundsMethod.Invoke(
                    startupWindow,
                    new object[] { 0 });
                int bindingIndex;
                for (bindingIndex = 1; bindingIndex < 5; bindingIndex++)
                {
                    Rectangle bindingBounds = (Rectangle)bindingBoundsMethod.Invoke(
                        startupWindow,
                        new object[] { bindingIndex });
                    Require(
                        bindingBounds.Left == firstBinding.Left &&
                        bindingBounds.Width == firstBinding.Width,
                        "startup shortcut controls share one alignment");
                }
                ComboBox availability = null;
                foreach (Control control in startupWindow.Controls)
                {
                    availability = control as ComboBox;
                    if (availability != null)
                    {
                        break;
                    }
                }
                Require(
                    availability != null && availability.Items.Count == 2,
                    "startup window includes recording availability dropdown");
                Require(
                    availability.Items[0].ToString() == "After Alt + Q" &&
                    availability.Items[1].ToString() == "Always Available",
                    "recording availability choices use the current capture shortcut");
                MethodInfo resetMethod = typeof(StartupWindowForm).GetMethod(
                    "ResetKeybinds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo resetBoundsMethod = typeof(StartupWindowForm).GetMethod(
                    "GetResetBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Require(resetMethod != null && resetBoundsMethod != null,
                    "startup window includes a reset keybinds button");
                Rectangle resetBounds = (Rectangle)resetBoundsMethod.Invoke(
                    startupWindow,
                    null);
                Require(resetBounds.Bottom <= startupWindow.ClientSize.Height,
                    "reset keybinds button stays inside the startup window");
                resetMethod.Invoke(startupWindow, null);
                Require(
                    resetApplied != null &&
                    resetApplied.CaptureShortcut.Equals(ShortcutSettings.DefaultCapture) &&
                    resetApplied.CopyColourShortcut.Equals(ShortcutSettings.DefaultCopyColour) &&
                    resetApplied.CaptureZoneShortcut.Equals(ShortcutSettings.DefaultCaptureZone) &&
                    resetApplied.RecordingShortcut.Equals(ShortcutSettings.DefaultRecording) &&
                    resetApplied.RecordingControlShortcut.Equals(
                        ShortcutSettings.DefaultRecordingControl) &&
                    resetApplied.RecordingAvailability ==
                        ShortcutSettings.DefaultRecordingAvailability,
                    "reset keybinds restores every shortcut default");
                startupWindow.Show();
                Application.DoEvents();
                startupWindow.DrawToBitmap(
                    preview,
                    new Rectangle(0, 0, preview.Width, preview.Height));
                startupWindow.Hide();
                preview.Save(startupPreviewPath, ImageFormat.Png);
            }
            Console.WriteLine("STARTUP_PREVIEW=" + startupPreviewPath);
        }

        private static void TestCaptureFileNames()
        {
            Require(
                CaptureFileStore.IsScreenshotFileName("14-05-06 --- 03-02-26.png"),
                "timestamp screenshot name is recognized");
            Require(
                CaptureFileStore.IsScreenshotFileName("14-05-06.789 --- 03-02-26.png"),
                "millisecond screenshot name is recognized");
            Require(
                CaptureFileStore.IsScreenshotFileName("OpenCapture_2026-08-19_18-05-47-798.png"),
                "legacy screenshot name is recognized for cleanup");
            Require(
                !CaptureFileStore.IsScreenshotFileName("notes.png"),
                "unrelated PNG is protected from cleanup");
            Require(
                !CaptureFileStore.IsScreenshotFileName("OpenCapture-notes.png"),
                "similarly named unrelated PNG is protected from cleanup");

            string directory = Path.Combine(
                Path.GetTempPath(),
                "OpenCapture-name-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                DateTime capturedAt = new DateTime(2026, 2, 3, 14, 5, 6, 789);
                string first = CaptureFileStore.GetUniqueCapturePath(directory, capturedAt);
                Require(
                    Path.GetFileName(first) == "14-05-06 --- 03-02-26.png",
                    "filename omits milliseconds when unnecessary");
                File.WriteAllBytes(first, new byte[] { 1 });
                string collision = CaptureFileStore.GetUniqueCapturePath(directory, capturedAt);
                Require(
                    Path.GetFileName(collision) == "14-05-06.789 --- 03-02-26.png",
                    "filename adds milliseconds only for a collision");

                File.WriteAllBytes(collision, new byte[] { 2 });
                File.WriteAllBytes(Path.Combine(directory, "notes.png"), new byte[] { 3 });
                string nested = Path.Combine(directory, "archive");
                Directory.CreateDirectory(nested);
                File.WriteAllBytes(
                    Path.Combine(nested, "15-05-06 --- 03-02-26.png"),
                    new byte[] { 4 });
                IList<string> clearable = CaptureFileStore.GetScreenshotFiles(directory);
                Require(clearable.Count == 2,
                    "folder cleanup selects only top-level OpenCapture screenshots");
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }

            string ownershipDirectory = Path.Combine(
                Path.GetTempPath(),
                "OpenCapture-ownership-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(ownershipDirectory);
                Require(
                    CaptureFileStore.GetScreenshotFiles(ownershipDirectory).Count == 0,
                    "an empty capture folder is not clearable");

                string managed = Path.Combine(
                    ownershipDirectory,
                    "09-08-07 --- 06-05-26.png");
                File.WriteAllBytes(managed, new byte[] { 1, 2, 3, 4 });
                Require(
                    CaptureFileStore.GetScreenshotFiles(ownershipDirectory).Count == 0,
                    "a timestamp-shaped file is not adopted after migration");

                CaptureFileStore.RegisterScreenshot(ownershipDirectory, managed);
                Require(
                    CaptureFileStore.GetScreenshotFiles(ownershipDirectory).Count == 1,
                    "a completed OpenCapture save enables folder cleanup");

                string renamed = Path.Combine(ownershipDirectory, "keep forever.png");
                File.Move(managed, renamed);
                CaptureFileStore.MarkScreenshotRenamed(
                    ownershipDirectory,
                    managed,
                    renamed);
                Require(
                    CaptureFileStore.GetScreenshotFiles(ownershipDirectory).Count == 0,
                    "a renamed screenshot is excluded from cleanup");

                File.Move(renamed, managed);
                Require(
                    CaptureFileStore.GetScreenshotFiles(ownershipDirectory).Count == 0,
                    "rename immunity persists after the original name is restored");

                string laterCapture = Path.Combine(
                    ownershipDirectory,
                    "10-08-07 --- 06-05-26.png");
                File.WriteAllBytes(laterCapture, new byte[] { 5, 6, 7, 8 });
                CaptureFileStore.RegisterScreenshot(ownershipDirectory, laterCapture);
                Require(
                    CaptureFileStore.GetScreenshotFiles(ownershipDirectory).Count == 1,
                    "new captures remain clearable beside immune files");

                File.AppendAllText(laterCapture, "changed");
                Require(
                    CaptureFileStore.GetScreenshotFiles(ownershipDirectory).Count == 0,
                    "a modified capture is protected as a replaced file");
            }
            finally
            {
                if (Directory.Exists(ownershipDirectory))
                {
                    Directory.Delete(ownershipDirectory, true);
                }
            }
        }

        private static void TestOwnershipSaveCleanup()
        {
            foreach (string extension in new string[] { ".png", ".avi" })
            {
                string directory = Path.Combine(
                    Path.GetTempPath(), "OpenCapture-cleanup-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string indexPath = Path.Combine(directory, ".ownership");
                string temporaryPath = indexPath + ".locked.tmp";
                try
                {
                    ManagedMediaFileStore store = new ManagedMediaFileStore(
                        directory, ".ownership", "test ownership", "*" + extension,
                        delegate(string name) { return true; });
                    store.GetClearableFiles();
                    string mediaPath = Path.Combine(directory, "capture" + extension);
                    File.WriteAllText(mediaPath, "capture data");
                    store.Register(mediaPath);
                    string savedIndex = File.ReadAllText(indexPath);
                    File.WriteAllText(temporaryPath, savedIndex);

                    using (FileStream held = new FileStream(
                        temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        ManagedMediaFileStore.FinishOwnershipSave(
                            indexPath, temporaryPath, FileAttributes.Normal);
                        Require(File.Exists(temporaryPath), "locked temporary file is safely left behind " + extension);
                        Require(File.ReadAllText(indexPath) == savedIndex, "cleanup preserves the saved index " + extension);
                        Require(store.GetClearableFiles().Count == 1, "ownership loads with a locked leftover " + extension);
                    }
                    ManagedMediaFileStore.FinishOwnershipSave(indexPath, temporaryPath, FileAttributes.ReadOnly);
                    Require(!File.Exists(temporaryPath), "unlocked temporary file is removed " + extension);
                    Require((File.GetAttributes(indexPath) & (FileAttributes.Hidden | FileAttributes.ReadOnly)) ==
                        (FileAttributes.Hidden | FileAttributes.ReadOnly), "index attributes are restored " + extension);
                    File.SetAttributes(indexPath, FileAttributes.Hidden);

                    File.WriteAllText(temporaryPath, savedIndex);
                    File.SetAttributes(temporaryPath, FileAttributes.ReadOnly);
                    ManagedMediaFileStore.FinishOwnershipSave(indexPath, temporaryPath, FileAttributes.Normal);
                    Require(File.Exists(temporaryPath), "read-only temporary file does not fail cleanup " + extension);
                    File.SetAttributes(temporaryPath, FileAttributes.Normal);
                    ManagedMediaFileStore.FinishOwnershipSave(indexPath, temporaryPath, FileAttributes.Normal);
                    ManagedMediaFileStore.FinishOwnershipSave(indexPath, temporaryPath, FileAttributes.Normal);

                    string laterPath = Path.Combine(directory, "later" + extension);
                    File.WriteAllText(laterPath, "later capture");
                    bool saveFailed = false;
                    using (FileStream held = new FileStream(
                        indexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        try { store.Register(laterPath); }
                        catch (IOException) { saveFailed = true; }
                    }
                    Require(saveFailed, "an actual ownership write failure is still reported " + extension);
                    Require(File.ReadAllText(indexPath) == savedIndex, "failed save preserves existing ownership " + extension);
                    store.Register(laterPath);
                    Require(store.GetClearableFiles().Count == 2, "ownership saves recover after unlock " + extension);
                }
                finally
                {
                    foreach (string path in Directory.GetFiles(directory))
                    {
                        File.SetAttributes(path, FileAttributes.Normal);
                    }
                    Directory.Delete(directory, true);
                }
            }
        }

        private static void TestCopiedTextFileNames()
        {
            Require(
                CopiedTextFileStore.MakeSafeBaseName("Hello\r\nworld: test?") ==
                    "Hello world test",
                "OCR image name uses recognised text safely");
            Require(
                CopiedTextFileStore.MakeSafeBaseName("CON") == "CON text",
                "reserved Windows filenames are made safe");

            string directory = Path.Combine(
                Path.GetTempPath(),
                "OpenCapture-copied-text-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string first = CopiedTextFileStore.GetUniquePath(
                    directory,
                    "Copied words");
                Require(Path.GetFileName(first) == "Copied words.png",
                    "copied-text image filename is the recognised text");
                File.WriteAllBytes(first, new byte[] { 1 });
                string collision = CopiedTextFileStore.GetUniquePath(
                    directory,
                    "Copied words");
                Require(Path.GetFileName(collision) == "Copied words (2).png",
                    "copied-text image names stay unique");
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        private static void TestWindowsOcr()
        {
            DataObject textPayload = OpenCaptureApplication.CreateTextClipboardData(
                "OPEN CAPTURE 42");
            Require(textPayload.GetDataPresent(DataFormats.UnicodeText),
                "OCR clipboard payload contains text");
            Require(!textPayload.GetDataPresent(DataFormats.Bitmap),
                "OCR clipboard payload contains no image");
            Require(WindowsOcrEngine.IsAvailable(),
                "Windows local OCR is available");
            IList<string> ocrLanguages = WindowsOcrEngine.GetAvailableLanguageTags();
            Require(ocrLanguages.Count > 0,
                "installed OCR languages are discovered");
            Console.WriteLine("OCR_LANGUAGES=" + string.Join(",", ocrLanguages));
            Require(
                WindowsOcrEngine.NormalizeRecognizedText("多 语 言 测 试 2026") ==
                    "多语言测试 2026",
                "artificial spacing is removed from compact scripts");
            string simplifiedChinese =
                WindowsOcrEngine.ConvertTraditionalChineseToSimplified(
                    "臺灣資訊軟體");
            Console.WriteLine("OCR_TRADITIONAL_CONVERSION=" + simplifiedChinese);
            Require(
                simplifiedChinese == "台湾资讯软体",
                "Traditional Chinese is converted to Simplified Chinese");
            using (Bitmap image = new Bitmap(900, 170, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(image))
            using (Font font = new Font(
                FontFamily.GenericSansSerif,
                54.0f,
                FontStyle.Bold,
                GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                graphics.TextRenderingHint =
                    System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                graphics.DrawString(
                    "OPEN CAPTURE 42",
                    font,
                    Brushes.Black,
                    new PointF(20.0f, 42.0f));
                Stopwatch timer = Stopwatch.StartNew();
                OcrRecognition detailed = WindowsOcrEngine.RecognizeDetailed(image);
                string recognised = detailed.Text;
                timer.Stop();
                Console.WriteLine(
                    "OCR_MS={0} OCR_TEXT={1}",
                    timer.ElapsedMilliseconds,
                    recognised.Replace(Environment.NewLine, " | "));
                Require(
                    recognised.IndexOf("OPEN", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    recognised.IndexOf("42", StringComparison.OrdinalIgnoreCase) >= 0,
                    "Windows OCR recognises the selected text");
                Require(
                    detailed.Regions.Count > 0 &&
                    detailed.Regions[0].Bounds.Left >= 0.0f &&
                    detailed.Regions[0].Bounds.Right <= image.Width + 1.0f,
                    "OCR returns line geometry for the flowing screen highlight");
                Require(timer.ElapsedMilliseconds < 3000,
                    "Windows OCR completes quickly");
            }

            using (Bitmap lowContrast = new Bitmap(
                420,
                88,
                PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(lowContrast))
            using (Font font = new Font(
                FontFamily.GenericSansSerif,
                18.0f,
                FontStyle.Bold,
                GraphicsUnit.Pixel))
            using (Brush textBrush = new SolidBrush(Color.FromArgb(145, 155, 165)))
            {
                graphics.Clear(Color.FromArgb(210, 220, 230));
                graphics.TextRenderingHint =
                    System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                graphics.DrawString(
                    "SMALL TEXT 73",
                    font,
                    textBrush,
                    new PointF(12.0f, 27.0f));
                string recognised = WindowsOcrEngine.Recognize(lowContrast);
                Console.WriteLine("OCR_LOW_CONTRAST=" + recognised);
                Require(
                    recognised.IndexOf("SMALL", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    recognised.IndexOf("73", StringComparison.OrdinalIgnoreCase) >= 0,
                    "OCR recognises small low-contrast text");
            }

            using (Bitmap inverted = new Bitmap(
                520,
                100,
                PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(inverted))
            using (Font font = new Font(
                FontFamily.GenericSansSerif,
                23.0f,
                FontStyle.Bold,
                GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.FromArgb(24, 30, 38));
                graphics.TextRenderingHint =
                    System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                graphics.DrawString(
                    "BRIGHT WORDS 81",
                    font,
                    Brushes.White,
                    new PointF(14.0f, 32.0f));
                string recognised = WindowsOcrEngine.Recognize(inverted);
                Console.WriteLine("OCR_INVERTED=" + recognised);
                Require(
                    recognised.IndexOf("BRIGHT", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    recognised.IndexOf("81", StringComparison.OrdinalIgnoreCase) >= 0,
                    "OCR recognises light text on a dark surface");
            }

            bool chineseAvailable = false;
            foreach (string languageTag in ocrLanguages)
            {
                if (languageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                {
                    chineseAvailable = true;
                    break;
                }
            }
            if (chineseAvailable)
            {
                using (Bitmap chinese = new Bitmap(
                    700,
                    150,
                    PixelFormat.Format32bppArgb))
                using (Graphics graphics = Graphics.FromImage(chinese))
                using (Font font = new Font(
                    "Microsoft YaHei UI",
                    46.0f,
                    FontStyle.Regular,
                    GraphicsUnit.Pixel))
                {
                    graphics.Clear(Color.White);
                    graphics.TextRenderingHint =
                        System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    graphics.DrawString(
                        "多语言测试 2026",
                        font,
                        Brushes.Black,
                        new PointF(18.0f, 42.0f));
                    string recognised = WindowsOcrEngine.Recognize(chinese);
                    Console.WriteLine("OCR_CHINESE=" + recognised);
                    Require(
                        recognised.IndexOf("2026", StringComparison.Ordinal) >= 0 &&
                        recognised.IndexOf("语言测试", StringComparison.Ordinal) >= 0,
                        "OCR uses an installed non-Latin language pack");
                }
            }
        }

        private static void TestVideoFilesAndRecorder()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "OpenCapture-video-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                DateTime startedAt = new DateTime(2026, 2, 3, 14, 5, 6, 789);
                string first = VideoFileStore.GetUniqueVideoPath(directory, startedAt);
                Require(
                    Path.GetFileName(first) == "14-05-06 --- 03-02-26.avi",
                    "video filename omits milliseconds when unnecessary");
                File.WriteAllBytes(first, new byte[] { 1 });
                string collision = VideoFileStore.GetUniqueVideoPath(directory, startedAt);
                Require(
                    Path.GetFileName(collision) == "14-05-06.789 --- 03-02-26.avi",
                    "video filename adds milliseconds only for a collision");

                string writerPath = Path.Combine(directory, "writer.avi");
                byte[] jpeg;
                using (Bitmap image = new Bitmap(16, 12, PixelFormat.Format24bppRgb))
                using (Graphics graphics = Graphics.FromImage(image))
                using (MemoryStream encoded = new MemoryStream())
                {
                    graphics.Clear(Color.FromArgb(45, 120, 210));
                    image.Save(encoded, ImageFormat.Jpeg);
                    jpeg = encoded.ToArray();
                }
                using (MjpegAviWriter writer = new MjpegAviWriter(writerPath, 16, 12, 20))
                {
                    writer.WriteFrame(jpeg, jpeg.Length);
                    writer.WriteFrame(jpeg, jpeg.Length);
                    Require(writer.FrameCount == 2, "AVI writer tracks frames");
                }
                byte[] avi = File.ReadAllBytes(writerPath);
                Require(
                    avi.Length > jpeg.Length * 2 &&
                    ContainsAscii(avi, "RIFF") &&
                    ContainsAscii(avi, "AVI ") &&
                    ContainsAscii(avi, "movi") &&
                    ContainsAscii(avi, "idx1"),
                    "AVI writer finalizes a complete indexed container");

                Rectangle desktop = NativeMethods.GetVirtualScreenBounds();
                IList<HdrMonitorAdjustment> recordingHdrDisplays =
                    HdrDisplayCompensation.DiscoverActiveAdjustments();
                Rectangle recordingDisplay = recordingHdrDisplays.Count > 0
                    ? recordingHdrDisplays[0].Bounds
                    : desktop;
                Rectangle selection = new Rectangle(
                    recordingDisplay.Left + Math.Min(
                        10,
                        Math.Max(0, recordingDisplay.Width - 1)),
                    recordingDisplay.Top + Math.Min(
                        10,
                        Math.Max(0, recordingDisplay.Height - 1)),
                    Math.Min(160, Math.Max(1, recordingDisplay.Width - 10)),
                    Math.Min(90, Math.Max(1, recordingDisplay.Height - 10)));
                string recordingPath = Path.Combine(directory, "screen-recorder.avi");
                Require(ScreenRecorder.DefaultFramesPerSecond == 60,
                    "screen recordings target 60 FPS");
                using (ManualResetEvent completed = new ManualResetEvent(false))
                using (ScreenRecorder recorder = new ScreenRecorder(selection, recordingPath))
                {
                    RecordingCompletedEventArgs result = null;
                    recorder.Completed += delegate(object sender, RecordingCompletedEventArgs args)
                    {
                        result = args;
                        completed.Set();
                    };
                    recorder.Start();
                    Thread.Sleep(350);
                    if (recordingHdrDisplays.Count > 0)
                    {
                        // A cold graphics driver can initialize after the first
                        // 350 ms. Wait for actual startup, retaining the HDR
                        // requirement rather than turning it into a skipped test.
                        Stopwatch hdrStartup = Stopwatch.StartNew();
                        while (recorder.ActiveHdrOutputCount == 0 && !completed.WaitOne(0) &&
                            hdrStartup.ElapsedMilliseconds < 5000)
                        {
                            Application.DoEvents();
                            Thread.Sleep(10);
                        }
                        Console.WriteLine("HDR_RECORDING_STARTUP_WAIT_MS=" + hdrStartup.ElapsedMilliseconds);
                        if (result != null && result.Error != null)
                            Console.WriteLine("HDR_RECORDING_ERROR=" + result.Error);
                        Require(
                            recorder.ActiveHdrOutputCount > 0,
                            "screen recorder continuously tone-maps its HDR display");
                    }
                    recorder.Stop();
                    Stopwatch completionWait = Stopwatch.StartNew();
                    while (!completed.WaitOne(0) && completionWait.ElapsedMilliseconds < 5000)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }
                    Require(completed.WaitOne(0), "screen recorder stops promptly");
                    Require(result != null && result.Error == null,
                        "screen recorder completes without error");
                    Require(result != null && result.FrameCount > 0,
                        "screen recorder writes captured frames");
                    Require(recorder.CurrentFileSizeBytes > 0,
                        "screen recorder exposes current storage usage");
                }
                Require(File.Exists(recordingPath) && new FileInfo(recordingPath).Length > 512,
                    "screen recording is saved as an AVI file");
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }

            string ownershipDirectory = Path.Combine(
                Path.GetTempPath(),
                "OpenCapture-recording-ownership-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(ownershipDirectory);
                Require(
                    VideoFileStore.GetRecordingFiles(ownershipDirectory).Count == 0,
                    "an empty recordings folder is not clearable");
                Require(
                    VideoFileStore.IsRecordingFileName("09-08-07 --- 06-05-26.avi"),
                    "timestamp recording name is recognized for migration");
                Require(
                    !VideoFileStore.IsRecordingFileName("family-video.avi"),
                    "unrelated AVI names are protected from migration");

                string managed = Path.Combine(
                    ownershipDirectory,
                    "09-08-07 --- 06-05-26.avi");
                File.WriteAllBytes(managed, new byte[] { 1, 2, 3, 4 });
                Require(
                    VideoFileStore.GetRecordingFiles(ownershipDirectory).Count == 0,
                    "a timestamp-shaped AVI is not adopted after migration");

                VideoFileStore.RegisterRecording(ownershipDirectory, managed);
                Require(
                    VideoFileStore.GetRecordingFiles(ownershipDirectory).Count == 1,
                    "a completed OpenCapture recording enables cleanup");

                string renamed = Path.Combine(ownershipDirectory, "keep forever.avi");
                File.Move(managed, renamed);
                VideoFileStore.MarkRecordingRenamed(
                    ownershipDirectory,
                    managed,
                    renamed);
                Require(
                    VideoFileStore.GetRecordingFiles(ownershipDirectory).Count == 0,
                    "a renamed recording is excluded from cleanup");

                File.Move(renamed, managed);
                Require(
                    VideoFileStore.GetRecordingFiles(ownershipDirectory).Count == 0,
                    "recording rename immunity survives restoring its original name");

                string laterRecording = Path.Combine(
                    ownershipDirectory,
                    "10-08-07 --- 06-05-26.avi");
                File.WriteAllBytes(laterRecording, new byte[] { 5, 6, 7, 8 });
                VideoFileStore.RegisterRecording(ownershipDirectory, laterRecording);
                Require(
                    VideoFileStore.GetRecordingFiles(ownershipDirectory).Count == 1,
                    "new recordings remain clearable beside immune videos");

                File.AppendAllText(laterRecording, "changed");
                Require(
                    VideoFileStore.GetRecordingFiles(ownershipDirectory).Count == 0,
                    "a modified recording is protected as a replaced file");
            }
            finally
            {
                if (Directory.Exists(ownershipDirectory))
                {
                    Directory.Delete(ownershipDirectory, true);
                }
            }
        }

        private static bool ContainsAscii(byte[] buffer, string value)
        {
            byte[] pattern = System.Text.Encoding.ASCII.GetBytes(value);
            int offset;
            for (offset = 0; offset <= buffer.Length - pattern.Length; offset++)
            {
                int index;
                for (index = 0; index < pattern.Length; index++)
                {
                    if (buffer[offset + index] != pattern[index])
                    {
                        break;
                    }
                }
                if (index == pattern.Length)
                {
                    return true;
                }
            }
            return false;
        }

        private static void TestRecordingBorderThickness()
        {
            Rectangle selection = new Rectangle(240, 180, 640, 360);
            using (RecordingBorderOverlay border = new RecordingBorderOverlay(selection))
            {
                border.Show();
                Application.DoEvents();
                IList<Rectangle> pieces = border.PieceBounds;
                Require(pieces.Count == 4, "recording indicator has four edge strips");
                Require(pieces[0] == new Rectangle(240, 177, 640, 3),
                    "recording top border stays three pixels thick");
                Require(pieces[1] == new Rectangle(240, 540, 640, 3),
                    "recording bottom border stays three pixels thick");
                Require(pieces[2] == new Rectangle(237, 180, 3, 360),
                    "recording left border stays three pixels thick");
                Require(pieces[3] == new Rectangle(880, 180, 3, 360),
                    "recording right border stays three pixels thick");
                Require(
                    border.StatusBounds.Width == 250 &&
                    border.StatusBounds.Height == 58,
                    "recording storage status keeps its compact size");
                Require(
                    border.StatusInstruction == "Space to stop",
                    "active recording status identifies Space as the stop control");
                Require(border.IsStatusExcludedFromCapture,
                    "recording storage status is excluded from captured video");
            }
            using (RecordingBorderOverlay customBorder = new RecordingBorderOverlay(selection, "F8"))
            {
                Require(
                    customBorder.StatusInstruction == "F8 to stop",
                    "active recording status uses the configured start/stop control");
            }
        }

        private static void TestRecordingOverlay(
            CapturedFrame frame,
            IList<ShellZone> warmedShellZones)
        {
            ShortcutSettings recordingShortcuts = new ShortcutSettings(
                ShortcutSettings.DefaultCapture,
                ShortcutSettings.DefaultCopyColour,
                ShortcutSettings.DefaultCaptureZone,
                ShortcutSettings.DefaultRecording,
                ShortcutBinding.Keyboard(Keys.None, Keys.F8),
                ShortcutSettings.DefaultRecordingAvailability);
            using (ZoneDetector detector = ZoneDetector.Capture(frame, warmedShellZones))
            using (OverlayForm overlay = new OverlayForm(frame, detector, recordingShortcuts))
            {
                Rectangle requested = Rectangle.Empty;
                overlay.RecordingRequested += delegate(
                    object sender,
                    RecordingRequestEventArgs eventArgs)
                {
                    requested = eventArgs.Selection;
                };
                overlay.CreateControl();
                Require(!overlay.IsRecordingMode, "overlay begins in screenshot mode");
                overlay.ToggleCaptureMode();
                Require(overlay.IsRecordingMode, "second capture shortcut enters recording mode");
                overlay.ToggleCaptureMode();
                Require(!overlay.IsRecordingMode, "third capture shortcut returns to screenshot mode");
                overlay.ToggleCaptureMode();

                MethodInfo processShortcut = typeof(OverlayForm).GetMethod(
                    "ProcessCmdKey",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo currentTarget = typeof(OverlayForm).GetMethod(
                    "CurrentTargetBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo finishFlash = typeof(OverlayForm).GetMethod(
                    "FinishSuccessFlash",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Require(processShortcut != null && currentTarget != null,
                    "recording start/stop shortcut routing exists");
                Require(finishFlash != null, "recording success flash completion exists");
                Rectangle selection = (Rectangle)currentTarget.Invoke(overlay, null);
                object[] defaultControlArguments = new object[] { new Message(), Keys.Space };
                bool defaultHandled = (bool)processShortcut.Invoke(
                    overlay,
                    defaultControlArguments);
                Require(!defaultHandled && requested.IsEmpty,
                    "old Space control no longer starts recording after reassignment");
                object[] configuredControlArguments = new object[] { new Message(), Keys.F8 };
                Require(
                    (bool)processShortcut.Invoke(overlay, configuredControlArguments),
                    "configured recording control starts recording selection");
                Require(requested.IsEmpty, "recording waits for success flash");
                finishFlash.Invoke(overlay, new object[] { overlay, EventArgs.Empty });
                Require(requested == selection,
                    "recording starts with the selected screen area after the flash");
            }
        }

        private static void TestGestureTiming()
        {
            MethodInfo classifier = typeof(OverlayForm).GetMethod(
                "ShouldTransitionToDrag",
                BindingFlags.Static | BindingFlags.NonPublic);
            Require(classifier != null, "drag timing classifier exists");

            Func<bool, bool, long, bool> classify = delegate(
                bool overZone,
                bool moved,
                long elapsedMilliseconds)
            {
                return (bool)classifier.Invoke(
                    null,
                    new object[]
                    {
                        overZone,
                        moved,
                        elapsedMilliseconds,
                        1000L
                    });
            };

            Require(!classify(true, true, 299), "299ms movement remains a zone click");
            Require(!classify(true, true, 300), "300ms movement remains a zone click");
            Require(classify(true, true, 301), "301ms movement becomes a drag");
            Require(!classify(true, false, 301), "holding without movement remains a zone click");
        }

        private static void TestVisualColourZones()
        {
            Rectangle desktop = new Rectangle(-120, 40, 640, 360);
            Bitmap bitmap = new Bitmap(desktop.Width, desktop.Height, PixelFormat.Format32bppRgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (SolidBrush background = new SolidBrush(Color.FromArgb(245, 246, 248)))
            using (SolidBrush card = new SolidBrush(Color.FromArgb(211, 231, 248)))
            using (SolidBrush nested = new SolidBrush(Color.FromArgb(116, 177, 221)))
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(32, 42, 54)))
            {
                graphics.FillRectangle(background, 0, 0, bitmap.Width, bitmap.Height);
                graphics.FillRectangle(card, 48, 38, 270, 190);
                graphics.FillRectangle(nested, 84, 82, 154, 76);
                // Dark glyph-like holes should not split the surrounding colour block.
                graphics.FillRectangle(ink, 105, 104, 82, 5);
                graphics.FillRectangle(ink, 105, 119, 53, 5);
                graphics.FillEllipse(ink, 198, 101, 18, 18);
            }

            using (CapturedFrame synthetic = new CapturedFrame(bitmap, desktop, 0, DateTime.UtcNow))
            using (VisualZoneDetector visual = new VisualZoneDetector(synthetic))
            {
                bitmap = null;
                Rectangle found;
                Point nestedBackground = new Point(desktop.Left + 94, desktop.Top + 92);
                Require(
                    visual.TryFindZoneAt(nestedBackground, desktop, out found),
                    "colour block detected");
                Rectangle expectedNested = new Rectangle(
                    desktop.Left + 84,
                    desktop.Top + 82,
                    154,
                    76);
                Require(RectanglesNear(found, expectedNested, 4), "colour block bounds");

                Point overText = new Point(desktop.Left + 125, desktop.Top + 106);
                Rectangle throughGlyph;
                Require(
                    visual.TryFindZoneAt(overText, desktop, out throughGlyph),
                    "colour block bridges text and icon holes");
                Require(
                    RectanglesNear(throughGlyph, expectedNested, 4),
                    "colour block around glyph uses enclosing fill");

                Rectangle backgroundResult;
                Require(
                    !visual.TryFindZoneAt(
                        new Point(desktop.Right - 20, desktop.Bottom - 20),
                        desktop,
                        out backgroundResult),
                    "full background does not replace monitor fallback");
            }

            if (bitmap != null)
            {
                bitmap.Dispose();
            }

            Rectangle gradientDesktop = new Rectangle(25, -30, 460, 190);
            Bitmap gradientBitmap = new Bitmap(
                gradientDesktop.Width,
                gradientDesktop.Height,
                PixelFormat.Format32bppRgb);
            Rectangle gradientZone = new Rectangle(
                gradientDesktop.Left + 70,
                gradientDesktop.Top + 55,
                300,
                72);
            using (Graphics graphics = Graphics.FromImage(gradientBitmap))
            {
                graphics.Clear(Color.FromArgb(246, 248, 250));
                int x;
                for (x = 0; x < gradientZone.Width; x++)
                {
                    int shade = 196 + x * 30 / Math.Max(1, gradientZone.Width - 1);
                    using (Pen pen = new Pen(Color.FromArgb(shade, shade + 4, shade + 8)))
                    {
                        int localX = gradientZone.Left - gradientDesktop.Left + x;
                        graphics.DrawLine(
                            pen,
                            localX,
                            gradientZone.Top - gradientDesktop.Top,
                            localX,
                            gradientZone.Bottom - gradientDesktop.Top - 1);
                    }
                }
            }

            using (CapturedFrame gradientFrame = new CapturedFrame(
                gradientBitmap,
                gradientDesktop,
                0,
                DateTime.UtcNow))
            using (VisualZoneDetector gradientDetector = new VisualZoneDetector(gradientFrame))
            {
                gradientBitmap = null;
                Rectangle found;
                Require(
                    gradientDetector.TryFindZoneAt(
                        CenterOf(gradientZone),
                        gradientDesktop,
                        out found),
                    "gentle gradient is detected as one zone");
                Require(
                    RectanglesNear(found, gradientZone, 5),
                    "gentle gradient does not fragment into colour bands");
            }
            if (gradientBitmap != null)
            {
                gradientBitmap.Dispose();
            }

            Rectangle roundedDesktop = new Rectangle(0, 0, 520, 320);
            Rectangle roundedCard = new Rectangle(180, 70, 240, 180);
            Bitmap roundedBitmap = new Bitmap(
                roundedDesktop.Width,
                roundedDesktop.Height,
                PixelFormat.Format32bppRgb);
            using (Graphics graphics = Graphics.FromImage(roundedBitmap))
            using (SolidBrush cardFill = new SolidBrush(Color.FromArgb(31, 31, 33)))
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(236, 237, 240)))
            {
                graphics.Clear(Color.FromArgb(55, 70, 98));
                // The card overlaps a matching sidebar, reproducing the supplied
                // client popup where a normal flood fill merges both surfaces.
                graphics.FillRectangle(cardFill, 400, 0, 120, 320);
                const int radius = 18;
                graphics.FillRectangle(
                    cardFill,
                    roundedCard.Left + radius,
                    roundedCard.Top,
                    roundedCard.Width - radius * 2,
                    roundedCard.Height);
                graphics.FillRectangle(
                    cardFill,
                    roundedCard.Left,
                    roundedCard.Top + radius,
                    roundedCard.Width,
                    roundedCard.Height - radius * 2);
                graphics.FillEllipse(
                    cardFill,
                    roundedCard.Left,
                    roundedCard.Top,
                    radius * 2,
                    radius * 2);
                graphics.FillEllipse(
                    cardFill,
                    roundedCard.Right - radius * 2,
                    roundedCard.Top,
                    radius * 2,
                    radius * 2);
                graphics.FillEllipse(
                    cardFill,
                    roundedCard.Left,
                    roundedCard.Bottom - radius * 2,
                    radius * 2,
                    radius * 2);
                graphics.FillEllipse(
                    cardFill,
                    roundedCard.Right - radius * 2,
                    roundedCard.Bottom - radius * 2,
                    radius * 2,
                    radius * 2);
                graphics.FillEllipse(ink, 205, 96, 48, 48);
                graphics.FillRectangle(ink, 205, 164, 112, 6);
                graphics.FillRectangle(ink, 205, 184, 86, 5);
            }

            using (CapturedFrame roundedFrame = new CapturedFrame(
                roundedBitmap,
                roundedDesktop,
                0,
                DateTime.UtcNow))
            using (VisualZoneDetector roundedDetector =
                new VisualZoneDetector(roundedFrame))
            {
                roundedBitmap = null;
                Rectangle found;
                Require(
                    roundedDetector.TryFindZoneAt(
                        CenterOf(roundedCard),
                        roundedDesktop,
                        out found),
                    "rounded card attached to a matching panel is detected");
                Require(
                    Math.Abs(found.Left - roundedCard.Left) <= 4 &&
                    Math.Abs(found.Top - roundedCard.Top) <= 4 &&
                    Math.Abs(found.Bottom - roundedCard.Bottom) <= 4 &&
                    found.Right >= roundedCard.Right &&
                    found.Right <= roundedCard.Right + 16,
                    "rounded card uses its complete rectangular outer bounds");
            }
            if (roundedBitmap != null)
            {
                roundedBitmap.Dispose();
            }

            Rectangle panelDesktop = new Rectangle(0, 0, 760, 440);
            Rectangle subtleSidePanel = new Rectangle(515, 34, 205, 365);
            Bitmap panelBitmap = new Bitmap(
                panelDesktop.Width,
                panelDesktop.Height,
                PixelFormat.Format32bppRgb);
            using (Graphics graphics = Graphics.FromImage(panelBitmap))
            using (SolidBrush panelFill = new SolidBrush(Color.FromArgb(247, 248, 250)))
            using (Pen faintOutline = new Pen(Color.FromArgb(240, 241, 243), 1.0f))
            using (SolidBrush content = new SolidBrush(Color.FromArgb(106, 113, 124)))
            {
                graphics.Clear(Color.FromArgb(250, 251, 253));
                graphics.FillRectangle(panelFill, subtleSidePanel);
                graphics.DrawRectangle(
                    faintOutline,
                    subtleSidePanel.Left,
                    subtleSidePanel.Top,
                    subtleSidePanel.Width - 1,
                    subtleSidePanel.Height - 1);
                graphics.FillRectangle(content, subtleSidePanel.Left + 22, 72, 116, 5);
                graphics.FillRectangle(content, subtleSidePanel.Left + 22, 98, 152, 5);
                graphics.FillRectangle(content, subtleSidePanel.Left + 22, 124, 93, 5);
            }

            using (CapturedFrame panelFrame = new CapturedFrame(
                panelBitmap,
                panelDesktop,
                0,
                DateTime.UtcNow))
            using (VisualZoneDetector panelDetector = new VisualZoneDetector(panelFrame))
            {
                panelBitmap = null;
                Rectangle found;
                Require(
                    panelDetector.TryFindZoneAt(
                        new Point(subtleSidePanel.Left + 100, subtleSidePanel.Top + 240),
                        panelDesktop,
                        out found),
                    "subtle custom-drawn side panel is detected as a zone");
                Require(
                    RectanglesNear(found, subtleSidePanel, 4),
                    "subtle side panel hugs its visible outline");
            }
            if (panelBitmap != null)
            {
                panelBitmap.Dispose();
            }
        }

        private static bool RectanglesNear(Rectangle actual, Rectangle expected, int tolerance)
        {
            return Math.Abs(actual.Left - expected.Left) <= tolerance &&
                Math.Abs(actual.Top - expected.Top) <= tolerance &&
                Math.Abs(actual.Right - expected.Right) <= tolerance &&
                Math.Abs(actual.Bottom - expected.Bottom) <= tolerance;
        }

        private static void TestSemanticCandidateValidation()
        {
            Rectangle desktop = new Rectangle(-40, 30, 520, 180);
            Bitmap bitmap = new Bitmap(
                desktop.Width,
                desktop.Height,
                PixelFormat.Format32bppRgb);
            Rectangle tile = new Rectangle(desktop.Left + 40, desktop.Top + 46, 66, 72);
            Rectangle transparentCell = new Rectangle(
                desktop.Left + 150,
                desktop.Top + 46,
                66,
                72);
            Rectangle falseClockStrip = new Rectangle(
                desktop.Left + 292,
                desktop.Top + 46,
                42,
                72);
            Rectangle emptyStrip = new Rectangle(
                desktop.Left + 410,
                desktop.Top + 46,
                42,
                72);

            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (SolidBrush surface = new SolidBrush(Color.FromArgb(213, 225, 235)))
            using (SolidBrush tileFill = new SolidBrush(Color.FromArgb(190, 211, 229)))
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(42, 55, 67)))
            {
                graphics.FillRectangle(surface, 0, 0, bitmap.Width, bitmap.Height);
                graphics.FillRectangle(
                    tileFill,
                    tile.Left - desktop.Left,
                    tile.Top - desktop.Top,
                    tile.Width,
                    tile.Height);
                graphics.FillRectangle(
                    ink,
                    transparentCell.Left - desktop.Left + 22,
                    transparentCell.Top - desktop.Top + 24,
                    22,
                    22);

                // Clock-like text crosses both sides of the stale 42px rectangle;
                // it belongs to the continuous parent surface, not to that strip.
                graphics.FillRectangle(
                    ink,
                    falseClockStrip.Left - desktop.Left - 14,
                    falseClockStrip.Top - desktop.Top + 18,
                    falseClockStrip.Width + 28,
                    4);
                graphics.FillRectangle(
                    ink,
                    falseClockStrip.Left - desktop.Left - 9,
                    falseClockStrip.Top - desktop.Top + 40,
                    falseClockStrip.Width + 18,
                    4);
            }

            using (CapturedFrame synthetic = new CapturedFrame(
                bitmap,
                desktop,
                0,
                DateTime.UtcNow))
            using (VisualZoneDetector visual = new VisualZoneDetector(synthetic))
            {
                bitmap = null;
                Rectangle refined;
                Require(
                    visual.TryValidateCandidate(
                        CenterOf(tile),
                        tile,
                        desktop,
                        out refined) && RectanglesNear(refined, tile, 3),
                    "visually bounded semantic tile is accepted");
                Require(
                    visual.TryValidateCandidate(
                        CenterOf(transparentCell),
                        transparentCell,
                        desktop,
                        out refined) && refined == transparentCell,
                    "isolated icon validates its transparent semantic cell");
                Require(
                    !visual.TryValidateCandidate(
                        CenterOf(falseClockStrip),
                        falseClockStrip,
                        desktop,
                        out refined),
                    "text crossing a stale strip is not a distinct zone");
                Require(
                    !visual.TryValidateCandidate(
                        CenterOf(emptyStrip),
                        emptyStrip,
                        desktop,
                        out refined),
                    "continuous empty surface is not a distinct zone");
            }

            if (bitmap != null)
            {
                bitmap.Dispose();
            }
        }

        private static Point CenterOf(Rectangle bounds)
        {
            return new Point(
                bounds.Left + bounds.Width / 2,
                bounds.Top + bounds.Height / 2);
        }

        private static IList<ShellZone> TestShellZones()
        {
            using (ShellZoneProvider provider = ShellZoneProvider.Capture(
                Process.GetCurrentProcess().Id,
                1200))
            {
                Stopwatch wait = Stopwatch.StartNew();
                while (!provider.IsComplete && wait.ElapsedMilliseconds < 1400)
                {
                    Thread.Sleep(15);
                }

                int taskbars = 0;
                int appButtons = 0;
                int clockButtons = 0;
                int trayTargets = 0;
                int index;
                for (index = 0; index < provider.Snapshot.Count; index++)
                {
                    ShellZone zone = provider.Snapshot[index];
                    if (zone.Kind == ShellZoneKind.PrimaryTaskbar ||
                        zone.Kind == ShellZoneKind.SecondaryTaskbar)
                    {
                        taskbars++;
                    }
                    if (zone.Kind == ShellZoneKind.TaskbarAppButton ||
                        zone.Kind == ShellZoneKind.StartButton ||
                        zone.Kind == ShellZoneKind.SearchButton ||
                        zone.Kind == ShellZoneKind.TaskViewButton ||
                        zone.Kind == ShellZoneKind.WidgetsButton)
                    {
                        appButtons++;
                    }
                    if (zone.Kind == ShellZoneKind.ClockCalendarButton)
                    {
                        clockButtons++;
                    }
                    if (zone.Kind == ShellZoneKind.TrayIcon ||
                        zone.Kind == ShellZoneKind.NotificationArea ||
                        zone.Kind == ShellZoneKind.QuickSettingsButton ||
                        zone.Kind == ShellZoneKind.NotificationCenterButton ||
                        zone.Kind == ShellZoneKind.TaskbarOverflow)
                    {
                        trayTargets++;
                    }
                }

                Console.WriteLine(
                    "SHELL_ZONES={0} TIME_MS={1}",
                    provider.Snapshot.Count,
                    wait.ElapsedMilliseconds);
                Point reportedFalsePositive = new Point(2506, 1535);
                Rectangle currentDesktop = NativeMethods.GetVirtualScreenBounds();
                if (currentDesktop.Contains(reportedFalsePositive))
                {
                    ShellZone reportedZone = provider.FindZoneAt(reportedFalsePositive);
                    Console.WriteLine(
                        "REPORTED_POINT={0},{1} CURRENT={2}",
                        reportedFalsePositive.X,
                        reportedFalsePositive.Y,
                        reportedZone == null ? "<none>" : reportedZone.ToString());
                    for (index = 0; index < provider.Snapshot.Count; index++)
                    {
                        ShellZone candidate = provider.Snapshot[index];
                        if (candidate.Bounds.Contains(reportedFalsePositive))
                        {
                            Console.WriteLine(
                                "REPORTED_CANDIDATE={0} id={1} class={2} name={3}",
                                candidate,
                                candidate.AutomationId,
                                candidate.ClassName,
                                candidate.Name.Replace("\r", " ").Replace("\n", " "));
                        }
                    }
                }
                Require(taskbars > 0, "taskbar is a semantic zone");
                Require(appButtons > 0, "individual taskbar apps are semantic zones");
                Require(clockButtons > 0, "taskbar clock/calendar is a semantic zone");
                Require(trayTargets > 0, "tray and quick-settings controls are semantic zones");
                return new List<ShellZone>(provider.Snapshot);
            }
        }

        private static ShellZone FindShellZone(
            IList<ShellZone> zones,
            ShellZoneKind kind)
        {
            int index;
            for (index = 0; index < zones.Count; index++)
            {
                if (zones[index].Kind == kind)
                {
                    return zones[index];
                }
            }
            return null;
        }

        private static void TestStaleShellZoneReplacement(
            CapturedFrame frame,
            IList<ShellZone> warmedShellZones)
        {
            Point reportedPoint = new Point(2506, 1535);
            ShellZone currentClock = FindShellZone(
                warmedShellZones,
                ShellZoneKind.ClockCalendarButton);
            if (!frame.DesktopBounds.Contains(reportedPoint) ||
                currentClock == null ||
                !currentClock.Bounds.Contains(reportedPoint))
            {
                Console.WriteLine("SKIP stale shell replacement outside this desktop layout");
                return;
            }

            Rectangle staleBounds = new Rectangle(
                reportedPoint.X - 3,
                currentClock.Bounds.Top,
                42,
                currentClock.Bounds.Height);
            ShellZone staleQuickSettings = new ShellZone(
                staleBounds,
                ShellZoneKind.QuickSettingsButton,
                "Stale quick-settings rectangle",
                "SystemTrayIcon",
                "SystemTray.AccentButton",
                currentClock.ProcessId,
                currentClock.RootWindowHandle,
                125);

            using (ZoneDetector staleDetector = ZoneDetector.Capture(
                frame,
                new List<ShellZone> { staleQuickSettings }))
            {
                Stopwatch wait = Stopwatch.StartNew();
                while (!staleDetector.IsRefinementComplete &&
                    wait.ElapsedMilliseconds < 1500)
                {
                    Thread.Sleep(10);
                }

                Zone result = staleDetector.FindZoneAt(reportedPoint);
                Console.WriteLine(
                    "STALE_PREWARM={0} REFINED={1}",
                    staleBounds,
                    result == null ? "<null>" : result.ToString());
                Require(
                    staleDetector.IsRefinementComplete,
                    "fresh shell refinement completes");
                Require(
                    result != null && result.Bounds != staleBounds,
                    "fresh shell geometry replaces stale smaller prewarm");
            }
        }
    }
}
