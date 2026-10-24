using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace OpenCapture
{
    internal sealed class OpenCaptureApplication : ApplicationContext
    {
        private const double MinimumShellRefreshSeconds = 2.0;
        private const double ShellSafetyRefreshSeconds = 300.0;

        private readonly string capturesDirectory;
        private readonly string videosDirectory;
        private readonly string copiedTextDirectory;
        private readonly FileSystemWatcher captureRenameWatcher;
        private readonly FileSystemWatcher recordingRenameWatcher;
        private readonly NotifyIcon tray;
        private readonly Icon trayIcon;
        private TrayMenuForm trayMenu;
        private HotkeyWindow hotkeyWindow;
        private HotkeyWindow recordingModeHotkey;
        private ShortcutSettings shortcuts;
        private StartupWindowForm startupWindow;
        private ShellZoneProvider shellZoneCache;
        private ShellZoneProvider pendingShellZoneCache;
        private ShellChangeWatcher shellChangeWatcher;
        private DateTime shellZoneRefreshStartedUtc;
        private readonly System.Windows.Forms.Timer shellZoneRefreshTimer;
        private OverlayForm overlay;
        private CapturedFrame activeFrame;
        private ScreenRecorder screenRecorder;
        private HotkeyWindow recordingStopHotkey;
        private RecordingBorderOverlay recordingBorder;
        private System.Windows.Forms.Timer recordingStatusTimer;
        private bool captureInProgress;
        private bool shortcutEditorOpen;
        private bool exiting;
        private CaptureReviewForm captureReview;
        private DateTime recordingCapturedAt;
        private Rectangle recordingCapturedBounds;

        internal OpenCaptureApplication(string[] args)
        {
            capturesDirectory = Path.Combine(AppContext.BaseDirectory, "Captures");
            videosDirectory = Path.Combine(AppContext.BaseDirectory, "Videos");
            copiedTextDirectory = Path.Combine(AppContext.BaseDirectory, "Copied Text");
            Directory.CreateDirectory(capturesDirectory);
            Directory.CreateDirectory(videosDirectory);
            Directory.CreateDirectory(copiedTextDirectory);
            // Initialize ownership before watching so pre-existing captures are
            // migrated once and later timestamp-shaped files are never adopted.
            CaptureFileStore.GetScreenshotFiles(capturesDirectory);
            captureRenameWatcher = new FileSystemWatcher(capturesDirectory, "*.png");
            captureRenameWatcher.NotifyFilter = NotifyFilters.FileName;
            captureRenameWatcher.Renamed += CaptureScreenshotRenamed;
            captureRenameWatcher.EnableRaisingEvents = true;
            VideoFileStore.GetRecordingFiles(videosDirectory);
            recordingRenameWatcher = new FileSystemWatcher(videosDirectory, "*.avi");
            recordingRenameWatcher.NotifyFilter = NotifyFilters.FileName;
            recordingRenameWatcher.Renamed += RecordingFileRenamed;
            recordingRenameWatcher.EnableRaisingEvents = true;
            shortcuts = ShortcutSettings.Load();

            try
            {
                StartupManager.EnsureDefaultEnabled();
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not enable sign-in startup", exception);
            }

            trayIcon = OpenCaptureTheme.CreateTrayIcon();
            tray = new NotifyIcon();
            tray.Icon = trayIcon;
            tray.Text = GetTrayTooltip();
            tray.Visible = true;
            tray.MouseUp += ShowTrayMenu;
            tray.DoubleClick += delegate { BeginCapture(); };

            shellZoneRefreshTimer = new System.Windows.Forms.Timer();
            // The timer only checks one atomic invalidation flag. Full UI
            // Automation scans are event-driven, with a slow safety refresh.
            shellZoneRefreshTimer.Interval = 500;
            shellZoneRefreshTimer.Tick += RefreshShellZoneCache;
            shellChangeWatcher = new ShellChangeWatcher();
            try
            {
                shellZoneCache = ShellZoneProvider.Capture();
                shellZoneRefreshStartedUtc = DateTime.UtcNow;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not warm shell zone detection", exception);
            }
            shellZoneRefreshTimer.Start();

            try
            {
                hotkeyWindow = new HotkeyWindow(shortcuts.CaptureShortcut);
                hotkeyWindow.Pressed += HotkeyPressed;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not register the capture shortcut", exception);
                ShowTrayMessage(
                    shortcuts.CaptureShortcut.DisplayText + " is unavailable",
                    "Another app is using that shortcut. Open Edit Keybinds from the tray to choose another.",
                    ToolTipIcon.Warning);
            }

            if (shortcuts.RecordingAvailability == RecordingShortcutAvailability.AlwaysAvailable)
            {
                try
                {
                    recordingModeHotkey = new HotkeyWindow(shortcuts.RecordingShortcut);
                    recordingModeHotkey.Pressed += RecordingModeHotkeyPressed;
                }
                catch (Exception exception)
                {
                    ErrorLog.Write("Could not register the recording shortcut", exception);
                    ShowTrayMessage(
                        shortcuts.RecordingShortcut.DisplayText + " is unavailable",
                        "Another app is using the recording shortcut. Open Edit Keybinds from the tray to choose another.",
                        ToolTipIcon.Warning);
                }
            }

            if (!ShortcutSettings.StartupWindowDismissedForever)
            {
                ShowShortcutWindow(this, EventArgs.Empty);
            }
            if (CrashRecovery.RecoveryRequested)
            {
                ShowTrayMessage(
                    "OpenCapture recovered",
                    "An unexpected background error closed the previous process. OpenCapture restarted automatically; details were written to OpenCapture.log.",
                    ToolTipIcon.Warning);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // A normal message-loop shutdown must never be mistaken for a
                // fatal disappearance by the recovery path.
                CrashRecovery.Suppress();
                exiting = true;
                FinishPendingReview();
                CleanupCapture();
                DisposeRecordingSession();

                if (captureRenameWatcher != null)
                {
                    captureRenameWatcher.EnableRaisingEvents = false;
                    captureRenameWatcher.Renamed -= CaptureScreenshotRenamed;
                    captureRenameWatcher.Dispose();
                }
                if (recordingRenameWatcher != null)
                {
                    recordingRenameWatcher.EnableRaisingEvents = false;
                    recordingRenameWatcher.Renamed -= RecordingFileRenamed;
                    recordingRenameWatcher.Dispose();
                }

                shellZoneRefreshTimer.Stop();
                shellZoneRefreshTimer.Tick -= RefreshShellZoneCache;
                shellZoneRefreshTimer.Dispose();
                if (shellChangeWatcher != null)
                {
                    shellChangeWatcher.Dispose();
                    shellChangeWatcher = null;
                }
                if (pendingShellZoneCache != null)
                {
                    pendingShellZoneCache.Dispose();
                    pendingShellZoneCache = null;
                }
                if (shellZoneCache != null)
                {
                    shellZoneCache.Dispose();
                    shellZoneCache = null;
                }

                if (hotkeyWindow != null)
                {
                    hotkeyWindow.Pressed -= HotkeyPressed;
                    hotkeyWindow.Dispose();
                    hotkeyWindow = null;
                }
                if (recordingModeHotkey != null)
                {
                    recordingModeHotkey.Pressed -= RecordingModeHotkeyPressed;
                    recordingModeHotkey.Dispose();
                    recordingModeHotkey = null;
                }

                if (startupWindow != null)
                {
                    StartupWindowForm oldStartup = startupWindow;
                    startupWindow = null;
                    oldStartup.FormClosed -= StartupWindowClosed;
                    if (!oldStartup.IsDisposed)
                    {
                        oldStartup.Close();
                        oldStartup.Dispose();
                    }
                }

                if (trayMenu != null)
                {
                    trayMenu.QuitRequested -= ExitFromTray;
                    trayMenu.AutostartRequested -= ToggleStartup;
                    trayMenu.EditKeybindsRequested -= ShowShortcutWindow;
                    trayMenu.ClearCapturesRequested -= ClearCaptureFolder;
                    trayMenu.ClearRecordingsRequested -= ClearRecordingFolder;
                    trayMenu.ClearClipboardRequested -= ClearCaptureClipboard;
                    trayMenu.Dispose();
                    trayMenu = null;
                }

                if (tray != null)
                {
                    tray.Visible = false;
                    tray.MouseUp -= ShowTrayMenu;
                    tray.Dispose();
                }

                if (trayIcon != null)
                {
                    trayIcon.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private void HotkeyPressed(object sender, EventArgs eventArgs)
        {
            if (screenRecorder != null)
            {
                return;
            }
            if (captureInProgress && overlay != null && !overlay.IsDisposed)
            {
                if (shortcuts.RecordingAvailability == RecordingShortcutAvailability.AfterCaptureShortcut &&
                    shortcuts.RecordingShortcut.Equals(shortcuts.CaptureShortcut))
                {
                    overlay.ToggleCaptureMode();
                }
                return;
            }
            BeginCapture();
        }

        private void RecordingModeHotkeyPressed(object sender, EventArgs eventArgs)
        {
            if (screenRecorder != null)
            {
                return;
            }
            if (captureInProgress && overlay != null && !overlay.IsDisposed)
            {
                overlay.ToggleCaptureMode();
                return;
            }
            BeginCapture(true);
        }

        private void BeginCapture()
        {
            BeginCapture(false);
        }

        private void BeginCapture(bool startInRecordingMode)
        {
            if (captureInProgress || screenRecorder != null || exiting)
            {
                return;
            }

            if (captureReview != null && captureReview.IsEditing) return;
            FinishPendingReview();

            captureInProgress = true;
            try
            {
                // The frame is copied before any OpenCapture window is shown. Zone
                // discovery follows and only inspects the underlying HWND snapshot.
                activeFrame = CaptureEngine.CaptureVirtualDesktop(false);
                IList<ShellZone> prewarmedZones = shellZoneCache == null
                    ? null
                    : shellZoneCache.Snapshot;
                ZoneDetector detector = ZoneDetector.Capture(
                    activeFrame,
                    prewarmedZones);
                overlay = new OverlayForm(activeFrame, detector, shortcuts);
                overlay.SelectionCompleted += SelectionCompleted;
                overlay.ColorCopyRequested += ColorCopyRequested;
                overlay.TextCopyRequested += TextCopyRequested;
                overlay.RecordingRequested += RecordingRequested;
                overlay.SelectionCancelled += SelectionCancelled;
                overlay.FormClosed += OverlayClosedUnexpectedly;
                overlay.Show();
                if (startInRecordingMode)
                {
                    overlay.ToggleCaptureMode();
                }
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not begin a capture", exception);
                CleanupCapture();
                ShowTrayMessage(
                    "Capture failed",
                    exception.Message,
                    ToolTipIcon.Error);
            }
        }

        private void SelectionCompleted(object sender, SelectionEventArgs eventArgs)
        {
            PendingCapture pending = null;
            try
            {
                pending = new PendingCapture(CaptureKind.Screenshot,
                    activeFrame.Crop(eventArgs.Selection), null,
                    activeFrame.CapturedAtUtc.ToLocalTime(), null);
                pending.DesktopBounds = eventArgs.Selection;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not prepare capture review", exception);
                ShowTrayMessage("Capture failed", exception.Message, ToolTipIcon.Warning);
            }
            finally { CleanupCapture(); }
            if (pending != null) ShowCaptureReview(pending);
        }

        private void CommitScreenshot(PendingCapture pending)
        {
            Bitmap image = null;
            string savedPath = null;
            Exception saveError = null;
            Exception clipboardError = null;

            try
            {
                image = pending.RenderImage();
                savedPath = GetUniqueCapturePath(pending.CapturedAt);
                try
                {
                    image.Save(savedPath, ImageFormat.Png);
                    CaptureFileStore.RegisterScreenshot(capturesDirectory, savedPath);
                }
                catch (Exception exception)
                {
                    saveError = exception;
                    ErrorLog.Write("Could not save capture", exception);
                }

                if (!CaptureEngine.TryCopyImageToClipboard(image, out clipboardError))
                {
                    ErrorLog.Write("Could not copy capture to the clipboard", clipboardError);
                }
            }
            catch (Exception exception)
            {
                saveError = exception;
                ErrorLog.Write("Could not complete capture", exception);
            }
            finally
            {
                if (image != null)
                {
                    image.Dispose();
                }
            }

            if (saveError != null || clipboardError != null)
            {
                string message;
                if (saveError != null && clipboardError != null)
                {
                    message = "The image could not be saved or copied. See OpenCapture.log.";
                }
                else if (saveError != null)
                {
                    message = "The image was copied, but it could not be saved.";
                }
                else
                {
                    message = "The image was saved, but the clipboard was busy.";
                }
                ShowTrayMessage("Capture needs attention", message, ToolTipIcon.Warning);
            }
        }

        private void SelectionCancelled(object sender, EventArgs eventArgs)
        {
            CleanupCapture();
        }

        private void ColorCopyRequested(object sender, ColorCopyEventArgs eventArgs)
        {
            PendingCapture pending = new PendingCapture(CaptureKind.Colour, null,
                eventArgs.HexCode, DateTime.Now, null);
            CleanupCapture();
            ShowCaptureReview(pending);
        }

        private void CommitColour(PendingCapture pending)
        {
            Exception clipboardError;
            if (TryCopyColourToClipboard(pending.Text, out clipboardError)) return;
            ErrorLog.Write("Could not copy colour code to the clipboard", clipboardError);
            ShowTrayMessage("Colour code was not copied", "The clipboard was busy. Please try again.", ToolTipIcon.Warning);
        }

        internal static bool TryCopyColourToClipboard(string hexCode, out Exception clipboardError)
        {
            clipboardError = null;
            try
            {
                DataObject data = CreateTextClipboardData(hexCode);
                Clipboard.SetDataObject(
                    data,
                    true,
                    CaptureEngine.DefaultClipboardRetryCount,
                    CaptureEngine.DefaultClipboardRetryDelayMilliseconds);
                return true;
            }
            catch (Exception exception)
            {
                clipboardError = exception;
                return false;
            }
        }

        private void TextCopyRequested(object sender, TextCopyRequestEventArgs eventArgs)
        {
            PendingCapture pending = null;
            try
            {
                pending = new PendingCapture(CaptureKind.Text,
                    activeFrame.Crop(eventArgs.Selection),
                    eventArgs.Recognition == null ? null : eventArgs.Recognition.Text,
                    activeFrame.CapturedAtUtc.ToLocalTime(), null);
                pending.DesktopBounds = eventArgs.Selection;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not prepare text review", exception);
                ShowTrayMessage("Text capture failed", exception.Message, ToolTipIcon.Warning);
            }
            finally { CleanupCapture(); }
            if (pending != null) ShowCaptureReview(pending);
        }

        private void CommitText(PendingCapture pending)
        {
            Bitmap image = null;
            Exception error = null;
            string recognisedText = null;
            try
            {
                image = pending.RenderImage();
                recognisedText = pending.Text ?? WindowsOcrEngine.RecognizeDetailed(image).Text;
                if (string.IsNullOrWhiteSpace(recognisedText))
                {
                    throw new InvalidOperationException("No text was detected in the selected area.");
                }

                string savedPath = CopiedTextFileStore.GetUniquePath(
                    copiedTextDirectory,
                    recognisedText);
                image.Save(savedPath, ImageFormat.Png);

                // Text is the only clipboard format on this path. The cropped
                // image is retained on disk but is never placed on the clipboard.
                DataObject data = CreateTextClipboardData(recognisedText);
                Clipboard.SetDataObject(
                    data,
                    true,
                    CaptureEngine.DefaultClipboardRetryCount,
                    CaptureEngine.DefaultClipboardRetryDelayMilliseconds);
            }
            catch (Exception exception)
            {
                error = exception;
                ErrorLog.Write("Could not copy text from the selected area", exception);
            }
            finally
            {
                if (image != null)
                {
                    image.Dispose();
                }
            }

            if (error != null)
            {
                ShowTrayMessage(
                    "Text was not copied",
                    error is InvalidOperationException &&
                        error.Message.StartsWith("No text", StringComparison.Ordinal)
                        ? error.Message
                        : "OCR, saving, or the clipboard failed. See OpenCapture.log.",
                    ToolTipIcon.Warning);
            }
        }

        internal static DataObject CreateTextClipboardData(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                throw new ArgumentException("Clipboard text is required.", "text");
            }

            DataObject data = new DataObject();
            data.SetText(text, TextDataFormat.UnicodeText);
            return data;
        }

        private void OverlayClosedUnexpectedly(object sender, FormClosedEventArgs eventArgs)
        {
            if (captureInProgress)
            {
                CleanupCapture();
            }
        }

        private void CleanupCapture()
        {
            OverlayForm oldOverlay = overlay;
            overlay = null;
            if (oldOverlay != null)
            {
                oldOverlay.SelectionCompleted -= SelectionCompleted;
                oldOverlay.ColorCopyRequested -= ColorCopyRequested;
                oldOverlay.TextCopyRequested -= TextCopyRequested;
                oldOverlay.RecordingRequested -= RecordingRequested;
                oldOverlay.SelectionCancelled -= SelectionCancelled;
                oldOverlay.FormClosed -= OverlayClosedUnexpectedly;
                if (!oldOverlay.IsDisposed)
                {
                    oldOverlay.Close();
                    oldOverlay.Dispose();
                }
            }

            CapturedFrame oldFrame = activeFrame;
            activeFrame = null;
            if (oldFrame != null)
            {
                oldFrame.Dispose();
            }
            captureInProgress = false;
        }

        private string GetUniqueCapturePath(DateTime capturedAt)
        {
            return CaptureFileStore.GetUniqueCapturePath(capturesDirectory, capturedAt);
        }

        private void CaptureScreenshotRenamed(object sender, RenamedEventArgs eventArgs)
        {
            try
            {
                CaptureFileStore.MarkScreenshotRenamed(
                    capturesDirectory,
                    eventArgs.OldFullPath,
                    eventArgs.FullPath);
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not protect a renamed screenshot", exception);
            }
        }

        private void RecordingFileRenamed(object sender, RenamedEventArgs eventArgs)
        {
            try
            {
                VideoFileStore.MarkRecordingRenamed(
                    videosDirectory,
                    eventArgs.OldFullPath,
                    eventArgs.FullPath);
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not protect a renamed recording", exception);
            }
        }

        private void ToggleStartup(object sender, EventArgs eventArgs)
        {
            try
            {
                StartupManager.SetEnabled(!StartupManager.IsEnabled);
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not change startup setting", exception);
                ShowTrayMessage("Startup setting was not changed", exception.Message, ToolTipIcon.Error);
            }
        }

        private void RefreshShellZoneCache(object sender, EventArgs eventArgs)
        {
            if (exiting || captureInProgress || screenRecorder != null)
            {
                return;
            }

            if (pendingShellZoneCache != null)
            {
                if (!pendingShellZoneCache.IsComplete)
                {
                    return;
                }

                ShellZoneProvider old = shellZoneCache;
                shellZoneCache = pendingShellZoneCache;
                pendingShellZoneCache = null;
                if (old != null)
                {
                    old.Dispose();
                }
            }

            DateTime now = DateTime.UtcNow;
            double secondsSinceRefresh = shellZoneRefreshStartedUtc == DateTime.MinValue
                ? double.MaxValue
                : (now - shellZoneRefreshStartedUtc).TotalSeconds;
            bool shellChanged = shellChangeWatcher != null &&
                shellChangeWatcher.ConsumeChanged();
            if (shellChanged && secondsSinceRefresh < MinimumShellRefreshSeconds)
            {
                // Preserve the invalidation while collapsing bursts of taskbar
                // animation and foreground events into one scan.
                shellChangeWatcher.MarkChanged();
                return;
            }
            bool safetyRefreshDue = shellZoneRefreshStartedUtc == DateTime.MinValue ||
                secondsSinceRefresh >= ShellSafetyRefreshSeconds;
            if (!shellChanged && !safetyRefreshDue)
            {
                return;
            }

            try
            {
                pendingShellZoneCache = ShellZoneProvider.Capture();
                shellZoneRefreshStartedUtc = now;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not refresh shell zones", exception);
            }
        }

        private void ExitFromTray(object sender, EventArgs eventArgs)
        {
            CrashRecovery.Suppress();
            exiting = true;
            FinishPendingReview();
            CleanupCapture();
            if (screenRecorder != null)
            {
                // Finish the AVI before leaving the message loop; its completion
                // callback accepts it without opening another review on shutdown.
                StopScreenRecording();
                return;
            }
            ExitThread();
        }

        private void RecordingRequested(object sender, RecordingRequestEventArgs eventArgs)
        {
            Rectangle selection = eventArgs.Selection;
            CleanupCapture();
            StartScreenRecording(selection);
        }

        private void StartScreenRecording(Rectangle selection)
        {
            if (screenRecorder != null || exiting)
            {
                return;
            }

            string path = null;
            try
            {
                FinishPendingReview();
                recordingCapturedAt = DateTime.Now;
                recordingCapturedBounds = selection;
                string stagingDirectory = Path.Combine(videosDirectory, ".Pending");
                Directory.CreateDirectory(stagingDirectory);
                path = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".avi");
                if (ShouldActivateGlobalShortcuts(shortcutEditorOpen))
                {
                    CreateRecordingStopHotkey();
                }

                recordingBorder = new RecordingBorderOverlay(
                    selection,
                    shortcuts.RecordingControlShortcut.DisplayText);
                recordingBorder.Show();

                screenRecorder = new ScreenRecorder(selection, path);
                screenRecorder.Completed += ScreenRecordingCompleted;
                screenRecorder.Start();
                recordingBorder.UpdateStorage(0L);
                recordingStatusTimer = new System.Windows.Forms.Timer();
                recordingStatusTimer.Interval = 200;
                recordingStatusTimer.Tick += UpdateRecordingStatus;
                recordingStatusTimer.Start();
                tray.Text = GetRecordingTrayTooltip(shortcuts.RecordingControlShortcut);
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not start screen recording", exception);
                DisposeRecordingSession();
                ShowTrayMessage(
                    "Recording did not start",
                    exception.Message,
                    ToolTipIcon.Error);
            }
        }

        private void RecordingStopHotkeyPressed(object sender, EventArgs eventArgs)
        {
            StopScreenRecording();
        }

        private void UpdateRecordingStatus(object sender, EventArgs eventArgs)
        {
            if (screenRecorder != null && recordingBorder != null)
            {
                recordingBorder.UpdateStorage(screenRecorder.CurrentFileSizeBytes);
            }
        }

        private void StopScreenRecording()
        {
            DisposeRecordingStatusTimer();
            ReleaseRecordingStopHotkey();
            if (recordingBorder != null)
            {
                recordingBorder.Dispose();
                recordingBorder = null;
            }
            if (screenRecorder != null)
            {
                screenRecorder.Stop();
            }
        }

        private void ScreenRecordingCompleted(
            object sender,
            RecordingCompletedEventArgs eventArgs)
        {
            ScreenRecorder completed = sender as ScreenRecorder;
            if (completed != null)
            {
                completed.Completed -= ScreenRecordingCompleted;
            }

            DisposeRecordingSession();
            bool recordingExists = eventArgs.FrameCount > 0 && File.Exists(eventArgs.Path);
            if (recordingExists)
            {
                PendingCapture pending = null;
                try
                {
                    using (MjpegAviReader reader = new MjpegAviReader(eventArgs.Path))
                    {
                        pending = new PendingCapture(CaptureKind.Recording,
                            reader.ReadFrame(), null, recordingCapturedAt, eventArgs.Path);
                        pending.DesktopBounds = recordingCapturedBounds;
                    }
                    if (exiting) { CommitRecording(pending); }
                    else { ShowCaptureReview(pending); }
                }
                catch (Exception exception)
                {
                    ErrorLog.Write("Could not review the completed recording", exception);
                    // Preserve the completed original if the thumbnail decoder fails.
                    if (pending == null)
                        pending = new PendingCapture(CaptureKind.Recording, null, null, recordingCapturedAt, eventArgs.Path);
                    CommitRecording(pending);
                }
            }
            if (exiting)
            {
                ExitThread();
                return;
            }

            if (eventArgs.Error != null)
            {
                ErrorLog.Write("Screen recording ended with an error", eventArgs.Error);
                ShowTrayMessage(
                    eventArgs.FrameCount > 0 ? "Recording stopped early" : "Recording failed",
                    eventArgs.Error.Message,
                    ToolTipIcon.Warning);
            }
        }

        private void ShowCaptureReview(PendingCapture pending)
        {
            FinishPendingReview();
            try
            {
                captureReview = new CaptureReviewForm(pending);
                captureReview.Decided += CaptureReviewDecided;
                captureReview.ShowNear(Cursor.Position);
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not open capture review", exception);
                if (captureReview != null)
                {
                    captureReview.Decided -= CaptureReviewDecided;
                    captureReview.Dispose();
                    captureReview = null;
                }
                CommitCapture(pending);
            }
        }

        private void FinishPendingReview()
        {
            if (captureReview == null) return;
            if (exiting) captureReview.FinishForShutdown();
            else captureReview.Finish(true);
        }

        private void CaptureReviewDecided(object sender, CaptureReviewDecisionEventArgs args)
        {
            CaptureReviewForm review = (CaptureReviewForm)sender;
            PendingCapture pending = review.CaptureResult;
            if (ReferenceEquals(captureReview, review)) captureReview = null;
            review.Decided -= CaptureReviewDecided;
            review.Dispose();
            if (args.Accepted) CommitCapture(pending);
            else pending.Dispose();
        }

        private void CommitCapture(PendingCapture pending)
        {
            if (pending.Kind == CaptureKind.Recording)
            {
                CommitRecording(pending);
                return;
            }
            try
            {
                if (pending.Kind == CaptureKind.Screenshot) CommitScreenshot(pending);
                else if (pending.Kind == CaptureKind.Colour) CommitColour(pending);
                else CommitText(pending);
            }
            finally { pending.Dispose(); }
        }

        private void CommitRecording(PendingCapture pending)
        {
            // Large video crops are streamed off the interface thread. The review
            // transfers its private original to this operation until export succeeds.
            SynchronizationContext context = SynchronizationContext.Current;
            Thread export = new Thread(delegate()
            {
                string savedPath = null;
                Exception error = null;
                try
                {
                    savedPath = VideoFileStore.GetUniqueVideoPath(videosDirectory, pending.CapturedAt);
                    if (pending.HasCrop)
                        MjpegAviReader.CropVideo(pending.VideoPath, savedPath, pending.Crop);
                    else
                    {
                        File.Move(pending.VideoPath, savedPath);
                        pending.VideoPath = null;
                    }
                    VideoFileStore.RegisterRecording(videosDirectory, savedPath);
                }
                catch (Exception exception)
                {
                    error = exception;
                    ErrorLog.Write("Could not accept recording", exception);
                    // Export failures preserve the original in Videos/.Pending.
                    pending.VideoPath = null;
                }
                finally { pending.Dispose(); }
                if (context == null) return;
                try
                {
                    context.Post(delegate
                    {
                        if (exiting) return;
                        if (error != null)
                        {
                            ShowTrayMessage("Recording needs attention", error.Message, ToolTipIcon.Warning);
                            return;
                        }
                        Exception clipboardError;
                        if (!CaptureEngine.TryCopyRecordingToClipboard(savedPath, out clipboardError))
                        {
                            ErrorLog.Write("Could not copy the completed recording", clipboardError);
                            ShowTrayMessage("Recording saved", "The clipboard was busy.", ToolTipIcon.Warning);
                        }
                    }, null);
                }
                catch (Exception exception) { ErrorLog.Write("Could not finish recording acceptance", exception); }
            });
            // A confirmed export must finish even if the user quits while a long
            // crop is being encoded. Idle/trailing/countdown work has no worker.
            export.IsBackground = false;
            export.Name = "OpenCapture accepted recording export";
            export.Start();
        }

        private void DisposeRecordingSession()
        {
            DisposeRecordingStatusTimer();
            ReleaseRecordingStopHotkey();
            if (recordingBorder != null)
            {
                recordingBorder.Dispose();
                recordingBorder = null;
            }
            if (screenRecorder != null)
            {
                ScreenRecorder oldRecorder = screenRecorder;
                screenRecorder = null;
                oldRecorder.Completed -= ScreenRecordingCompleted;
                oldRecorder.Stop();
                oldRecorder.Dispose();
            }
            if (tray != null)
            {
                tray.Text = GetTrayTooltip();
            }
        }

        private void DisposeRecordingStatusTimer()
        {
            if (recordingStatusTimer != null)
            {
                recordingStatusTimer.Stop();
                recordingStatusTimer.Tick -= UpdateRecordingStatus;
                recordingStatusTimer.Dispose();
                recordingStatusTimer = null;
            }
        }

        private void ShowTrayMessage(string title, string message, ToolTipIcon icon)
        {
            if (tray == null)
            {
                return;
            }

            tray.BalloonTipTitle = title;
            tray.BalloonTipText = message;
            tray.BalloonTipIcon = icon;
            tray.ShowBalloonTip(4500);
        }

        private void ShowTrayMenu(object sender, MouseEventArgs eventArgs)
        {
            if (eventArgs.Button != MouseButtons.Right || exiting)
            {
                return;
            }

            EnsureTrayMenu();
            trayMenu.AutostartEnabled = StartupManager.IsEnabled;
            trayMenu.CapturesClearable = HasClearableCaptures();
            trayMenu.RecordingsClearable = HasClearableRecordings();
            trayMenu.ClipboardClearable = CaptureEngine.IsOpenCaptureContentOnClipboard();
            trayMenu.ShowAt(Cursor.Position);
        }

        private void EnsureTrayMenu()
        {
            if (trayMenu != null && !trayMenu.IsDisposed)
            {
                return;
            }

            trayMenu = new TrayMenuForm();
            trayMenu.QuitRequested += ExitFromTray;
            trayMenu.AutostartRequested += ToggleStartup;
            trayMenu.EditKeybindsRequested += ShowShortcutWindow;
            trayMenu.ClearCapturesRequested += ClearCaptureFolder;
            trayMenu.ClearRecordingsRequested += ClearRecordingFolder;
            trayMenu.ClearClipboardRequested += ClearCaptureClipboard;
        }

        private void ShowShortcutWindow(object sender, EventArgs eventArgs)
        {
            if (startupWindow != null && !startupWindow.IsDisposed)
            {
                startupWindow.Show();
                startupWindow.Activate();
                startupWindow.Focus();
                return;
            }

            shortcutEditorOpen = true;
            ReleaseShortcutHotkeys();
            ReleaseRecordingStopHotkey();
            startupWindow = new StartupWindowForm(shortcuts, TryApplyShortcutSettings);
            startupWindow.FormClosed += StartupWindowClosed;
            startupWindow.Show();
        }

        private void StartupWindowClosed(object sender, FormClosedEventArgs eventArgs)
        {
            StartupWindowForm closed = sender as StartupWindowForm;
            if (closed != null)
            {
                closed.FormClosed -= StartupWindowClosed;
                if (ReferenceEquals(startupWindow, closed))
                {
                    shortcuts = closed.Settings;
                    startupWindow = null;
                    shortcutEditorOpen = false;
                    RestoreShortcutHotkeysAfterEditor();
                }
            }
        }

        private string TryApplyShortcutSettings(ShortcutSettings candidate)
        {
            if (candidate == null)
            {
                return "The shortcut settings were empty.";
            }

            ShortcutSettings previous = shortcuts;
            if (screenRecorder != null &&
                !candidate.RecordingControlShortcut.Equals(previous.RecordingControlShortcut))
            {
                return "Stop the active recording before changing its start/stop shortcut.";
            }
            if (candidate.RecordingAvailability == RecordingShortcutAvailability.AlwaysAvailable &&
                candidate.RecordingShortcut.Equals(candidate.CaptureShortcut))
            {
                return "Capture and recording need different shortcuts when recording is always available.";
            }
            if (HasShortcutConflict(candidate))
            {
                return "Each OpenCapture action needs a different shortcut.";
            }

            bool hotkeysChanged =
                !candidate.CaptureShortcut.Equals(previous.CaptureShortcut) ||
                !candidate.RecordingShortcut.Equals(previous.RecordingShortcut) ||
                candidate.RecordingAvailability != previous.RecordingAvailability;
            if (hotkeysChanged && ShouldActivateGlobalShortcuts(shortcutEditorOpen))
            {
                ReleaseShortcutHotkeys();

                try
                {
                    CreateShortcutHotkeys(candidate);
                }
                catch (Exception exception)
                {
                    ErrorLog.Write("Could not change the capture or recording shortcut", exception);
                    ReleaseShortcutHotkeys();
                    try
                    {
                        CreateShortcutHotkeys(previous);
                    }
                    catch (Exception restoreException)
                    {
                        ErrorLog.Write("Could not restore the previous capture shortcut", restoreException);
                    }
                    return exception.Message;
                }
            }

            try
            {
                candidate.Save();
                shortcuts = candidate;
                tray.Text = GetTrayTooltip();
                return null;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not save shortcut settings", exception);
                if (hotkeysChanged && ShouldActivateGlobalShortcuts(shortcutEditorOpen))
                {
                    ReleaseShortcutHotkeys();
                    try
                    {
                        CreateShortcutHotkeys(previous);
                    }
                    catch (Exception restoreException)
                    {
                        ErrorLog.Write("Could not restore the previous capture shortcut", restoreException);
                    }
                }
                return exception.Message;
            }
        }

        private void CreateShortcutHotkeys(ShortcutSettings settingsToRegister)
        {
            hotkeyWindow = new HotkeyWindow(settingsToRegister.CaptureShortcut);
            hotkeyWindow.Pressed += HotkeyPressed;
            if (settingsToRegister.RecordingAvailability ==
                RecordingShortcutAvailability.AlwaysAvailable)
            {
                recordingModeHotkey = new HotkeyWindow(settingsToRegister.RecordingShortcut);
                recordingModeHotkey.Pressed += RecordingModeHotkeyPressed;
            }
        }

        private void ReleaseShortcutHotkeys()
        {
            if (hotkeyWindow != null)
            {
                hotkeyWindow.Pressed -= HotkeyPressed;
                hotkeyWindow.Dispose();
                hotkeyWindow = null;
            }
            if (recordingModeHotkey != null)
            {
                recordingModeHotkey.Pressed -= RecordingModeHotkeyPressed;
                recordingModeHotkey.Dispose();
                recordingModeHotkey = null;
            }
        }

        private void CreateRecordingStopHotkey()
        {
            if (recordingStopHotkey != null)
            {
                return;
            }
            recordingStopHotkey = new HotkeyWindow(shortcuts.RecordingControlShortcut);
            recordingStopHotkey.Pressed += RecordingStopHotkeyPressed;
        }

        private void ReleaseRecordingStopHotkey()
        {
            if (recordingStopHotkey == null)
            {
                return;
            }
            recordingStopHotkey.Pressed -= RecordingStopHotkeyPressed;
            recordingStopHotkey.Dispose();
            recordingStopHotkey = null;
        }

        private void RestoreShortcutHotkeysAfterEditor()
        {
            if (exiting || !ShouldActivateGlobalShortcuts(shortcutEditorOpen))
            {
                return;
            }

            try
            {
                CreateShortcutHotkeys(shortcuts);
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not restore shortcuts after closing the keybind editor", exception);
                ReleaseShortcutHotkeys();
                ShowTrayMessage(
                    "A shortcut is unavailable",
                    exception.Message,
                    ToolTipIcon.Warning);
            }

            if (screenRecorder != null)
            {
                try
                {
                    CreateRecordingStopHotkey();
                }
                catch (Exception exception)
                {
                    ErrorLog.Write("Could not restore the recording stop shortcut", exception);
                    ReleaseRecordingStopHotkey();
                    ShowTrayMessage(
                        shortcuts.RecordingControlShortcut.DisplayText + " is unavailable",
                        "Close the app currently using that shortcut, then reopen Edit Keybinds.",
                        ToolTipIcon.Warning);
                }
            }
        }

        private bool HasClearableCaptures()
        {
            try
            {
                return CaptureFileStore.GetScreenshotFiles(capturesDirectory).Count > 0;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not inspect the Captures folder", exception);
                return false;
            }
        }

        private bool HasClearableRecordings()
        {
            try
            {
                return VideoFileStore.GetRecordingFiles(videosDirectory).Count > 0;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not inspect the Videos folder", exception);
                return false;
            }
        }

        private void ClearCaptureFolder(object sender, EventArgs eventArgs)
        {
            IList<string> screenshots;
            try
            {
                screenshots = CaptureFileStore.GetScreenshotFiles(capturesDirectory);
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not inspect the Captures folder", exception);
                ShowTrayMessage("Captures were not cleared", exception.Message, ToolTipIcon.Warning);
                return;
            }

            if (screenshots.Count == 0)
            {
                return;
            }

            DialogResult answer = MessageBox.Show(
                string.Format(
                    "Move {0} OpenCapture screenshot{1} to the Recycle Bin?\r\n\r\nOther files and subfolders will not be changed.",
                    screenshots.Count,
                    screenshots.Count == 1 ? string.Empty : "s"),
                "Clear Captures folder",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            try
            {
                int cleared = CaptureFileStore.ClearScreenshotsToRecycleBin(capturesDirectory);
                ShowTrayMessage(
                    "Captures folder cleared",
                    string.Format("{0} screenshot{1} moved to the Recycle Bin.", cleared, cleared == 1 ? string.Empty : "s"),
                    ToolTipIcon.Info);
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not clear the Captures folder", exception);
                ShowTrayMessage("Captures were not cleared", exception.Message, ToolTipIcon.Warning);
            }
        }

        private void ClearRecordingFolder(object sender, EventArgs eventArgs)
        {
            IList<string> recordings;
            try
            {
                recordings = VideoFileStore.GetRecordingFiles(videosDirectory);
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not inspect the Videos folder", exception);
                ShowTrayMessage("Recordings were not cleared", exception.Message, ToolTipIcon.Warning);
                return;
            }

            if (recordings.Count == 0)
            {
                return;
            }

            DialogResult answer = MessageBox.Show(
                string.Format(
                    "Move {0} OpenCapture recording{1} to the Recycle Bin?\r\n\r\nOther files and subfolders will not be changed.",
                    recordings.Count,
                    recordings.Count == 1 ? string.Empty : "s"),
                "Clear Recordings folder",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            try
            {
                int cleared = VideoFileStore.ClearRecordingsToRecycleBin(videosDirectory);
                ShowTrayMessage(
                    "Recordings folder cleared",
                    string.Format(
                        "{0} recording{1} moved to the Recycle Bin.",
                        cleared,
                        cleared == 1 ? string.Empty : "s"),
                    ToolTipIcon.Info);
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not clear the Videos folder", exception);
                ShowTrayMessage("Recordings were not cleared", exception.Message, ToolTipIcon.Warning);
            }
        }

        private void ClearCaptureClipboard(object sender, EventArgs eventArgs)
        {
            bool cleared;
            Exception error;
            if (!CaptureEngine.TryClearOpenCaptureContentFromClipboard(out cleared, out error))
            {
                ErrorLog.Write("Could not clear the OpenCapture clipboard item", error);
                ShowTrayMessage(
                    "Clipboard was not cleared",
                    error == null ? "The clipboard is busy." : error.Message,
                    ToolTipIcon.Warning);
            }
        }

        private string GetTrayTooltip()
        {
            string text = "OpenCapture — " + shortcuts.CaptureShortcut.DisplayText + " to capture";
            return LimitTrayTooltip(text);
        }

        internal static string GetRecordingTrayTooltip(ShortcutBinding recordingControl)
        {
            return LimitTrayTooltip("OpenCapture — Recording — " + recordingControl.DisplayText + " to stop");
        }

        private static string LimitTrayTooltip(string text)
        {
            // NotifyIcon rejects text longer than 63 characters on .NET Framework.
            return text.Length <= 63 ? text : text.Substring(0, 63);
        }

        private static bool HasShortcutConflict(ShortcutSettings candidate)
        {
            return candidate.CaptureShortcut.Equals(candidate.CopyColourShortcut) ||
                candidate.CaptureShortcut.Equals(candidate.CaptureZoneShortcut) ||
                candidate.CopyColourShortcut.Equals(candidate.CaptureZoneShortcut) ||
                candidate.RecordingShortcut.Equals(candidate.CopyColourShortcut) ||
                candidate.RecordingShortcut.Equals(candidate.CaptureZoneShortcut) ||
                candidate.RecordingControlShortcut.Equals(candidate.CaptureShortcut) ||
                candidate.RecordingControlShortcut.Equals(candidate.CopyColourShortcut) ||
                candidate.RecordingControlShortcut.Equals(candidate.CaptureZoneShortcut) ||
                candidate.RecordingControlShortcut.Equals(candidate.RecordingShortcut);
        }

        internal static bool ShouldActivateGlobalShortcuts(bool keybindEditorOpen)
        {
            return !keybindEditorOpen;
        }
    }
}
