using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace OpenCapture
{
    internal sealed class OverlayForm : Form
    {
        private const int WM_SETCURSOR = 0x0020;
        private const int MagnifierCells = 11;
        private const int MagnifierCellSize = 11;
        private const int PanelPadding = 12;
        private const int PanelWidth = 250;
        private const int DetailTop = 77;
        private const int DetailLineHeight = 14;
        private const int DefaultKeyboardSensitivityPercent = 100;
        private const int MinimumKeyboardSensitivityPercent = 25;
        private const int MaximumKeyboardSensitivityPercent = 400;
        private const int KeyboardSensitivityStepPercent = 25;
        private const int ScreenshotDetailLineCount = 5;
        private const int RecordingDetailLineCount = 6;
        private const int ScreenshotPanelHeight =
            DetailTop + DetailLineHeight * ScreenshotDetailLineCount + PanelPadding;
        private const int RecordingPanelHeight =
            DetailTop + DetailLineHeight * RecordingDetailLineCount + PanelPadding;
        private const int PanelGap = 18;
        private const int PanelSelectionGap = 10;
        private const int ZoneDragHoldMilliseconds = 300;
        private const int SuccessFlashMilliseconds = 130;
        private const int LiveOcrPollMilliseconds = 35;
        private const int LiveOcrAnimationIntervalMilliseconds = 16;
        private const int LiveOcrMinimumAnimationMilliseconds = 6500;
        private const int LiveOcrMaximumAnimationMilliseconds = 20000;
        private const double LiveOcrAnimationPixelsPerSecond = 135.0;
        private const double EstimatedPngBytesPerPixel = 0.65;
        private const double EstimatedMjpegBytesPerPixel = 0.20;

        private enum InteractionState
        {
            Inspecting,
            Pressed,
            Dragging,
            Flashing,
            Finished
        }

        private readonly CapturedFrame frame;
        private readonly ZoneDetector zones;
        private readonly ShortcutSettings shortcuts;
        private readonly Font detailFont;
        private readonly Font monoFont;
        private readonly System.Windows.Forms.Timer zoneRefreshTimer;
        private readonly System.Windows.Forms.Timer pointerZoneTimer;
        private readonly System.Windows.Forms.Timer successFlashTimer;
        private readonly System.Windows.Forms.Timer liveOcrTimer;
        private readonly System.Windows.Forms.Timer liveOcrAnimationTimer;
        private readonly BufferedGraphicsContext renderBufferContext;
        private readonly PresentationClock panelMotionTimer;
        private FloatingPanelForm floatingPreview;
        private Bitmap panelSurface;
        private Point panelSurfacePointer;
        private Rectangle panelSurfaceTarget;
        private int panelSurfaceSensitivity;
        private bool panelSurfaceRecording;
        private bool panelTargetDirty;
        private bool pointerVisualDirty;
        private Point lastPaintedPointer;
        private Region nativePaintRegion;
        private readonly bool panelAnimationEnabled;
        private PointF panelPosition;
        private Rectangle panelMotionTarget;
        private bool panelPositionInitialized;
        private long panelMotionTimestamp;
        private InteractionState state;
        private Point pointerScreen;
        private Point anchorScreen;
        private Zone hoverZone;
        private Zone lockedZone;
        private Rectangle manualSelection;
        private long pressStartedTimestamp;
        private bool pressStartedOverDetectedZone;
        private Rectangle pendingSelection;
        private string pendingHexCode;
        private OcrRecognition pendingTextRecognition;
        private Rectangle successFlashBounds;
        private Color successFlashColor;
        private bool successFlashIsColour;
        private bool successFlashIsText;
        private bool successFlashIsRecording;
        private bool recordingMode;
        private Rectangle recordingSelection;
        private Point lastZoneLookupPoint;
        private int zoneLookupInFlight;
        private volatile bool disposingOverlay;
        private int finishGate;
        private int keyboardSensitivityPercent;
        private double horizontalMouseRemainder;
        private double verticalMouseRemainder;
        private Point lastPhysicalMouseScreen;
        private bool mouseTrackingInitialized;
        private Rectangle liveOcrRequestedBounds;
        private Rectangle liveOcrLastAttemptedBounds;
        private Rectangle liveOcrRecognitionBounds;
        private OcrRecognition liveOcrRecognition;
        private int liveOcrInFlight;
        private int liveOcrGeneration;
        private long liveOcrAnimationStartedTimestamp;

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr cursor);

        internal OverlayForm(CapturedFrame capturedFrame, ZoneDetector zoneDetector)
            : this(capturedFrame, zoneDetector, ShortcutSettings.Load())
        {
        }

        internal OverlayForm(
            CapturedFrame capturedFrame,
            ZoneDetector zoneDetector,
            ShortcutSettings shortcutSettings)
        {
            if (capturedFrame == null)
            {
                throw new ArgumentNullException("capturedFrame");
            }
            if (zoneDetector == null)
            {
                throw new ArgumentNullException("zoneDetector");
            }
            if (shortcutSettings == null)
            {
                throw new ArgumentNullException("shortcutSettings");
            }

            frame = capturedFrame;
            zones = zoneDetector;
            shortcuts = shortcutSettings;
            state = InteractionState.Inspecting;
            pointerScreen = frame.DesktopBounds.Location;
            keyboardSensitivityPercent = DefaultKeyboardSensitivityPercent;

            detailFont = OpenCaptureTheme.CreateFont(9.0f, FontStyle.Regular);
            monoFont = OpenCaptureTheme.CreateFont(10.0f, FontStyle.Bold);
            zoneRefreshTimer = new System.Windows.Forms.Timer();
            zoneRefreshTimer.Interval = 75;
            zoneRefreshTimer.Tick += RefreshRefinedZone;
            pointerZoneTimer = new System.Windows.Forms.Timer();
            // Zone boundaries do not need to be recomputed at the mouse report
            // rate. Thirty updates per second keeps targets responsive without
            // letting colour analysis compete with the application underneath.
            pointerZoneTimer.Interval = 33;
            pointerZoneTimer.Tick += RefreshPointerZone;
            successFlashTimer = new System.Windows.Forms.Timer();
            successFlashTimer.Interval = SuccessFlashMilliseconds;
            successFlashTimer.Tick += FinishSuccessFlash;
            liveOcrTimer = new System.Windows.Forms.Timer();
            liveOcrTimer.Interval = LiveOcrPollMilliseconds;
            liveOcrTimer.Tick += RefreshLiveOcr;
            liveOcrAnimationTimer = new System.Windows.Forms.Timer();
            liveOcrAnimationTimer.Interval = LiveOcrAnimationIntervalMilliseconds;
            liveOcrAnimationTimer.Tick += AnimateLiveOcr;
            renderBufferContext = new BufferedGraphicsContext();
            panelAnimationEnabled = PreviewMotion.AnimationsEnabled;
            panelMotionTimer = new PresentationClock(this);
            panelMotionTimer.Interval = 8;
            panelMotionTimer.Tick += AnimateInformationPanel;

            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            FormBorderStyle = FormBorderStyle.None;
            KeyPreview = true;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "OpenCaptureOverlay";
            Text = "OpenCapture";
            AccessibleName = "OpenCapture screenshot overlay";
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            // The overlay can span several high-resolution monitors. A full-form
            // back buffer forces tens of megabytes to be copied on every mouse
            // move, even though only the cursor and precision panel changed.
            DoubleBuffered = false;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.Opaque,
                true);
            Bounds = frame.DesktopBounds;
        }

        internal event EventHandler<SelectionEventArgs> SelectionCompleted;
        internal event EventHandler<ColorCopyEventArgs> ColorCopyRequested;
        internal event EventHandler<TextCopyRequestEventArgs> TextCopyRequested;
        internal event EventHandler<RecordingRequestEventArgs> RecordingRequested;
        internal event EventHandler SelectionCancelled;

        internal bool IsRecordingMode
        {
            get { return recordingMode; }
        }

        internal int KeyboardSensitivityPercent
        {
            get { return keyboardSensitivityPercent; }
        }

        internal Point PrecisionPointer
        {
            get { return pointerScreen; }
        }

        internal void ToggleCaptureMode()
        {
            if (state == InteractionState.Finished || state == InteractionState.Flashing)
            {
                return;
            }

            recordingMode = !recordingMode;
            recordingSelection = Rectangle.Empty;
            manualSelection = Rectangle.Empty;
            lockedZone = null;
            ClearLiveOcr(true);
            state = InteractionState.Inspecting;
            QueueZoneLookup(pointerScreen, true);
            RetargetInformationPanel(false);
            Invalidate();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return parameters;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return false; }
        }

        protected override void OnShown(EventArgs eventArgs)
        {
            base.OnShown(eventArgs);
            Bounds = frame.DesktopBounds;
            Activate();
            Focus();

            Point current;
            if (NativeMethods.TryGetPhysicalCursorPosition(out current))
            {
                pointerScreen = ClampToDesktop(current);
                lastPhysicalMouseScreen = current;
                mouseTrackingInitialized = true;
            }
            hoverZone = null;
            lastZoneLookupPoint = new Point(int.MinValue, int.MinValue);
            pointerZoneTimer.Start();
            liveOcrTimer.Start();
            QueueZoneLookup(pointerScreen, true);
            if (!zones.IsRefinementComplete)
            {
                zoneRefreshTimer.Start();
            }
            SetCursor(IntPtr.Zero);
            RetargetInformationPanel(true);
            lastPaintedPointer = pointerScreen;
            floatingPreview = new FloatingPanelForm(true);
            floatingPreview.Text = "OpenCapture precision preview";
            floatingPreview.ClientSize = new Size(PanelWidth + 7, ScreenshotPanelHeight + 8);
            UpdateFloatingPreview();
            floatingPreview.Show(this);
            Invalidate();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x000F)
            {
                using (Region update = DirtyPaintRegion.Read(Handle))
                {
                    nativePaintRegion = update;
                    try { base.WndProc(ref message); }
                    finally { nativePaintRegion = null; }
                }
                return;
            }
            if (message.Msg == WM_SETCURSOR && state != InteractionState.Finished)
            {
                SetCursor(IntPtr.Zero);
                message.Result = new IntPtr(1);
                return;
            }

            base.WndProc(ref message);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (shortcuts.CopyColourShortcut.Matches(keyData))
            {
                if (state == InteractionState.Dragging &&
                    manualSelection.Width > 0 && manualSelection.Height > 0)
                {
                    CompleteTextCopy(manualSelection);
                }
                else
                {
                    // Hovered zones, including shell zones, are deliberately
                    // passive. Ctrl+C only becomes OCR after the user has
                    // crossed into an active manual drag.
                    CompleteColorCopy();
                }
                return true;
            }

            if ((keyData & Keys.KeyCode) == Keys.Escape)
            {
                CancelSelection();
                return true;
            }

            if (shortcuts.RecordingShortcut.Matches(keyData))
            {
                ToggleCaptureMode();
                return true;
            }

            if (recordingMode && shortcuts.RecordingControlShortcut.Matches(keyData))
            {
                StartRecording(CurrentTargetBounds());
                return true;
            }

            if (shortcuts.CaptureZoneShortcut.Matches(keyData) || keyData == Keys.Enter)
            {
                Rectangle selection = CurrentTargetBounds();
                if (selection.Width > 0 && selection.Height > 0)
                {
                    if (recordingMode)
                    {
                        StartRecording(selection);
                    }
                    else
                    {
                        CompleteSelection(selection);
                    }
                }
                return true;
            }

            if ((keyData & Keys.Modifiers) == Keys.None &&
                MovePointerWithKeyboard(keyData & Keys.KeyCode))
            {
                return true;
            }

            return base.ProcessCmdKey(ref message, keyData);
        }

        protected override void OnMouseMove(MouseEventArgs eventArgs)
        {
            long eventTimestamp = Stopwatch.GetTimestamp();
            base.OnMouseMove(eventArgs);
            if (state == InteractionState.Finished || state == InteractionState.Flashing)
            {
                return;
            }

            Point previousPointer = pointerScreen;
            Rectangle previousTarget = CurrentTargetBounds();
            ApplyPhysicalMousePosition(ClientToDesktop(eventArgs.Location));
            if (state == InteractionState.Inspecting)
            {
                if (hoverZone != null && !hoverZone.Bounds.Contains(pointerScreen))
                {
                    hoverZone = null;
                }
            }
            else if (state == InteractionState.Pressed &&
                ShouldTransitionToDrag(
                    pressStartedOverDetectedZone,
                    HasLeftDragThreshold(anchorScreen, pointerScreen),
                    ElapsedPressTicks(eventTimestamp),
                    Stopwatch.Frequency))
            {
                state = InteractionState.Dragging;
                lockedZone = null;
                manualSelection = MakeInclusiveSelection(anchorScreen, pointerScreen);
            }
            else if (state == InteractionState.Dragging)
            {
                manualSelection = MakeInclusiveSelection(anchorScreen, pointerScreen);
            }

            if (state == InteractionState.Dragging)
            {
                TrackLiveOcrSelection(manualSelection);
            }

            SetCursor(IntPtr.Zero);
            InvalidatePointerChange(previousPointer, previousTarget);
        }

        protected override void OnMouseWheel(MouseEventArgs eventArgs)
        {
            base.OnMouseWheel(eventArgs);
            if (state == InteractionState.Finished || state == InteractionState.Flashing ||
                eventArgs.Delta == 0)
            {
                return;
            }

            AdjustKeyboardSensitivity(eventArgs.Delta);
            RetargetInformationPanel(false);
        }

        internal void AdjustKeyboardSensitivity(int wheelDelta)
        {
            if (wheelDelta == 0)
            {
                return;
            }

            int notches = Math.Max(1, Math.Abs(wheelDelta) / 120);
            int direction = wheelDelta > 0 ? 1 : -1;
            keyboardSensitivityPercent = Math.Max(
                MinimumKeyboardSensitivityPercent,
                Math.Min(
                    MaximumKeyboardSensitivityPercent,
                    keyboardSensitivityPercent +
                    direction * notches * KeyboardSensitivityStepPercent));
            horizontalMouseRemainder = 0.0;
            verticalMouseRemainder = 0.0;
            Point physicalPointer;
            if (NativeMethods.TryGetPhysicalCursorPosition(out physicalPointer))
            {
                lastPhysicalMouseScreen = physicalPointer;
                mouseTrackingInitialized = true;
            }
        }

        internal void ResetMouseSensitivity()
        {
            keyboardSensitivityPercent = DefaultKeyboardSensitivityPercent;
            horizontalMouseRemainder = 0.0;
            verticalMouseRemainder = 0.0;
            Point physicalPointer;
            if (NativeMethods.TryGetPhysicalCursorPosition(out physicalPointer))
            {
                lastPhysicalMouseScreen = physicalPointer;
                mouseTrackingInitialized = true;
            }
        }

        internal bool MovePointerWithKeyboard(Keys keyCode)
        {
            if (state != InteractionState.Inspecting)
            {
                return false;
            }

            int directionX = 0;
            int directionY = 0;
            if (keyCode == Keys.Left || keyCode == Keys.A) directionX = -1;
            if (keyCode == Keys.Right || keyCode == Keys.D) directionX = 1;
            if (keyCode == Keys.Up || keyCode == Keys.W) directionY = -1;
            if (keyCode == Keys.Down || keyCode == Keys.S) directionY = 1;
            if (directionX == 0 && directionY == 0)
            {
                return false;
            }

            Point previousPointer = pointerScreen;
            Rectangle previousTarget = CurrentTargetBounds();
            pointerScreen = ClampToDesktop(new Point(
                pointerScreen.X + directionX,
                pointerScreen.Y + directionY));
            if (pointerScreen == previousPointer)
            {
                return true;
            }

            if (hoverZone != null && !hoverZone.Bounds.Contains(pointerScreen))
            {
                hoverZone = null;
            }
            QueueZoneLookup(pointerScreen, true);
            SetCursor(IntPtr.Zero);
            InvalidatePointerChange(previousPointer, previousTarget);
            RetargetInformationPanel(true);
            return true;
        }

        private void ApplyPhysicalMousePosition(Point physicalPosition)
        {
            if (!mouseTrackingInitialized)
            {
                lastPhysicalMouseScreen = physicalPosition;
                mouseTrackingInitialized = true;
                return;
            }

            int deltaX = physicalPosition.X - lastPhysicalMouseScreen.X;
            int deltaY = physicalPosition.Y - lastPhysicalMouseScreen.Y;
            lastPhysicalMouseScreen = physicalPosition;
            ApplyPhysicalMouseDelta(deltaX, deltaY);
        }

        internal void ApplyPhysicalMouseDelta(int deltaX, int deltaY)
        {
            double scale = keyboardSensitivityPercent / 100.0;
            horizontalMouseRemainder += deltaX * scale;
            verticalMouseRemainder += deltaY * scale;
            int movementX = (int)Math.Truncate(horizontalMouseRemainder);
            int movementY = (int)Math.Truncate(verticalMouseRemainder);
            horizontalMouseRemainder -= movementX;
            verticalMouseRemainder -= movementY;
            if (movementX == 0 && movementY == 0)
            {
                return;
            }
            pointerScreen = ClampToDesktop(new Point(
                pointerScreen.X + movementX,
                pointerScreen.Y + movementY));
        }

        protected override void OnMouseDown(MouseEventArgs eventArgs)
        {
            long eventTimestamp = Stopwatch.GetTimestamp();
            base.OnMouseDown(eventArgs);
            if (state == InteractionState.Finished || state == InteractionState.Flashing)
            {
                return;
            }

            if (eventArgs.Button == MouseButtons.Middle)
            {
                ResetMouseSensitivity();
                RetargetInformationPanel(false);
                return;
            }

            if (shortcuts.CopyColourShortcut.Matches(eventArgs.Button))
            {
                CompleteColorCopy();
                return;
            }

            if (shortcuts.CaptureZoneShortcut.Matches(eventArgs.Button) &&
                eventArgs.Button != MouseButtons.Left)
            {
                Rectangle target = CurrentTargetBounds();
                if (target.Width > 0 && target.Height > 0)
                {
                    if (recordingMode)
                    {
                        StartRecording(target);
                    }
                    else
                    {
                        CompleteSelection(target);
                    }
                }
                return;
            }

            if (eventArgs.Button == MouseButtons.Right)
            {
                CancelSelection();
                return;
            }

            if (eventArgs.Button != MouseButtons.Left || state != InteractionState.Inspecting)
            {
                return;
            }

            ApplyPhysicalMousePosition(ClientToDesktop(eventArgs.Location));
            anchorScreen = pointerScreen;
            if (recordingMode)
            {
                recordingSelection = Rectangle.Empty;
            }
            lockedZone = hoverZone;
            if (lockedZone == null || !lockedZone.Bounds.Contains(pointerScreen))
            {
                lockedZone = FilterSelectableZone(
                    zones.FindZoneAt(pointerScreen),
                    pointerScreen);
            }
            pressStartedTimestamp = eventTimestamp;
            pressStartedOverDetectedZone =
                lockedZone != null && shortcuts.CaptureZoneShortcut.Matches(MouseButtons.Left);
            state = InteractionState.Pressed;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs eventArgs)
        {
            long eventTimestamp = Stopwatch.GetTimestamp();
            base.OnMouseUp(eventArgs);
            if (eventArgs.Button != MouseButtons.Left ||
                state == InteractionState.Finished ||
                state == InteractionState.Flashing)
            {
                return;
            }

            ApplyPhysicalMousePosition(ClientToDesktop(eventArgs.Location));
            Capture = false;
            if (state == InteractionState.Dragging)
            {
                manualSelection = MakeInclusiveSelection(anchorScreen, pointerScreen);
                if (recordingMode)
                {
                    ClearLiveOcr(true);
                    recordingSelection = Rectangle.Intersect(
                        frame.DesktopBounds,
                        manualSelection);
                    state = InteractionState.Inspecting;
                    lockedZone = null;
                    hoverZone = null;
                    Invalidate();
                }
                else
                {
                    CompleteSelection(manualSelection);
                }
                return;
            }

            if (state == InteractionState.Pressed)
            {
                if (ShouldTransitionToDrag(
                    pressStartedOverDetectedZone,
                    HasLeftDragThreshold(anchorScreen, pointerScreen),
                    ElapsedPressTicks(eventTimestamp),
                    Stopwatch.Frequency))
                {
                    manualSelection = MakeInclusiveSelection(anchorScreen, pointerScreen);
                    if (recordingMode)
                    {
                        ClearLiveOcr(true);
                        recordingSelection = Rectangle.Intersect(
                            frame.DesktopBounds,
                            manualSelection);
                        state = InteractionState.Inspecting;
                        lockedZone = null;
                        hoverZone = null;
                        Invalidate();
                    }
                    else
                    {
                        CompleteSelection(manualSelection);
                    }
                    return;
                }

                if (shortcuts.CaptureZoneShortcut.Matches(MouseButtons.Left))
                {
                    Rectangle selection = lockedZone == null
                        ? MakeInclusiveSelection(anchorScreen, anchorScreen)
                        : lockedZone.Bounds;
                    if (recordingMode)
                    {
                        StartRecording(selection);
                    }
                    else
                    {
                        CompleteSelection(selection);
                    }
                }
                else
                {
                    state = InteractionState.Inspecting;
                    lockedZone = null;
                    Invalidate();
                }
            }
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            Rectangle clip = Rectangle.Intersect(ClientRectangle, eventArgs.ClipRectangle);
            if (clip.Width <= 0 || clip.Height <= 0)
            {
                return;
            }
            foreach (Rectangle tile in DirtyPaintRegion.Tiles(nativePaintRegion, clip))
            {
                using (BufferedGraphics buffer = renderBufferContext.Allocate(eventArgs.Graphics, tile))
                {
                    DrawOverlay(buffer.Graphics, tile);
                    buffer.Render(eventArgs.Graphics);
                }
            }
        }

        private void DrawOverlay(Graphics graphics, Rectangle clip)
        {
            graphics.SetClip(clip);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImage(
                frame.Bitmap,
                clip,
                clip.X,
                clip.Y,
                clip.Width,
                clip.Height,
                GraphicsUnit.Pixel);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;

            if (state == InteractionState.Flashing)
            {
                DrawSuccessFlash(graphics);
                return;
            }

            Rectangle target = CurrentTargetBounds();
            double liveOcrPhase = GetLiveOcrAnimationPhase(liveOcrRecognition);
            if (target.Width > 0 && target.Height > 0)
            {
                DrawSelectionHighlight(graphics, DesktopToClient(target));
            }

            DrawLiveOcrHighlights(graphics, liveOcrPhase);

            DrawCrosshair(graphics, DesktopToClient(pointerScreen));
            if (floatingPreview == null) DrawInformationPanel(graphics, target);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposingOverlay)
            {
                disposingOverlay = true;
                if (floatingPreview != null) floatingPreview.Dispose();
                if (panelSurface != null) panelSurface.Dispose();
                panelMotionTimer.Stop();
                panelMotionTimer.Tick -= AnimateInformationPanel;
                panelMotionTimer.Dispose();
                pointerZoneTimer.Stop();
                pointerZoneTimer.Tick -= RefreshPointerZone;
                pointerZoneTimer.Dispose();
                zoneRefreshTimer.Stop();
                zoneRefreshTimer.Tick -= RefreshRefinedZone;
                zoneRefreshTimer.Dispose();
                successFlashTimer.Stop();
                successFlashTimer.Tick -= FinishSuccessFlash;
                successFlashTimer.Dispose();
                liveOcrTimer.Stop();
                liveOcrTimer.Tick -= RefreshLiveOcr;
                liveOcrTimer.Dispose();
                liveOcrAnimationTimer.Stop();
                liveOcrAnimationTimer.Tick -= AnimateLiveOcr;
                liveOcrAnimationTimer.Dispose();
                Interlocked.Increment(ref liveOcrGeneration);
                detailFont.Dispose();
                monoFont.Dispose();
                renderBufferContext.Dispose();
                zones.Dispose();
            }
            base.Dispose(disposing);
        }

        private Rectangle CurrentTargetBounds()
        {
            if (recordingMode && recordingSelection.Width > 0 &&
                recordingSelection.Height > 0)
            {
                return Rectangle.Intersect(frame.DesktopBounds, recordingSelection);
            }
            if (state == InteractionState.Dragging)
            {
                return Rectangle.Intersect(frame.DesktopBounds, manualSelection);
            }
            if (state == InteractionState.Pressed && lockedZone != null)
            {
                return Rectangle.Intersect(frame.DesktopBounds, lockedZone.Bounds);
            }
            if (hoverZone != null)
            {
                return Rectangle.Intersect(frame.DesktopBounds, hoverZone.Bounds);
            }
            return MakeInclusiveSelection(pointerScreen, pointerScreen);
        }

        private void CompleteSelection(Rectangle desktopSelection)
        {
            Rectangle clipped = Rectangle.Intersect(frame.DesktopBounds, desktopSelection);
            if (clipped.Width <= 0 || clipped.Height <= 0)
            {
                return;
            }
            if (Interlocked.Exchange(ref finishGate, 1) != 0)
            {
                return;
            }

            ClearLiveOcr(true);
            pendingSelection = clipped;
            pendingHexCode = null;
            pendingTextRecognition = null;
            successFlashIsRecording = false;
            successFlashIsText = false;
            successFlashBounds = clipped;
            successFlashIsColour = false;
            state = InteractionState.Flashing;
            HideFloatingPresentation();
            zoneRefreshTimer.Stop();
            Capture = false;
            successFlashTimer.Start();
            Invalidate();
        }

        private void StartRecording(Rectangle desktopSelection)
        {
            Rectangle clipped = Rectangle.Intersect(frame.DesktopBounds, desktopSelection);
            if (clipped.Width <= 0 || clipped.Height <= 0)
            {
                return;
            }
            if (Interlocked.Exchange(ref finishGate, 1) != 0)
            {
                return;
            }

            ClearLiveOcr(true);
            pendingSelection = clipped;
            pendingHexCode = null;
            pendingTextRecognition = null;
            successFlashBounds = clipped;
            successFlashIsColour = false;
            successFlashIsText = false;
            successFlashIsRecording = true;
            state = InteractionState.Flashing;
            HideFloatingPresentation();
            zoneRefreshTimer.Stop();
            Capture = false;
            successFlashTimer.Start();
            Invalidate();
        }

        private void CancelSelection()
        {
            if (Interlocked.Exchange(ref finishGate, 1) != 0)
            {
                return;
            }

            ClearLiveOcr(true);
            state = InteractionState.Finished;
            HideFloatingPresentation();
            zoneRefreshTimer.Stop();
            successFlashTimer.Stop();
            Capture = false;
            Hide();
            Cursor.Current = Cursors.Default;
            EventHandler handler = SelectionCancelled;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void CompleteColorCopy()
        {
            if (Interlocked.Exchange(ref finishGate, 1) != 0)
            {
                return;
            }

            ClearLiveOcr(true);
            Color sampled = frame.GetPixelAtDesktopPoint(pointerScreen);
            string hexCode = string.Format(
                "#{0:X2}{1:X2}{2:X2}",
                sampled.R,
                sampled.G,
                sampled.B);

            pendingSelection = Rectangle.Empty;
            pendingHexCode = hexCode;
            pendingTextRecognition = null;
            successFlashIsRecording = false;
            successFlashIsText = false;
            successFlashColor = sampled;
            successFlashBounds = new Rectangle(
                pointerScreen.X - 24,
                pointerScreen.Y - 24,
                48,
                48);
            successFlashBounds = Rectangle.Intersect(frame.DesktopBounds, successFlashBounds);
            successFlashIsColour = true;
            state = InteractionState.Flashing;
            HideFloatingPresentation();
            zoneRefreshTimer.Stop();
            Capture = false;
            successFlashTimer.Start();
            Invalidate();
        }

        private void CompleteTextCopy(Rectangle desktopSelection)
        {
            Rectangle clipped = Rectangle.Intersect(frame.DesktopBounds, desktopSelection);
            if (clipped.Width <= 0 || clipped.Height <= 0)
            {
                return;
            }
            if (Interlocked.Exchange(ref finishGate, 1) != 0)
            {
                return;
            }

            OcrRecognition recognition =
                liveOcrRecognition != null && liveOcrRecognitionBounds == clipped
                    ? liveOcrRecognition
                    : null;
            ClearLiveOcr(true);
            pendingSelection = clipped;
            pendingHexCode = null;
            pendingTextRecognition = recognition;
            successFlashBounds = clipped;
            successFlashIsColour = false;
            successFlashIsRecording = false;
            successFlashIsText = true;
            state = InteractionState.Flashing;
            HideFloatingPresentation();
            zoneRefreshTimer.Stop();
            Capture = false;
            successFlashTimer.Start();
            Invalidate();
        }

        private void FinishSuccessFlash(object sender, EventArgs eventArgs)
        {
            successFlashTimer.Stop();
            if (state != InteractionState.Flashing)
            {
                return;
            }

            state = InteractionState.Finished;
            Hide();
            Cursor.Current = Cursors.Default;
            if (successFlashIsColour)
            {
                EventHandler<ColorCopyEventArgs> colourHandler = ColorCopyRequested;
                if (colourHandler != null)
                {
                    colourHandler(this, new ColorCopyEventArgs(pendingHexCode));
                }
            }
            else if (successFlashIsText)
            {
                EventHandler<TextCopyRequestEventArgs> textHandler = TextCopyRequested;
                if (textHandler != null)
                {
                    textHandler(this, new TextCopyRequestEventArgs(
                        pendingSelection,
                        pendingTextRecognition));
                }
            }
            else if (successFlashIsRecording)
            {
                EventHandler<RecordingRequestEventArgs> recordingHandler = RecordingRequested;
                if (recordingHandler != null)
                {
                    recordingHandler(this, new RecordingRequestEventArgs(pendingSelection));
                }
            }
            else
            {
                EventHandler<SelectionEventArgs> selectionHandler = SelectionCompleted;
                if (selectionHandler != null)
                {
                    selectionHandler(this, new SelectionEventArgs(pendingSelection));
                }
            }
        }

        private void HideFloatingPresentation()
        {
            panelMotionTimer.Stop();
            if (floatingPreview != null) floatingPreview.Hide();
        }

        private void DrawSuccessFlash(Graphics graphics)
        {
            Rectangle visible = Rectangle.Intersect(
                ClientRectangle,
                DesktopToClient(successFlashBounds));
            if (visible.Width <= 0 || visible.Height <= 0)
            {
                return;
            }

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color activeAccent = successFlashIsRecording
                ? Color.FromArgb(238, 42, 52)
                : OpenCaptureTheme.Accent;
            Color fillColour = successFlashIsColour
                ? Color.FromArgb(235, successFlashColor)
                : Color.FromArgb(92, activeAccent);
            using (SolidBrush fill = new SolidBrush(fillColour))
            using (Pen white = new Pen(Color.FromArgb(245, Color.White), 3.0f))
            using (Pen accent = new Pen(Color.FromArgb(250, activeAccent), 2.0f))
            {
                if (successFlashIsColour)
                {
                    using (GraphicsPath path = OpenCaptureTheme.CreateRoundedRectangle(visible, 8))
                    {
                        graphics.FillPath(fill, path);
                        graphics.DrawPath(white, path);
                        Rectangle inset = visible;
                        inset.Inflate(-3, -3);
                        using (GraphicsPath insetPath = OpenCaptureTheme.CreateRoundedRectangle(inset, 6))
                        {
                            graphics.DrawPath(accent, insetPath);
                        }
                    }
                }
                else
                {
                    graphics.FillRectangle(fill, visible);
                    Rectangle outline = visible;
                    outline.Width = Math.Max(1, outline.Width - 1);
                    outline.Height = Math.Max(1, outline.Height - 1);
                    graphics.DrawRectangle(white, outline);
                    outline.Inflate(-2, -2);
                    if (outline.Width > 0 && outline.Height > 0)
                    {
                        graphics.DrawRectangle(accent, outline);
                    }
                }
            }
        }

        private void DrawSelectionHighlight(Graphics graphics, Rectangle bounds)
        {
            Rectangle visible = Rectangle.Intersect(ClientRectangle, bounds);
            if (visible.Width <= 0 || visible.Height <= 0)
            {
                return;
            }

            Color activeAccent = recordingMode
                ? Color.FromArgb(238, 42, 52)
                : OpenCaptureTheme.Accent;
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(42, activeAccent)))
            using (Pen outer = new Pen(Color.FromArgb(245, activeAccent), 2.0f))
            using (Pen inner = new Pen(Color.FromArgb(230, Color.White), 1.0f))
            {
                graphics.FillRectangle(fill, visible);
                Rectangle outline = new Rectangle(
                    visible.Left,
                    visible.Top,
                    Math.Max(0, visible.Width - 1),
                    Math.Max(0, visible.Height - 1));
                if (outline.Width > 0 && outline.Height > 0)
                {
                    graphics.DrawRectangle(outer, outline);
                    if (outline.Width > 3 && outline.Height > 3)
                    {
                        outline.Inflate(-2, -2);
                        graphics.DrawRectangle(inner, outline);
                    }
                }
            }
        }

        private void DrawLiveOcrHighlights(Graphics graphics, double phase)
        {
            if (!HasLiveOcrText || liveOcrRecognitionBounds.IsEmpty)
            {
                return;
            }

            List<RectangleF> regions = new List<RectangleF>();
            foreach (OcrTextRegion region in liveOcrRecognition.Regions)
            {
                RectangleF local = new RectangleF(
                    liveOcrRecognitionBounds.Left - frame.DesktopBounds.Left +
                        region.Bounds.Left,
                    liveOcrRecognitionBounds.Top - frame.DesktopBounds.Top +
                        region.Bounds.Top,
                    region.Bounds.Width,
                    region.Bounds.Height);
                RectangleF visible = RectangleF.Intersect(
                    local,
                    new RectangleF(
                        ClientRectangle.X,
                        ClientRectangle.Y,
                        ClientRectangle.Width,
                        ClientRectangle.Height));
                if (visible.Width > 0.0f && visible.Height > 0.0f)
                {
                    regions.Add(visible);
                }
            }
            DrawFlowingHighlights(graphics, regions, phase, frame.Bitmap);
        }

        private static void DrawFlowingHighlights(
            Graphics graphics,
            IList<RectangleF> regions,
            double phase,
            Bitmap contrastSource)
        {
            if (regions == null || regions.Count == 0)
            {
                return;
            }

            double totalWidth = 0.0;
            foreach (RectangleF region in regions)
            {
                totalWidth += Math.Max(1.0, region.Width);
            }
            double bandWidth = Math.Max(42.0, Math.Min(160.0, totalWidth * 0.24));
            double bandCenter = phase * totalWidth;
            using (SolidBrush baseHighlight = new SolidBrush(
                Color.FromArgb(88, OpenCaptureTheme.Accent)))
            using (SolidBrush flowingHighlight = new SolidBrush(
                Color.FromArgb(1, OpenCaptureTheme.Accent)))
            using (ImageAttributes contrastAttributes = new ImageAttributes())
            {
                ColorMatrix contrastMatrix = new ColorMatrix(new float[][]
                {
                    new float[] { 1.9f, 0.0f, 0.0f, 0.0f, 0.0f },
                    new float[] { 0.0f, 1.9f, 0.0f, 0.0f, 0.0f },
                    new float[] { 0.0f, 0.0f, 1.9f, 0.0f, 0.0f },
                    new float[] { 0.0f, 0.0f, 0.0f, 0.5f, 0.0f },
                    new float[] { -0.45f, -0.45f, -0.45f, 0.0f, 1.0f }
                });
                foreach (RectangleF region in regions)
                {
                    graphics.FillRectangle(baseHighlight, region);
                }

                double regionStart = 0.0;
                foreach (RectangleF region in regions)
                {
                    int wrap;
                    for (wrap = -1; wrap <= 1; wrap++)
                    {
                        double bandStart = bandCenter - bandWidth * 0.5 +
                            wrap * totalWidth;
                        double intersectionStart = Math.Max(regionStart, bandStart);
                        double intersectionEnd = Math.Min(
                            regionStart + region.Width,
                            bandStart + bandWidth);
                        if (intersectionEnd > intersectionStart)
                        {
                            double length = intersectionEnd - intersectionStart;
                            int slices = Math.Max(
                                1,
                                Math.Min(18, (int)Math.Ceiling(length / 4.0)));
                            int slice;
                            for (slice = 0; slice < slices; slice++)
                            {
                                double sliceStart = intersectionStart +
                                    length * slice / slices;
                                double sliceEnd = intersectionStart +
                                    length * (slice + 1) / slices;
                                double sliceMiddle = (sliceStart + sliceEnd) * 0.5;
                                double distance = Math.Min(
                                    1.0,
                                    Math.Abs(sliceMiddle - bandCenter -
                                        wrap * totalWidth) /
                                        Math.Max(1.0, bandWidth * 0.5));
                                double eased = 0.5 +
                                    0.5 * Math.Cos(distance * Math.PI);
                                flowingHighlight.Color = Color.FromArgb(
                                    70 + (int)Math.Round(140.0 * eased),
                                    OpenCaptureTheme.Accent);
                                RectangleF sweepSlice = new RectangleF(
                                    region.X + (float)(sliceStart - regionStart),
                                    region.Y,
                                    (float)(sliceEnd - sliceStart + 0.35),
                                    region.Height);
                                graphics.FillRectangle(flowingHighlight, sweepSlice);
                            }
                            if (contrastSource != null)
                            {
                                contrastMatrix.Matrix33 = 0.62f;
                                contrastAttributes.SetColorMatrix(
                                    contrastMatrix,
                                    ColorMatrixFlag.Default,
                                    ColorAdjustType.Bitmap);
                                DrawContrastEnhancedSlice(
                                    graphics,
                                    contrastSource,
                                    new RectangleF(
                                        region.X + (float)(
                                            intersectionStart - regionStart),
                                        region.Y,
                                        (float)(intersectionEnd -
                                            intersectionStart),
                                        region.Height),
                                    contrastAttributes);
                            }
                        }
                    }
                    regionStart += region.Width;
                }
            }
        }

        private static void DrawContrastEnhancedSlice(
            Graphics graphics,
            Bitmap source,
            RectangleF slice,
            ImageAttributes attributes)
        {
            Rectangle pixels = Rectangle.Intersect(
                new Rectangle(0, 0, source.Width, source.Height),
                Rectangle.FromLTRB(
                    (int)Math.Floor(slice.Left),
                    (int)Math.Floor(slice.Top),
                    (int)Math.Ceiling(slice.Right),
                    (int)Math.Ceiling(slice.Bottom)));
            if (pixels.Width <= 0 || pixels.Height <= 0)
            {
                return;
            }
            graphics.DrawImage(
                source,
                pixels,
                pixels.Left,
                pixels.Top,
                pixels.Width,
                pixels.Height,
                GraphicsUnit.Pixel,
                attributes);
        }

        private double GetLiveOcrAnimationPhase(OcrRecognition recognition)
        {
            if (recognition == null || recognition.Regions.Count == 0)
            {
                return 0.0;
            }
            double duration = CalculateLiveOcrAnimationDurationMilliseconds(
                recognition.Regions);
            long started = liveOcrAnimationStartedTimestamp;
            if (started <= 0)
            {
                started = Stopwatch.GetTimestamp();
            }
            double elapsedMilliseconds =
                (Stopwatch.GetTimestamp() - started) * 1000.0 /
                Math.Max(1L, Stopwatch.Frequency);
            return (elapsedMilliseconds % duration) / duration;
        }

        internal static double CalculateLiveOcrAnimationDurationMilliseconds(
            IList<OcrTextRegion> regions)
        {
            double totalWidth = 0.0;
            if (regions != null)
            {
                foreach (OcrTextRegion region in regions)
                {
                    totalWidth += Math.Max(1.0, region.Bounds.Width);
                }
            }
            return Math.Max(
                LiveOcrMinimumAnimationMilliseconds,
                Math.Min(
                    LiveOcrMaximumAnimationMilliseconds,
                    totalWidth / LiveOcrAnimationPixelsPerSecond * 1000.0));
        }

        private bool HasLiveOcrText
        {
            get
            {
                return state == InteractionState.Dragging &&
                    liveOcrRecognition != null &&
                    !string.IsNullOrEmpty(liveOcrRecognition.Text);
            }
        }

        private void TrackLiveOcrSelection(Rectangle desktopSelection)
        {
            Rectangle clipped = Rectangle.Intersect(frame.DesktopBounds, desktopSelection);
            if (clipped == liveOcrRequestedBounds)
            {
                return;
            }

            liveOcrRequestedBounds = clipped;
            if (liveOcrRecognition != null &&
                (clipped.Left != liveOcrRecognitionBounds.Left ||
                 clipped.Top != liveOcrRecognitionBounds.Top ||
                 !clipped.Contains(liveOcrRecognitionBounds)))
            {
                InvalidateLiveOcrRegions(
                    liveOcrRecognition,
                    liveOcrRecognitionBounds);
                liveOcrRecognition = null;
                liveOcrRecognitionBounds = Rectangle.Empty;
                liveOcrAnimationStartedTimestamp = 0;
                liveOcrAnimationTimer.Stop();
            }
            StartLiveOcrRecognition();
        }

        private void RefreshLiveOcr(object sender, EventArgs eventArgs)
        {
            StartLiveOcrRecognition();
        }

        private void StartLiveOcrRecognition()
        {
            if (disposingOverlay || state != InteractionState.Dragging ||
                liveOcrRequestedBounds.Width < 24 ||
                liveOcrRequestedBounds.Height < 12 ||
                liveOcrRequestedBounds == liveOcrLastAttemptedBounds ||
                Interlocked.CompareExchange(ref liveOcrInFlight, 0, 0) != 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref liveOcrInFlight, 1, 0) != 0)
            {
                return;
            }

            Rectangle requestedBounds = liveOcrRequestedBounds;
            int generation = Interlocked.CompareExchange(
                ref liveOcrGeneration,
                0,
                0);
            liveOcrLastAttemptedBounds = requestedBounds;
            Bitmap crop;
            try
            {
                // Clone on the interface thread so the worker never reads the
                // frozen desktop bitmap while it is being painted.
                crop = frame.Crop(requestedBounds);
            }
            catch
            {
                Interlocked.Exchange(ref liveOcrInFlight, 0);
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                OcrRecognition recognition = OcrRecognition.Empty;
                try
                {
                    recognition = WindowsOcrEngine.RecognizeDetailed(crop, delegate
                    {
                        return generation == Interlocked.CompareExchange(ref liveOcrGeneration, 0, 0);
                    });
                }
                catch
                {
                    // Live highlighting is best-effort. Ctrl+C still performs a
                    // fresh OCR pass and reports a real failure to the user.
                }
                finally
                {
                    crop.Dispose();
                }

                if (disposingOverlay || !IsHandleCreated || IsDisposed)
                {
                    Interlocked.Exchange(ref liveOcrInFlight, 0);
                    return;
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        ApplyLiveOcrResult(
                            generation,
                            requestedBounds,
                            recognition);
                    });
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Exchange(ref liveOcrInFlight, 0);
                }
            });
        }

        private void ApplyLiveOcrResult(
            int generation,
            Rectangle recognizedBounds,
            OcrRecognition recognition)
        {
            Interlocked.Exchange(ref liveOcrInFlight, 0);
            if (disposingOverlay || generation != liveOcrGeneration ||
                state != InteractionState.Dragging)
            {
                return;
            }

            Rectangle current = Rectangle.Intersect(frame.DesktopBounds, manualSelection);
            if (current.Left != recognizedBounds.Left ||
                current.Top != recognizedBounds.Top ||
                !current.Contains(recognizedBounds))
            {
                StartLiveOcrRecognition();
                return;
            }

            if (recognition != null && !string.IsNullOrEmpty(recognition.Text))
            {
                bool animationAlreadyRunning = liveOcrRecognition != null &&
                    liveOcrAnimationStartedTimestamp > 0;
                InvalidateLiveOcrRegions(
                    liveOcrRecognition,
                    liveOcrRecognitionBounds);
                liveOcrRecognition = recognition;
                liveOcrRecognitionBounds = recognizedBounds;
                if (!animationAlreadyRunning)
                {
                    liveOcrAnimationStartedTimestamp = Stopwatch.GetTimestamp();
                }
                InvalidateLiveOcrRegions(
                    liveOcrRecognition,
                    liveOcrRecognitionBounds);
                liveOcrAnimationTimer.Start();
            }
            else if (liveOcrRecognition == null)
            {
                liveOcrAnimationStartedTimestamp = 0;
                liveOcrAnimationTimer.Stop();
            }
            StartLiveOcrRecognition();
        }

        private void AnimateLiveOcr(object sender, EventArgs eventArgs)
        {
            if (!HasLiveOcrText)
            {
                liveOcrAnimationTimer.Stop();
                return;
            }
            InvalidateLiveOcrRegions(liveOcrRecognition, liveOcrRecognitionBounds);
        }

        private void ClearLiveOcr(bool invalidate)
        {
            OcrRecognition oldRecognition = liveOcrRecognition;
            Rectangle oldBounds = liveOcrRecognitionBounds;
            Interlocked.Increment(ref liveOcrGeneration);
            liveOcrRequestedBounds = Rectangle.Empty;
            liveOcrLastAttemptedBounds = Rectangle.Empty;
            liveOcrRecognitionBounds = Rectangle.Empty;
            liveOcrRecognition = null;
            liveOcrAnimationStartedTimestamp = 0;
            liveOcrAnimationTimer.Stop();
            if (invalidate)
            {
                InvalidateLiveOcrRegions(oldRecognition, oldBounds);
            }
        }

        private void InvalidateLiveOcrRegions(
            OcrRecognition recognition,
            Rectangle recognizedBounds)
        {
            if (recognition == null || recognizedBounds.IsEmpty)
            {
                return;
            }
            foreach (OcrTextRegion region in recognition.Regions)
            {
                Rectangle dirty = Rectangle.Ceiling(new RectangleF(
                    recognizedBounds.Left - frame.DesktopBounds.Left + region.Bounds.Left,
                    recognizedBounds.Top - frame.DesktopBounds.Top + region.Bounds.Top,
                    region.Bounds.Width,
                    region.Bounds.Height));
                dirty.Inflate(3, 3);
                Invalidate(dirty);
            }
        }

        private void RefreshRefinedZone(object sender, EventArgs eventArgs)
        {
            if (state == InteractionState.Inspecting)
            {
                QueueZoneLookup(pointerScreen, true);
            }

            if (zones.IsRefinementComplete)
            {
                zoneRefreshTimer.Stop();
            }
        }

        private void RefreshPointerZone(object sender, EventArgs eventArgs)
        {
            if (state == InteractionState.Inspecting)
            {
                QueueZoneLookup(pointerScreen, false);
            }
        }

        private void QueueZoneLookup(Point point, bool force)
        {
            if (disposingOverlay || (!force && point == lastZoneLookupPoint))
            {
                return;
            }
            if (Interlocked.CompareExchange(ref zoneLookupInFlight, 1, 0) != 0)
            {
                return;
            }

            lastZoneLookupPoint = point;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Zone result = null;
                try
                {
                    result = zones.FindZoneAt(point);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception exception)
                {
                    ErrorLog.Write("Could not refine the hovered zone", exception);
                }
                finally
                {
                    Interlocked.Exchange(ref zoneLookupInFlight, 0);
                }

                if (disposingOverlay || !IsHandleCreated || IsDisposed)
                {
                    return;
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        ApplyZoneLookup(point, result);
                    });
                }
                catch (InvalidOperationException)
                {
                }
            });
        }

        private void ApplyZoneLookup(Point requestedPoint, Zone result)
        {
            if (!zones.IsRefinementComplete && !zoneRefreshTimer.Enabled)
            {
                zoneRefreshTimer.Start();
            }
            Zone selectableResult = FilterSelectableZone(result, requestedPoint);
            if (disposingOverlay || state != InteractionState.Inspecting ||
                requestedPoint != pointerScreen ||
                SameZone(hoverZone, selectableResult))
            {
                return;
            }

            Rectangle previousTarget = CurrentTargetBounds();
            Rectangle previousPanel = GetPanelDirtyRectangle(
                pointerScreen,
                previousTarget);
            hoverZone = selectableResult;
            RetargetInformationPanel(false);
            InvalidateTargetChange(previousTarget, CurrentTargetBounds());
            if (floatingPreview == null)
            {
                Invalidate(previousPanel);
                Invalidate(GetPanelDirtyRectangle(pointerScreen));
            }
        }

        private Zone FilterSelectableZone(Zone candidate, Point point)
        {
            return IsWholeScreenZone(
                candidate,
                GetPointerMonitorBounds(point),
                GetPointerMonitorWorkAreaBounds(point))
                    ? null
                    : candidate;
        }

        internal static bool IsWholeScreenZone(
            Zone candidate,
            Rectangle monitorBounds,
            Rectangle workAreaBounds)
        {
            if (candidate == null || monitorBounds.IsEmpty)
            {
                return false;
            }
            if (candidate.Kind == ZoneKind.Monitor ||
                candidate.Bounds.Contains(monitorBounds))
            {
                return true;
            }
            Rectangle bounds = candidate.Bounds;
            return NearlyMatchesBounds(bounds, monitorBounds) ||
                NearlyMatchesBounds(bounds, workAreaBounds) ||
                CoversNearlyAllBounds(bounds, workAreaBounds);
        }

        private static bool NearlyMatchesBounds(
            Rectangle first,
            Rectangle second)
        {
            if (first.IsEmpty || second.IsEmpty)
            {
                return false;
            }
            const int edgeTolerance = 2;
            return Math.Abs(first.Left - second.Left) <= edgeTolerance &&
                Math.Abs(first.Top - second.Top) <= edgeTolerance &&
                Math.Abs(first.Right - second.Right) <= edgeTolerance &&
                Math.Abs(first.Bottom - second.Bottom) <= edgeTolerance;
        }

        private static bool CoversNearlyAllBounds(
            Rectangle candidate,
            Rectangle reference)
        {
            if (candidate.IsEmpty || reference.IsEmpty)
            {
                return false;
            }

            // Maximized Win32 windows commonly include an invisible DWM resize
            // frame just outside the visible work area. Treat that as the same
            // whole-work-area zone without hiding windows that leave a real gap.
            const int frameAllowance = 16;
            if (candidate.Left > reference.Left + frameAllowance ||
                candidate.Top > reference.Top + frameAllowance ||
                candidate.Right < reference.Right - frameAllowance ||
                candidate.Bottom < reference.Bottom - frameAllowance)
            {
                return false;
            }

            Rectangle intersection = Rectangle.Intersect(candidate, reference);
            long referenceArea = (long)reference.Width * reference.Height;
            long intersectionArea = (long)Math.Max(0, intersection.Width) *
                Math.Max(0, intersection.Height);
            return referenceArea > 0 &&
                intersectionArea * 1000L >= referenceArea * 985L;
        }

        private void InvalidatePointerChange(
            Point previousPointer,
            Rectangle previousTarget)
        {
            Rectangle currentTarget = CurrentTargetBounds();
            if (previousPointer == pointerScreen && previousTarget == currentTarget)
            {
                return;
            }

            pointerVisualDirty = true;
            InvalidateTargetChange(previousTarget, currentTarget);
            RetargetInformationPanel(false);
        }

        private void InvalidateTargetChange(Rectangle previous, Rectangle current)
        {
            if (previous == current)
            {
                return;
            }
            if (previous.Width > 0 && previous.Height > 0)
            {
                Rectangle dirty = DesktopToClient(previous);
                dirty.Inflate(4, 4);
                Invalidate(dirty);
            }
            if (current.Width > 0 && current.Height > 0)
            {
                Rectangle dirty = DesktopToClient(current);
                dirty.Inflate(4, 4);
                Invalidate(dirty);
            }
        }

        private Rectangle GetCrosshairDirtyRectangle(Point desktopPointer)
        {
            Point local = DesktopToClient(desktopPointer);
            return new Rectangle(local.X - 23, local.Y - 23, 47, 47);
        }

        private Rectangle GetPanelDirtyRectangle(Point desktopPointer)
        {
            return GetPanelDirtyRectangle(desktopPointer, CurrentTargetBounds());
        }

        private Rectangle GetPanelDirtyRectangle(
            Point desktopPointer,
            Rectangle desktopTarget)
        {
            Rectangle dirty = GetPanelRectangle(desktopPointer, desktopTarget);
            if (panelPositionInitialized)
            {
                dirty = Rectangle.Union(dirty, GetPanelRectangle());
            }
            dirty.Inflate(6, 6);
            return dirty;
        }

        private static bool SameZone(Zone first, Zone second)
        {
            if (ReferenceEquals(first, second))
            {
                return true;
            }
            if (first == null || second == null)
            {
                return false;
            }
            return first.Kind == second.Kind && first.Bounds == second.Bounds;
        }

        private void DrawCrosshair(Graphics graphics, Point point)
        {
            const int length = 19;
            const int gap = 4;
            using (Pen shadow = new Pen(Color.FromArgb(235, 0, 0, 0), 3.0f))
            using (Pen light = new Pen(Color.White, 1.0f))
            {
                DrawCrosshairLines(graphics, shadow, point, length, gap);
                DrawCrosshairLines(graphics, light, point, length, gap);
            }
        }

        private static void DrawCrosshairLines(Graphics graphics, Pen pen, Point point, int length, int gap)
        {
            graphics.DrawLine(pen, point.X - length, point.Y, point.X - gap, point.Y);
            graphics.DrawLine(pen, point.X + gap, point.Y, point.X + length, point.Y);
            graphics.DrawLine(pen, point.X, point.Y - length, point.X, point.Y - gap);
            graphics.DrawLine(pen, point.X, point.Y + gap, point.X, point.Y + length);
        }

        private void DrawInformationPanel(Graphics graphics, Rectangle target)
        {
            Rectangle panel = GetPanelRectangle();
            DrawInformationPanelContent(graphics, target, panel);
        }

        private void DrawInformationPanelContent(Graphics graphics, Rectangle target, Rectangle panel)
        {
            Color modeAccent = recordingMode
                ? Color.FromArgb(238, 42, 52)
                : OpenCaptureTheme.AccentDeep;
            FloatingPanelForm.DrawChrome(graphics, panel, modeAccent);

            if (recordingMode)
            {
                using (SolidBrush recordingDot = new SolidBrush(Color.FromArgb(238, 42, 52)))
                using (Pen recordingRing = new Pen(Color.FromArgb(225, Color.White), 1.0f))
                {
                    Rectangle dot = new Rectangle(panel.Right - 23, panel.Top + 11, 11, 11);
                    graphics.FillEllipse(recordingDot, dot);
                    graphics.DrawEllipse(recordingRing, dot);
                }
            }

            Rectangle magnifier = new Rectangle(
                panel.Left + PanelPadding,
                panel.Top + PanelPadding,
                MagnifierCells * MagnifierCellSize,
                MagnifierCells * MagnifierCellSize);
            DrawMagnifier(graphics, magnifier, target);

            int infoLeft = magnifier.Right + 15;
            int infoWidth = panel.Right - infoLeft - PanelPadding;
            Color sampled = frame.GetPixelAtDesktopPoint(pointerScreen);
            string hex = string.Format("#{0:X2}{1:X2}{2:X2}", sampled.R, sampled.G, sampled.B);

            TextRenderingHint previousTextRendering = graphics.TextRenderingHint;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (SolidBrush hexBrush = new SolidBrush(Color.FromArgb(20, 27, 34)))
            using (StringFormat hexFormat = new StringFormat(StringFormat.GenericTypographic))
            {
                hexFormat.Alignment = StringAlignment.Near;
                hexFormat.LineAlignment = StringAlignment.Center;
                hexFormat.FormatFlags = StringFormatFlags.NoWrap;
                graphics.DrawString(
                    hex,
                    monoFont,
                    hexBrush,
                    new RectangleF(infoLeft, panel.Top + PanelPadding, infoWidth, 24),
                    hexFormat);
            }

            Rectangle swatch = new Rectangle(infoLeft, panel.Top + 42, 36, 26);
            using (SolidBrush colorBrush = new SolidBrush(sampled))
            using (Pen swatchBorder = new Pen(Color.FromArgb(220, 20, 27, 34), 1.0f))
            {
                graphics.FillRectangle(colorBrush, swatch);
                graphics.DrawRectangle(swatchBorder, swatch);
            }

            int width = Math.Max(1, target.Width);
            int height = Math.Max(1, target.Height);
            string estimatedStorage = recordingMode
                ? FormatEstimatedStorage(
                    EstimateRecordingStorageBytesPerMinute(width, height)) + "/min"
                : FormatEstimatedStorage(
                    EstimateScreenshotStorageBytes(width, height)) + " PNG";
            string[] detailLines = recordingMode
                ? new string[]
                {
                    string.Format("{0} × {1}", width, height),
                    "≈ " + estimatedStorage,
                    FormatCurrentStorage(0) + " used",
                    "Sensitivity " + keyboardSensitivityPercent.ToString(CultureInfo.InvariantCulture) + "%",
                    "X  " + pointerScreen.X.ToString(CultureInfo.InvariantCulture),
                    "Y  " + pointerScreen.Y.ToString(CultureInfo.InvariantCulture)
                }
                : new string[]
                {
                    string.Format("{0} × {1}", width, height),
                    "≈ " + estimatedStorage,
                    "Sensitivity " + keyboardSensitivityPercent.ToString(CultureInfo.InvariantCulture) + "%",
                    "X  " + pointerScreen.X.ToString(CultureInfo.InvariantCulture),
                    "Y  " + pointerScreen.Y.ToString(CultureInfo.InvariantCulture)
                };
            using (SolidBrush detailBrush = new SolidBrush(Color.FromArgb(25, 34, 43)))
            using (StringFormat detailFormat = new StringFormat(StringFormat.GenericTypographic))
            {
                detailFormat.Alignment = StringAlignment.Near;
                detailFormat.LineAlignment = StringAlignment.Near;
                int lineIndex;
                for (lineIndex = 0; lineIndex < detailLines.Length; lineIndex++)
                {
                    graphics.DrawString(
                        detailLines[lineIndex],
                        detailFont,
                        detailBrush,
                        new RectangleF(
                            infoLeft,
                            panel.Top + DetailTop + lineIndex * DetailLineHeight,
                            infoWidth,
                            DetailLineHeight),
                        detailFormat);
                }
            }
            graphics.TextRenderingHint = previousTextRendering;

        }

        internal static long EstimateScreenshotStorageBytes(int width, int height)
        {
            long pixels = Math.Max(1L, (long)Math.Max(1, width) * Math.Max(1, height));
            return Math.Max(1024L, (long)Math.Ceiling(
                1024.0 + pixels * EstimatedPngBytesPerPixel));
        }

        internal static long EstimateRecordingStorageBytesPerMinute(int width, int height)
        {
            long pixels = Math.Max(1L, (long)Math.Max(1, width) * Math.Max(1, height));
            double estimatedFrameBytes = Math.Max(640.0, pixels * EstimatedMjpegBytesPerPixel);
            return (long)Math.Ceiling(
                estimatedFrameBytes * ScreenRecorder.DefaultFramesPerSecond * 60.0 +
                64.0 * 1024.0);
        }

        internal static string FormatEstimatedStorage(long bytes)
        {
            double value = Math.Max(0L, bytes);
            if (value < 1024.0)
            {
                return "<1 KB";
            }
            if (value < 1024.0 * 1024.0)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:0.#} KB",
                    value / 1024.0);
            }
            if (value < 1024.0 * 1024.0 * 1024.0)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:0.#} MB",
                    value / (1024.0 * 1024.0));
            }
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.##} GB",
                value / (1024.0 * 1024.0 * 1024.0));
        }

        internal static string FormatCurrentStorage(long bytes)
        {
            return bytes <= 0L ? "0 KB" : FormatEstimatedStorage(bytes);
        }

        private void DrawMagnifier(Graphics graphics, Rectangle bounds, Rectangle target)
        {
            int radius = MagnifierCells / 2;
            int row;
            int column;
            for (row = 0; row < MagnifierCells; row++)
            {
                for (column = 0; column < MagnifierCells; column++)
                {
                    Point samplePoint = new Point(
                        pointerScreen.X + column - radius,
                        pointerScreen.Y + row - radius);
                    Color color = frame.DesktopBounds.Contains(samplePoint)
                        ? frame.GetPixelAtDesktopPoint(samplePoint)
                        : Color.FromArgb(48, 52, 60);
                    using (SolidBrush brush = new SolidBrush(color))
                    {
                        graphics.FillRectangle(
                            brush,
                            bounds.Left + column * MagnifierCellSize,
                            bounds.Top + row * MagnifierCellSize,
                            MagnifierCellSize,
                            MagnifierCellSize);
                    }
                }
            }

            using (Pen grid = new Pen(Color.FromArgb(50, Color.Black), 1.0f))
            {
                int index;
                for (index = 1; index < MagnifierCells; index++)
                {
                    int x = bounds.Left + index * MagnifierCellSize;
                    int y = bounds.Top + index * MagnifierCellSize;
                    graphics.DrawLine(grid, x, bounds.Top, x, bounds.Bottom);
                    graphics.DrawLine(grid, bounds.Left, y, bounds.Right, y);
                }
            }

            // A physical display cannot draw a literal sub-pixel-width stroke.
            // A one-pixel, low-opacity hairline gives the intended lighter visual
            // weight while retaining exact alignment to the sampled pixel centre.
            float centreX = bounds.Left + (radius + 0.5f) * MagnifierCellSize;
            float centreY = bounds.Top + (radius + 0.5f) * MagnifierCellSize;
            Color edgeColour = recordingMode
                ? Color.FromArgb(238, 42, 52)
                : OpenCaptureTheme.AccentDeep;
            using (Pen crosshair = new Pen(Color.FromArgb(185, 0, 0, 0), 1.0f))
            using (Pen zoneEdge = new Pen(Color.FromArgb(155, edgeColour), 1.0f))
            using (Pen outline = new Pen(Color.FromArgb(205, 15, 20, 26), 1.0f))
            {
                graphics.DrawLine(crosshair, centreX, bounds.Top, centreX, bounds.Bottom);
                graphics.DrawLine(crosshair, bounds.Left, centreY, bounds.Right, centreY);
                DrawMagnifierZoneEdges(graphics, zoneEdge, bounds, target, radius);
                Rectangle outer = bounds;
                outer.Width--;
                outer.Height--;
                graphics.DrawRectangle(outline, outer);
            }
        }

        private void DrawMagnifierZoneEdges(
            Graphics graphics,
            Pen pen,
            Rectangle bounds,
            Rectangle target,
            int radius)
        {
            if (target.Width <= 0 || target.Height <= 0)
            {
                return;
            }

            int sourceLeft = pointerScreen.X - radius;
            int sourceTop = pointerScreen.Y - radius;
            float left = bounds.Left + (target.Left - sourceLeft) * MagnifierCellSize;
            float right = bounds.Left + (target.Right - sourceLeft) * MagnifierCellSize;
            float top = bounds.Top + (target.Top - sourceTop) * MagnifierCellSize;
            float bottom = bounds.Top + (target.Bottom - sourceTop) * MagnifierCellSize;

            if (left >= bounds.Left && left <= bounds.Right)
            {
                graphics.DrawLine(pen, left, bounds.Top, left, bounds.Bottom);
            }
            if (right >= bounds.Left && right <= bounds.Right)
            {
                graphics.DrawLine(pen, right, bounds.Top, right, bounds.Bottom);
            }
            if (top >= bounds.Top && top <= bounds.Bottom)
            {
                graphics.DrawLine(pen, bounds.Left, top, bounds.Right, top);
            }
            if (bottom >= bounds.Top && bottom <= bounds.Bottom)
            {
                graphics.DrawLine(pen, bounds.Left, bottom, bounds.Right, bottom);
            }
        }

        private Rectangle GetPanelRectangle()
        {
            if (!panelPositionInitialized)
            {
                return GetPanelRectangle(pointerScreen, CurrentTargetBounds());
            }
            return new Rectangle(Point.Round(panelPosition), panelMotionTarget.Size);
        }

        private void RetargetInformationPanel(bool immediate)
        {
            if (!immediate && panelPositionInitialized && floatingPreview != null)
            {
                panelTargetDirty = true;
                if (!panelMotionTimer.Enabled) panelMotionTimestamp = Stopwatch.GetTimestamp();
                panelMotionTimer.Start();
                return;
            }
            Rectangle old = GetPanelRectangle();
            Rectangle next = GetPanelRectangle(pointerScreen, CurrentTargetBounds());
            panelMotionTarget = next;
            if (!panelPositionInitialized || immediate || !panelAnimationEnabled)
            {
                panelPosition = next.Location;
                panelPositionInitialized = true;
                panelMotionTimer.Stop();
            }
            else
            {
                if (!panelMotionTimer.Enabled)
                {
                    panelMotionTimestamp = Stopwatch.GetTimestamp();
                    panelMotionTimer.Start();
                }
            }
            if (floatingPreview == null)
            {
                old.Inflate(6, 6);
                Invalidate(old);
                Invalidate(GetPanelDirtyRectangle(pointerScreen));
            }
            else
            {
                UpdateFloatingPreview();
                if (pointerVisualDirty)
                {
                    Invalidate(GetCrosshairDirtyRectangle(lastPaintedPointer));
                    Invalidate(GetCrosshairDirtyRectangle(pointerScreen));
                    lastPaintedPointer = pointerScreen;
                    pointerVisualDirty = false;
                }
            }
        }

        private void AnimateInformationPanel(object sender, EventArgs eventArgs)
        {
            if (disposingOverlay || state == InteractionState.Finished ||
                state == InteractionState.Flashing)
            {
                panelMotionTimer.Stop();
                if (floatingPreview != null) floatingPreview.Hide();
                return;
            }
            if (panelTargetDirty)
            {
                panelMotionTarget = GetPanelRectangle(pointerScreen, CurrentTargetBounds());
                panelTargetDirty = false;
            }
            Rectangle before = GetPanelRectangle();
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - panelMotionTimestamp) / (double)Stopwatch.Frequency;
            panelMotionTimestamp = now;
            panelPosition = panelAnimationEnabled
                ? PreviewMotion.Advance(panelPosition, panelMotionTarget.Location, seconds)
                : (PointF)panelMotionTarget.Location;
            Rectangle after = GetPanelRectangle();
            Rectangle safe = DesktopToClient(GetPointerMonitorBounds(pointerScreen));
            safe.Inflate(-8, -8);
            Rectangle target = DesktopToClient(CurrentTargetBounds());
            // When the placement changes sides, do not sweep across the selection
            // or a monitor gap just to finish an otherwise cosmetic transition.
            if (!safe.Contains(after) ||
                IntersectionArea(after, target) > IntersectionArea(panelMotionTarget, target))
            {
                panelPosition = panelMotionTarget.Location;
                after = GetPanelRectangle();
            }
            if (floatingPreview != null)
            {
                UpdateFloatingPreview();
                if (pointerVisualDirty)
                {
                    Invalidate(GetCrosshairDirtyRectangle(lastPaintedPointer));
                    Invalidate(GetCrosshairDirtyRectangle(pointerScreen));
                    lastPaintedPointer = pointerScreen;
                    pointerVisualDirty = false;
                }
                // Do not let continuous high-report-rate mouse messages starve paint.
                Update();
            }
            else
            {
                before.Inflate(6, 6);
                after.Inflate(6, 6);
                Invalidate(before);
                Invalidate(after);
            }
            if (panelPosition == (PointF)panelMotionTarget.Location)
            {
                panelMotionTimer.Stop();
            }
        }

        private void UpdateFloatingPreview()
        {
            if (floatingPreview == null || floatingPreview.IsDisposed) return;
            Rectangle panel = GetPanelRectangle();
            Rectangle target = CurrentTargetBounds();
            Point desktopLocation = ClientToDesktop(panel.Location);
            floatingPreview.Location = desktopLocation;
            if (panelSurface == null || panelSurfacePointer != pointerScreen || panelSurfaceTarget != target ||
                panelSurfaceSensitivity != keyboardSensitivityPercent || panelSurfaceRecording != recordingMode)
            {
                Size size = new Size(panel.Width + 7, panel.Height + 8);
                if (panelSurface == null || panelSurface.Size != size)
                {
                    if (panelSurface != null) panelSurface.Dispose();
                    panelSurface = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                }
                using (Graphics graphics = Graphics.FromImage(panelSurface))
                {
                    graphics.Clear(Color.Transparent);
                    DrawInformationPanelContent(graphics, target, new Rectangle(Point.Empty, panel.Size));
                }
                panelSurfacePointer = pointerScreen;
                panelSurfaceTarget = target;
                panelSurfaceSensitivity = keyboardSensitivityPercent;
                panelSurfaceRecording = recordingMode;
                floatingPreview.Present(panelSurface);
            }
        }

        private static long IntersectionArea(Rectangle first, Rectangle second)
        {
            Rectangle overlap = Rectangle.Intersect(first, second);
            return (long)Math.Max(0, overlap.Width) * Math.Max(0, overlap.Height);
        }

        private Rectangle GetPanelRectangle(Point desktopPointer)
        {
            return GetPanelRectangle(desktopPointer, CurrentTargetBounds());
        }

        private Rectangle GetPanelRectangle(
            Point desktopPointer,
            Rectangle desktopTarget)
        {
            int panelHeight = recordingMode
                ? RecordingPanelHeight
                : ScreenshotPanelHeight;
            Rectangle monitor = GetPointerMonitorBounds(desktopPointer);
            Point localPointer = DesktopToClient(desktopPointer);
            Rectangle localMonitor = DesktopToClient(monitor);
            Rectangle safe = localMonitor;
            safe.Inflate(-8, -8);
            Rectangle localTarget = desktopTarget.Width > 0 &&
                desktopTarget.Height > 0
                    ? DesktopToClient(desktopTarget)
                    : Rectangle.Empty;
            return ChoosePanelRectangle(
                safe,
                localPointer,
                localTarget,
                new Size(PanelWidth, panelHeight),
                PanelGap,
                PanelSelectionGap,
                state == InteractionState.Inspecting && hoverZone != null);
        }

        internal static Rectangle ChoosePanelRectangle(
            Rectangle safe,
            Point pointer,
            Rectangle target,
            Size panelSize,
            int pointerGap,
            int targetGap)
        {
            return ChoosePanelRectangle(
                safe,
                pointer,
                target,
                panelSize,
                pointerGap,
                targetGap,
                false);
        }

        internal static Rectangle ChoosePanelRectangle(
            Rectangle safe,
            Point pointer,
            Rectangle target,
            Size panelSize,
            int pointerGap,
            int targetGap,
            bool allowNormalPositionInsideZone)
        {
            Rectangle exclusion = target;
            if (!exclusion.IsEmpty)
            {
                exclusion.Inflate(targetGap, targetGap);
            }

            List<Rectangle> cursorCandidates = new List<Rectangle>
            {
                new Rectangle(
                    pointer.X + pointerGap,
                    pointer.Y + pointerGap,
                    panelSize.Width,
                    panelSize.Height),
                new Rectangle(
                    pointer.X - pointerGap - panelSize.Width,
                    pointer.Y + pointerGap,
                    panelSize.Width,
                    panelSize.Height),
                new Rectangle(
                    pointer.X + pointerGap,
                    pointer.Y - pointerGap - panelSize.Height,
                    panelSize.Width,
                    panelSize.Height),
                new Rectangle(
                    pointer.X - pointerGap - panelSize.Width,
                    pointer.Y - pointerGap - panelSize.Height,
                    panelSize.Width,
                    panelSize.Height)
            };
            foreach (Rectangle candidate in cursorCandidates)
            {
                if (safe.Contains(candidate) &&
                    (exclusion.IsEmpty ||
                        !candidate.IntersectsWith(exclusion) ||
                        (allowNormalPositionInsideZone &&
                            CanUseNormalPanelPositionInsideZone(
                                pointer,
                                target,
                                candidate))))
                {
                    return candidate;
                }
            }

            List<Rectangle> edgeCandidates = new List<Rectangle>();
            if (!target.IsEmpty)
            {
                int horizontal = ClampCoordinate(
                    pointer.X - panelSize.Width / 2,
                    safe.Left,
                    safe.Right - panelSize.Width);
                int vertical = ClampCoordinate(
                    pointer.Y - panelSize.Height / 2,
                    safe.Top,
                    safe.Bottom - panelSize.Height);
                edgeCandidates.Add(new Rectangle(
                    horizontal,
                    target.Top - targetGap - panelSize.Height,
                    panelSize.Width,
                    panelSize.Height));
                edgeCandidates.Add(new Rectangle(
                    horizontal,
                    target.Bottom + targetGap,
                    panelSize.Width,
                    panelSize.Height));
                edgeCandidates.Add(new Rectangle(
                    target.Left - targetGap - panelSize.Width,
                    vertical,
                    panelSize.Width,
                    panelSize.Height));
                edgeCandidates.Add(new Rectangle(
                    target.Right + targetGap,
                    vertical,
                    panelSize.Width,
                    panelSize.Height));
            }

            Rectangle bestOutside = Rectangle.Empty;
            long bestDistance = long.MaxValue;
            foreach (Rectangle candidate in edgeCandidates)
            {
                if (!safe.Contains(candidate) ||
                    (!exclusion.IsEmpty && candidate.IntersectsWith(exclusion)))
                {
                    continue;
                }
                long distance = DistanceSquaredToRectangle(pointer, candidate);
                if (distance < bestDistance)
                {
                    bestOutside = candidate;
                    bestDistance = distance;
                }
            }
            if (!bestOutside.IsEmpty)
            {
                return bestOutside;
            }

            Rectangle bestFallback = Rectangle.Empty;
            long bestOverlap = long.MaxValue;
            bestDistance = long.MaxValue;
            List<Rectangle> fallbacks = new List<Rectangle>();
            fallbacks.AddRange(cursorCandidates);
            fallbacks.AddRange(edgeCandidates);
            foreach (Rectangle candidate in fallbacks)
            {
                Rectangle clamped = ClampRectangle(candidate, safe, panelSize);
                Rectangle overlap = exclusion.IsEmpty
                    ? Rectangle.Empty
                    : Rectangle.Intersect(clamped, exclusion);
                long overlapArea = (long)Math.Max(0, overlap.Width) *
                    Math.Max(0, overlap.Height);
                long distance = DistanceSquaredToRectangle(pointer, clamped);
                if (overlapArea < bestOverlap ||
                    (overlapArea == bestOverlap && distance < bestDistance))
                {
                    bestFallback = clamped;
                    bestOverlap = overlapArea;
                    bestDistance = distance;
                }
            }
            return bestFallback.IsEmpty
                ? ClampRectangle(
                    new Rectangle(pointer, panelSize),
                    safe,
                    panelSize)
                : bestFallback;
        }

        private static bool CanUseNormalPanelPositionInsideZone(
            Point pointer,
            Rectangle target,
            Rectangle panel)
        {
            if (target.IsEmpty || !target.Contains(pointer))
            {
                return false;
            }

            // Avoid switching modes while the cursor merely grazes a zone. The
            // normal placement is used only after the pointer is comfortably in
            // its interior, which also prevents edge-to-edge panel flicker.
            const int minimumInteriorDepth = 48;
            int depth = Math.Min(
                Math.Min(pointer.X - target.Left, target.Right - 1 - pointer.X),
                Math.Min(pointer.Y - target.Top, target.Bottom - 1 - pointer.Y));
            if (depth < minimumInteriorDepth)
            {
                return false;
            }

            Rectangle overlap = Rectangle.Intersect(panel, target);
            long overlapArea = (long)Math.Max(0, overlap.Width) *
                Math.Max(0, overlap.Height);
            long targetArea = (long)target.Width * target.Height;

            // A full panel is harmless inside a large zone, but should not cover
            // a substantial portion of a compact card or control.
            const int maximumZoneCoveragePercent = 12;
            return targetArea > 0 &&
                overlapArea * 100L <= targetArea * maximumZoneCoveragePercent;
        }

        private static Rectangle ClampRectangle(
            Rectangle rectangle,
            Rectangle safe,
            Size size)
        {
            return new Rectangle(
                ClampCoordinate(rectangle.Left, safe.Left, safe.Right - size.Width),
                ClampCoordinate(rectangle.Top, safe.Top, safe.Bottom - size.Height),
                size.Width,
                size.Height);
        }

        private static int ClampCoordinate(int value, int minimum, int maximum)
        {
            if (maximum < minimum)
            {
                return minimum;
            }
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static long DistanceSquaredToRectangle(
            Point point,
            Rectangle rectangle)
        {
            long horizontal = point.X < rectangle.Left
                ? rectangle.Left - point.X
                : point.X > rectangle.Right
                    ? point.X - rectangle.Right
                    : 0;
            long vertical = point.Y < rectangle.Top
                ? rectangle.Top - point.Y
                : point.Y > rectangle.Bottom
                    ? point.Y - rectangle.Bottom
                    : 0;
            return horizontal * horizontal + vertical * vertical;
        }

        private Rectangle GetPointerMonitorBounds()
        {
            return GetPointerMonitorBounds(pointerScreen);
        }

        private Rectangle GetPointerMonitorBounds(Point desktopPointer)
        {
            IntPtr monitor = NativeMethods.MonitorFromPoint(
                new NativeMethods.POINT(desktopPointer),
                NativeMethods.MONITOR_DEFAULTTONEAREST);
            NativeMethods.MONITORINFOEX info;
            if (NativeMethods.TryGetMonitorInfo(monitor, out info))
            {
                return info.rcMonitor.ToRectangle();
            }
            return frame.DesktopBounds;
        }

        private Rectangle GetPointerMonitorWorkAreaBounds(Point desktopPointer)
        {
            IntPtr monitor = NativeMethods.MonitorFromPoint(
                new NativeMethods.POINT(desktopPointer),
                NativeMethods.MONITOR_DEFAULTTONEAREST);
            NativeMethods.MONITORINFOEX info;
            if (NativeMethods.TryGetMonitorInfo(monitor, out info))
            {
                return info.rcWork.ToRectangle();
            }
            return GetPointerMonitorBounds(desktopPointer);
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle rectangle, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private Point ClampToDesktop(Point point)
        {
            return new Point(
                Math.Max(frame.DesktopBounds.Left, Math.Min(frame.DesktopBounds.Right - 1, point.X)),
                Math.Max(frame.DesktopBounds.Top, Math.Min(frame.DesktopBounds.Bottom - 1, point.Y)));
        }

        private Point ClientToDesktop(Point point)
        {
            return new Point(point.X + frame.DesktopBounds.Left, point.Y + frame.DesktopBounds.Top);
        }

        private Point DesktopToClient(Point point)
        {
            return new Point(point.X - frame.DesktopBounds.Left, point.Y - frame.DesktopBounds.Top);
        }

        private Rectangle DesktopToClient(Rectangle rectangle)
        {
            return new Rectangle(
                rectangle.Left - frame.DesktopBounds.Left,
                rectangle.Top - frame.DesktopBounds.Top,
                rectangle.Width,
                rectangle.Height);
        }

        private static bool HasLeftDragThreshold(Point anchor, Point current)
        {
            Size drag = SystemInformation.DragSize;
            Rectangle clickArea = new Rectangle(
                anchor.X - drag.Width / 2,
                anchor.Y - drag.Height / 2,
                Math.Max(1, drag.Width),
                Math.Max(1, drag.Height));
            return !clickArea.Contains(current);
        }

        private long ElapsedPressTicks(long eventTimestamp)
        {
            return pressStartedTimestamp > 0 && eventTimestamp > pressStartedTimestamp
                ? eventTimestamp - pressStartedTimestamp
                : 0;
        }

        private static bool ShouldTransitionToDrag(
            bool startedOverDetectedZone,
            bool crossedMovementThreshold,
            long elapsedTicks,
            long timestampFrequency)
        {
            if (!crossedMovementThreshold)
            {
                return false;
            }

            // Empty-space drags retain the conventional immediate threshold.
            if (!startedOverDetectedZone)
            {
                return true;
            }

            if (elapsedTicks <= 0 || timestampFrequency <= 0)
            {
                return false;
            }

            // Strictly greater is intentional: a release at exactly 300 ms is
            // still a zone click. Guard the multiplication even though a normal
            // mouse press is many orders of magnitude below Int64's limit.
            if (elapsedTicks > long.MaxValue / 1000L)
            {
                return true;
            }
            if (timestampFrequency > long.MaxValue / ZoneDragHoldMilliseconds)
            {
                return ((double)elapsedTicks * 1000.0) >
                    ((double)timestampFrequency * ZoneDragHoldMilliseconds);
            }
            return elapsedTicks * 1000L >
                timestampFrequency * ZoneDragHoldMilliseconds;
        }

        private static Rectangle MakeInclusiveSelection(Point first, Point second)
        {
            int left = Math.Min(first.X, second.X);
            int top = Math.Min(first.Y, second.Y);
            int right = Math.Max(first.X, second.X) + 1;
            int bottom = Math.Max(first.Y, second.Y) + 1;
            return Rectangle.FromLTRB(left, top, right, bottom);
        }
    }
}
