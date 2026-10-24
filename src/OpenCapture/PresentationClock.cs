using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace OpenCapture
{
    // A kernel high-resolution deadline is independent of WinForms/ThreadPool
    // timer quantization. The UI still receives at most one pending frame.
    internal sealed class PresentationClock : IDisposable
    {
        private readonly Control owner;
        private readonly AutoResetEvent changed = new AutoResetEvent(false);
        private readonly ManualResetEvent shutdown = new ManualResetEvent(false);
        private readonly TimerHandle deadline;
        private Thread worker;
        private volatile bool enabled;
        private volatile bool disposed;
        private int queued;
        private int handlesReleased;
        private int interval = 8;
        internal event EventHandler Tick;
        internal bool Enabled { get { return enabled; } }
        internal int Interval { get { return interval; } set { interval = Math.Max(4, value); } }

        internal PresentationClock(Control control)
        {
            owner = control;
            IntPtr timer = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
            if (timer == IntPtr.Zero) timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x1F0003);
            if (timer == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                changed.Dispose();
                shutdown.Dispose();
                throw new Win32Exception(error);
            }
            deadline = new TimerHandle(timer);
        }

        internal void Start()
        {
            if (disposed || enabled || !owner.IsHandleCreated || owner.IsDisposed) return;
            enabled = true;
            if (worker == null)
            {
                worker = new Thread(FrameLoop);
                worker.Name = "OpenCapture presentation deadlines";
                worker.IsBackground = true;
                worker.Start();
            }
            Signal(changed);
        }

        internal void Stop()
        {
            if (disposed) return;
            enabled = false;
            Signal(changed);
        }

        private void FrameLoop()
        {
            try
            {
                WaitHandle[] dormant = new WaitHandle[] { shutdown, changed };
                WaitHandle[] active = new WaitHandle[] { shutdown, changed, deadline };
                while (!disposed)
                {
                    if (!enabled)
                    {
                        CancelWaitableTimer(deadline.SafeWaitHandle);
                        if (WaitHandle.WaitAny(dormant) == 0) return;
                        continue;
                    }
                    long due = -interval * 10000L;
                    if (!SetWaitableTimer(deadline.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        enabled = false;
                        continue;
                    }
                    int result = WaitHandle.WaitAny(active);
                    if (result == 0) return;
                    if (result == 2 && enabled) RequestFrame();
                }
            }
            catch (Exception exception)
            {
                if (!disposed) ErrorLog.Write("Presentation clock stopped", exception);
            }
            finally
            {
                enabled = false;
                disposed = true;
                ReleaseHandles();
            }
        }

        private void RequestFrame()
        {
            if (!enabled || Interlocked.CompareExchange(ref queued, 1, 0) != 0) return;
            try
            {
                owner.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        if (!enabled || disposed || owner.IsDisposed) return;
                        EventHandler handler = Tick;
                        if (handler != null) handler(this, EventArgs.Empty);
                    }
                    finally { Interlocked.Exchange(ref queued, 0); }
                });
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref queued, 0); }
        }

        public void Dispose()
        {
            if (disposed) return;
            enabled = false;
            disposed = true;
            Signal(shutdown);
            if (worker == null) ReleaseHandles();
            else if (worker != Thread.CurrentThread) worker.Join(500);
            // The worker owns its handles until the actual wait has ended.
        }

        private void ReleaseHandles()
        {
            if (Interlocked.Exchange(ref handlesReleased, 1) != 0) return;
            deadline.Dispose();
            changed.Dispose();
            shutdown.Dispose();
        }

        private static void Signal(EventWaitHandle signal)
        {
            try { signal.Set(); }
            catch (ObjectDisposedException) { }
        }

        private sealed class TimerHandle : WaitHandle
        {
            internal TimerHandle(IntPtr handle) { SafeWaitHandle = new SafeWaitHandle(handle, true); }
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period,
            IntPtr completion, IntPtr argument, bool resume);
        [DllImport("kernel32.dll")] private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
    }
}
