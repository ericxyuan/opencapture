using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenCapture
{
    internal sealed class ScreenRecorder : IDisposable
    {
        internal const int DefaultFramesPerSecond = 60;
        private const long JpegQuality = 86L;

        private readonly Rectangle selection;
        private readonly string path;
        private readonly int framesPerSecond;
        private readonly ManualResetEvent stopRequested;
        private readonly SynchronizationContext synchronizationContext;
        private readonly object lifecycleSync = new object();
        private Thread worker;
        private long currentFileSizeBytes;
        private int activeHdrOutputCount;
        private bool disposed;
        private bool workerFinished;

        internal ScreenRecorder(Rectangle recordingSelection, string outputPath)
            : this(recordingSelection, outputPath, DefaultFramesPerSecond)
        {
        }

        internal ScreenRecorder(Rectangle recordingSelection, string outputPath, int fps)
        {
            if (recordingSelection.Width <= 0 || recordingSelection.Height <= 0)
            {
                throw new ArgumentOutOfRangeException("recordingSelection");
            }
            if (string.IsNullOrEmpty(outputPath))
            {
                throw new ArgumentException("An output path is required.", "outputPath");
            }
            if (fps < 1 || fps > 120)
            {
                throw new ArgumentOutOfRangeException("fps");
            }

            selection = recordingSelection;
            path = outputPath;
            framesPerSecond = fps;
            stopRequested = new ManualResetEvent(false);
            synchronizationContext = SynchronizationContext.Current;
        }

        internal event EventHandler<RecordingCompletedEventArgs> Completed;

        internal long CurrentFileSizeBytes
        {
            get { return Interlocked.Read(ref currentFileSizeBytes); }
        }

        internal int ActiveHdrOutputCount
        {
            get { return Volatile.Read(ref activeHdrOutputCount); }
        }

        internal void Start()
        {
            lock (lifecycleSync)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(typeof(ScreenRecorder).FullName);
                }
                if (worker != null)
                {
                    throw new InvalidOperationException("Recording has already started.");
                }

                worker = new Thread(WorkerMain);
                worker.IsBackground = true;
                worker.Name = "OpenCapture screen recorder";
                try
                {
                    worker.Start();
                }
                catch
                {
                    worker = null;
                    throw;
                }
            }
        }

        internal void Stop()
        {
            lock (lifecycleSync)
            {
                if (!disposed && !workerFinished)
                {
                    stopRequested.Set();
                }
            }
        }

        public void Dispose()
        {
            Thread recordingWorker;
            lock (lifecycleSync)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                stopRequested.Set();
                recordingWorker = worker;
                if (recordingWorker == null || workerFinished)
                {
                    stopRequested.Dispose();
                    return;
                }
            }
            if (recordingWorker != Thread.CurrentThread && recordingWorker.IsAlive)
            {
                recordingWorker.Join(2000);
            }
            // A slow native call or disk flush can outlive the bounded join. The
            // worker releases its stop handle when it has actually finished.
        }

        private void WorkerMain()
        {
            try
            {
                RecordLoop();
            }
            finally
            {
                lock (lifecycleSync)
                {
                    workerFinished = true;
                    if (disposed)
                    {
                        stopRequested.Dispose();
                    }
                }
            }
        }

        private void RecordLoop()
        {
            int frames = 0;
            Exception error = null;
            bool outputOwned = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                MjpegAviWriter avi = new MjpegAviWriter(
                    path,
                    selection.Width,
                    selection.Height,
                    framesPerSecond);
                outputOwned = true;
                using (avi)
                using (Bitmap frame = new Bitmap(
                    selection.Width,
                    selection.Height,
                    PixelFormat.Format32bppRgb))
                using (Graphics graphics = Graphics.FromImage(frame))
                using (MemoryStream jpeg = new MemoryStream(Math.Max(65536, selection.Width * selection.Height / 3)))
                using (EncoderParameters encoderParameters = CreateEncoderParameters())
                using (HdrDesktopCapture.RecordingSession hdrCapture =
                    HdrDesktopCapture.CreateRecordingSession(selection))
                {
                    Volatile.Write(ref activeHdrOutputCount, hdrCapture.ActiveOutputCount);
                    ImageCodecInfo jpegCodec = FindJpegCodec();
                    if (jpegCodec == null)
                    {
                        throw new InvalidOperationException("Windows JPEG encoding is unavailable.");
                    }

                    IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
                    if (screenDc == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("Windows could not access the desktop for recording.");
                    }
                    IntPtr destinationDc = IntPtr.Zero;
                    try
                    {
                        destinationDc = graphics.GetHdc();
                        Stopwatch clock = Stopwatch.StartNew();
                        long nextFrameTicks = 0;
                        long framePeriodTicks = Math.Max(1, Stopwatch.Frequency / framesPerSecond);
                        while (!stopRequested.WaitOne(0))
                        {
                            if (!NativeMethods.BitBlt(
                                destinationDc,
                                0,
                                0,
                                selection.Width,
                                selection.Height,
                                screenDc,
                                selection.Left,
                                selection.Top,
                                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
                            {
                                throw new Win32Exception(
                                    Marshal.GetLastWin32Error(),
                                    "Windows could not copy the desktop for recording.");
                            }

                            graphics.ReleaseHdc(destinationDc);
                            destinationDc = IntPtr.Zero;
                            hdrCapture.ReplaceHdrPixels(frame);
                            jpeg.Position = 0;
                            jpeg.SetLength(0);
                            frame.Save(jpeg, jpegCodec, encoderParameters);
                            int byteCount = checked((int)jpeg.Length);
                            avi.WriteFrame(jpeg.GetBuffer(), byteCount);
                            frames = avi.FrameCount;
                            Interlocked.Exchange(
                                ref currentFileSizeBytes,
                                avi.CurrentFileSizeBytes);
                            destinationDc = graphics.GetHdc();

                            nextFrameTicks += framePeriodTicks;
                            long remainingTicks = nextFrameTicks - clock.ElapsedTicks;
                            if (remainingTicks > 0)
                            {
                                int waitMilliseconds = (int)Math.Min(
                                    1000,
                                    Math.Max(1, remainingTicks * 1000L / Stopwatch.Frequency));
                                if (stopRequested.WaitOne(waitMilliseconds))
                                {
                                    break;
                                }
                            }
                            else if (remainingTicks < -framePeriodTicks * 3)
                            {
                                nextFrameTicks = clock.ElapsedTicks;
                            }
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (destinationDc != IntPtr.Zero)
                            {
                                graphics.ReleaseHdc(destinationDc);
                            }
                        }
                        finally
                        {
                            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                error = exception;
                if (outputOwned && frames == 0)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            EventHandler<RecordingCompletedEventArgs> handler = Completed;
            if (handler != null)
            {
                RecordingCompletedEventArgs eventArgs =
                    new RecordingCompletedEventArgs(path, frames, error);
                if (synchronizationContext != null)
                {
                    synchronizationContext.Post(
                        delegate { handler(this, eventArgs); },
                        null);
                }
                else
                {
                    handler(this, eventArgs);
                }
            }
        }

        private static EncoderParameters CreateEncoderParameters()
        {
            EncoderParameters parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
            return parameters;
        }

        private static ImageCodecInfo FindJpegCodec()
        {
            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.FormatID == ImageFormat.Jpeg.Guid)
                {
                    return codec;
                }
            }
            return null;
        }
    }
}
