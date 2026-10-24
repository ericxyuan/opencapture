using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace OpenCapture
{
    /// <summary>
    /// Owns a frozen virtual-desktop bitmap and its physical desktop origin.
    /// Dispose the frame when the selection overlay has finished with it. The
    /// Bitmap property is borrowed; ownership remains with this frame.
    /// </summary>
    internal sealed class CapturedFrame : IDisposable
    {
        private Bitmap bitmap;

        internal CapturedFrame(
            Bitmap bitmap,
            Rectangle desktopBounds,
            long performanceCounterTimestamp,
            DateTime capturedAtUtc)
            : this(bitmap, desktopBounds, performanceCounterTimestamp, capturedAtUtc, 0)
        {
        }

        internal CapturedFrame(
            Bitmap bitmap,
            Rectangle desktopBounds,
            long performanceCounterTimestamp,
            DateTime capturedAtUtc,
            int hdrOutputsToneMapped)
        {
            if (bitmap == null)
            {
                throw new ArgumentNullException("bitmap");
            }

            this.bitmap = bitmap;
            DesktopBounds = desktopBounds;
            PerformanceCounterTimestamp = performanceCounterTimestamp;
            CapturedAtUtc = capturedAtUtc;
            HdrOutputsToneMapped = hdrOutputsToneMapped;
        }

        internal Bitmap Bitmap
        {
            get
            {
                ThrowIfDisposed();
                return bitmap;
            }
        }

        internal Rectangle DesktopBounds { get; private set; }

        /// <summary>
        /// QPC value sampled immediately before the screen DC is copied.
        /// </summary>
        internal long PerformanceCounterTimestamp { get; private set; }

        internal DateTime CapturedAtUtc { get; private set; }

        internal int HdrOutputsToneMapped { get; private set; }

        internal int Width
        {
            get { return DesktopBounds.Width; }
        }

        internal int Height
        {
            get { return DesktopBounds.Height; }
        }

        internal Point DesktopToBitmap(Point desktopPoint)
        {
            return new Point(
                desktopPoint.X - DesktopBounds.Left,
                desktopPoint.Y - DesktopBounds.Top);
        }

        internal Point BitmapToDesktop(Point bitmapPoint)
        {
            return new Point(
                bitmapPoint.X + DesktopBounds.Left,
                bitmapPoint.Y + DesktopBounds.Top);
        }

        /// <summary>
        /// Creates an independent bitmap for saving or placing on the clipboard.
        /// The caller owns and must dispose the returned bitmap.
        /// </summary>
        internal Bitmap CreateBitmapCopy()
        {
            ThrowIfDisposed();
            return bitmap.Clone(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                PixelFormat.Format32bppRgb);
        }

        /// <summary>
        /// Crops a desktop-space selection. The caller owns the returned bitmap.
        /// </summary>
        internal Bitmap Crop(Rectangle desktopSelection)
        {
            ThrowIfDisposed();

            Rectangle clipped = Rectangle.Intersect(DesktopBounds, desktopSelection);
            if (clipped.Width <= 0 || clipped.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "desktopSelection",
                    "The selection must intersect the captured desktop.");
            }

            Rectangle source = new Rectangle(
                clipped.Left - DesktopBounds.Left,
                clipped.Top - DesktopBounds.Top,
                clipped.Width,
                clipped.Height);
            return bitmap.Clone(source, PixelFormat.Format32bppRgb);
        }

        internal Color GetPixelAtDesktopPoint(Point desktopPoint)
        {
            ThrowIfDisposed();
            if (!DesktopBounds.Contains(desktopPoint))
            {
                throw new ArgumentOutOfRangeException("desktopPoint");
            }

            Point local = DesktopToBitmap(desktopPoint);
            return bitmap.GetPixel(local.X, local.Y);
        }

        public void Dispose()
        {
            Bitmap ownedBitmap = bitmap;
            bitmap = null;
            if (ownedBitmap != null)
            {
                ownedBitmap.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (bitmap == null)
            {
                throw new ObjectDisposedException(typeof(CapturedFrame).FullName);
            }
        }
    }

    /// <summary>
    /// Immediate GDI freeze of the composed Windows desktop, followed by native
    /// FP16 Desktop Duplication replacement for active HDR outputs.
    ///
    /// BitBlt never paints the cursor separately, so a hardware pointer is absent
    /// from the frozen frame. CAPTUREBLT includes DWM layered windows. This backend
    /// supplies an exact fallback for ordinary SDR application pixels. HDR scRGB
    /// values are converted to the final 32-bit SDR bitmap per display.
    /// </summary>
    internal static class CaptureEngine
    {
        internal const int DefaultClipboardRetryCount = 8;
        internal const int DefaultClipboardRetryDelayMilliseconds = 20;
        internal const string ClipboardScreenshotFormat = "OpenCapture.Screenshot.v1";
        internal const string ClipboardRecordingFormat = "OpenCapture.Recording.v1";
        private static readonly uint ClipboardScreenshotFormatId =
            NativeMethods.RegisterClipboardFormat(ClipboardScreenshotFormat);
        private static readonly uint ClipboardRecordingFormatId =
            NativeMethods.RegisterClipboardFormat(ClipboardRecordingFormat);

        /// <summary>
        /// Captures every monitor immediately. DwmFlush is deliberately disabled by
        /// default so the hotkey path does not wait for the next compositor boundary.
        /// </summary>
        internal static CapturedFrame CaptureVirtualDesktop()
        {
            return CaptureVirtualDesktop(false);
        }

        /// <summary>
        /// Captures every monitor. Set synchronizeComposition only when a caller
        /// prefers a completed DWM present over the lowest possible trigger latency.
        /// </summary>
        internal static CapturedFrame CaptureVirtualDesktop(bool synchronizeComposition)
        {
            Rectangle desktopBounds = NativeMethods.GetVirtualScreenBounds();
            return CaptureDesktopRectangle(desktopBounds, synchronizeComposition);
        }

        /// <summary>
        /// Compatibility alias for callers that name the operation by screen.
        /// </summary>
        internal static CapturedFrame CaptureVirtualScreen()
        {
            return CaptureVirtualDesktop(false);
        }

        internal static CapturedFrame CaptureMonitor(IntPtr monitor)
        {
            NativeMethods.MONITORINFOEX monitorInfo;
            if (!NativeMethods.TryGetMonitorInfo(monitor, out monitorInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read monitor bounds.");
            }

            return CaptureDesktopRectangle(monitorInfo.rcMonitor.ToRectangle(), false);
        }

        /// <summary>
        /// Captures a physical desktop rectangle. The returned frame owns its Bitmap.
        /// No cursor icon is rendered by this method.
        /// </summary>
        internal static CapturedFrame CaptureDesktopRectangle(
            Rectangle desktopBounds,
            bool synchronizeComposition)
        {
            if (desktopBounds.Width <= 0 || desktopBounds.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "desktopBounds",
                    "Capture bounds must have a positive width and height.");
            }

            if (synchronizeComposition)
            {
                NativeMethods.TryDwmFlush();
            }

            long timestamp;
            if (!NativeMethods.QueryPerformanceCounter(out timestamp))
            {
                timestamp = 0;
            }

            DateTime capturedAtUtc = DateTime.UtcNow;
            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not acquire the desktop device context.");
            }

            Bitmap bitmap = null;
            try
            {
                // Format32bppRgb is an opaque BGRX DIB. It avoids relying on GDI to
                // synthesize alpha bytes while retaining one exact 32-bit pixel per sample.
                bitmap = new Bitmap(
                    desktopBounds.Width,
                    desktopBounds.Height,
                    PixelFormat.Format32bppRgb);

                using (Graphics destination = Graphics.FromImage(bitmap))
                {
                    IntPtr destinationDc = destination.GetHdc();
                    try
                    {
                        bool copied = NativeMethods.BitBlt(
                            destinationDc,
                            0,
                            0,
                            desktopBounds.Width,
                            desktopBounds.Height,
                            screenDc,
                            desktopBounds.Left,
                            desktopBounds.Top,
                            NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
                        if (!copied)
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "The desktop copy failed.");
                        }
                    }
                    finally
                    {
                        destination.ReleaseHdc(destinationDc);
                    }
                }

                // GDI already returns ordinary 8-bit application colours exactly,
                // even on an HDR display. Replace only active HDR outputs with a
                // native FP16 Desktop Duplication frame, then convert scRGB to SDR
                // using that output's Windows reference-white level. If native HDR
                // capture is unavailable, the exact GDI frame remains untouched.
                IList<HdrMonitorAdjustment> hdrAdjustments =
                    HdrDisplayCompensation.DiscoverActiveAdjustments();
                int hdrOutputsToneMapped = HdrDesktopCapture.ReplaceHdrOutputs(
                    bitmap,
                    desktopBounds,
                    hdrAdjustments);

                CapturedFrame frame = new CapturedFrame(
                    bitmap,
                    desktopBounds,
                    timestamp,
                    capturedAtUtc,
                    hdrOutputsToneMapped);
                bitmap = null;
                return frame;
            }
            finally
            {
                if (bitmap != null)
                {
                    bitmap.Dispose();
                }

                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>
        /// Places one persistent image on the clipboard. Windows Forms performs the
        /// requested retries if another process briefly owns the clipboard. The image
        /// is borrowed and is never disposed by this method. Call from an STA thread.
        /// </summary>
        internal static bool TryCopyImageToClipboard(
            Image image,
            out Exception error)
        {
            return TryCopyImageToClipboard(
                image,
                DefaultClipboardRetryCount,
                DefaultClipboardRetryDelayMilliseconds,
                out error);
        }

        internal static bool TryCopyImageToClipboard(
            Image image,
            int retryCount,
            int retryDelayMilliseconds,
            out Exception error)
        {
            if (image == null)
            {
                throw new ArgumentNullException("image");
            }

            if (retryCount < 0)
            {
                throw new ArgumentOutOfRangeException("retryCount");
            }

            if (retryDelayMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException("retryDelayMilliseconds");
            }

            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                error = new InvalidOperationException("Clipboard access must run on an STA thread.");
                return false;
            }

            try
            {
                DataObject data = CreateScreenshotClipboardData(image);
                Clipboard.SetDataObject(data, true, retryCount, retryDelayMilliseconds);
                error = null;
                return true;
            }
            catch (ExternalException exception)
            {
                error = exception;
                return false;
            }
        }

        internal static DataObject CreateScreenshotClipboardData(Image image)
        {
            if (image == null)
            {
                throw new ArgumentNullException("image");
            }

            DataObject data = new DataObject();
            data.SetData(DataFormats.Bitmap, true, image);
            // A private clipboard format lets the tray menu distinguish the
            // screenshot OpenCapture placed there from text, colour codes, or
            // images copied by another application.
            data.SetData(ClipboardScreenshotFormat, false, new byte[] { 1 });
            return data;
        }

        internal static bool TryCopyRecordingToClipboard(
            string recordingPath,
            out Exception error)
        {
            if (string.IsNullOrWhiteSpace(recordingPath))
            {
                throw new ArgumentException("A recording path is required.", "recordingPath");
            }
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                error = new InvalidOperationException("Clipboard access must run on an STA thread.");
                return false;
            }

            try
            {
                DataObject data = CreateRecordingClipboardData(recordingPath);
                Clipboard.SetDataObject(
                    data,
                    true,
                    DefaultClipboardRetryCount,
                    DefaultClipboardRetryDelayMilliseconds);
                error = null;
                return true;
            }
            catch (ExternalException exception)
            {
                error = exception;
                return false;
            }
        }

        internal static DataObject CreateRecordingClipboardData(string recordingPath)
        {
            if (string.IsNullOrWhiteSpace(recordingPath))
            {
                throw new ArgumentException("A recording path is required.", "recordingPath");
            }

            StringCollection files = new StringCollection();
            files.Add(Path.GetFullPath(recordingPath));
            DataObject data = new DataObject();
            data.SetFileDropList(files);
            data.SetData(ClipboardRecordingFormat, false, new byte[] { 1 });
            return data;
        }

        internal static bool IsOpenCaptureScreenshotOnClipboard()
        {
            try
            {
                return ClipboardScreenshotFormatId != 0 &&
                    NativeMethods.IsClipboardFormatAvailable(ClipboardScreenshotFormatId);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsOpenCaptureContentOnClipboard()
        {
            try
            {
                return (ClipboardScreenshotFormatId != 0 &&
                        NativeMethods.IsClipboardFormatAvailable(ClipboardScreenshotFormatId)) ||
                    (ClipboardRecordingFormatId != 0 &&
                        NativeMethods.IsClipboardFormatAvailable(ClipboardRecordingFormatId));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Clears the clipboard only while the private OpenCapture screenshot
        /// marker is present. OpenClipboard holds the clipboard lock across the
        /// marker check and EmptyClipboard, so another app cannot replace the data
        /// with text between those operations.
        /// </summary>
        internal static bool TryClearOpenCaptureScreenshotFromClipboard(
            out bool cleared,
            out Exception error)
        {
            return TryClearOpenCaptureContentFromClipboard(out cleared, out error);
        }

        internal static bool TryClearOpenCaptureContentFromClipboard(
            out bool cleared,
            out Exception error)
        {
            cleared = false;
            error = null;
            if (ClipboardScreenshotFormatId == 0 && ClipboardRecordingFormatId == 0)
            {
                error = new InvalidOperationException("Windows did not register the OpenCapture clipboard marker.");
                return false;
            }

            int attempt;
            for (attempt = 0; attempt <= DefaultClipboardRetryCount; attempt++)
            {
                if (NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        bool hasScreenshot = ClipboardScreenshotFormatId != 0 &&
                            NativeMethods.IsClipboardFormatAvailable(ClipboardScreenshotFormatId);
                        bool hasRecording = ClipboardRecordingFormatId != 0 &&
                            NativeMethods.IsClipboardFormatAvailable(ClipboardRecordingFormatId);
                        if (!hasScreenshot && !hasRecording)
                        {
                            return true;
                        }
                        if (!NativeMethods.EmptyClipboard())
                        {
                            error = new Win32Exception(
                                Marshal.GetLastWin32Error(),
                                "Windows could not clear the OpenCapture item from the clipboard.");
                            return false;
                        }
                        cleared = true;
                        return true;
                    }
                    finally
                    {
                        NativeMethods.CloseClipboard();
                    }
                }

                if (attempt < DefaultClipboardRetryCount)
                {
                    Thread.Sleep(DefaultClipboardRetryDelayMilliseconds);
                }
            }

            error = new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The clipboard is busy.");
            return false;
        }
    }
}
