using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenCapture
{
    /// <summary>
    /// Uses inexpensive WinEvent notifications to invalidate the prewarmed shell
    /// zone cache. This replaces continuous UI Automation rescans while preserving
    /// immediate updates when the foreground app, taskbar, or tray changes.
    /// </summary>
    internal sealed class ShellChangeWatcher : IDisposable
    {
        private const uint EventSystemForeground = 0x0003;
        private const uint EventObjectCreate = 0x8000;
        private const uint EventObjectLocationChange = 0x800B;
        private const uint WineventOutOfContext = 0x0000;
        private const uint WineventSkipOwnProcess = 0x0002;

        private readonly WinEventProc callback;
        private readonly List<IntPtr> hooks;
        private int changed;
        private int disposed;

        internal ShellChangeWatcher()
        {
            callback = WinEventReceived;
            hooks = new List<IntPtr>(2);
            AddHook(EventSystemForeground, EventSystemForeground);
            AddHook(EventObjectCreate, EventObjectLocationChange);
        }

        internal bool IsOperational
        {
            get { return hooks.Count > 0; }
        }

        /// <summary>
        /// Returns and clears the accumulated invalidation. Multiple noisy shell
        /// events collapse into one background refresh.
        /// </summary>
        internal bool ConsumeChanged()
        {
            return Interlocked.Exchange(ref changed, 0) != 0;
        }

        internal void MarkChanged()
        {
            if (Interlocked.CompareExchange(ref disposed, 0, 0) == 0)
            {
                Interlocked.Exchange(ref changed, 1);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            foreach (IntPtr hook in hooks)
            {
                if (hook != IntPtr.Zero)
                {
                    UnhookWinEvent(hook);
                }
            }
            hooks.Clear();
        }

        private void AddHook(uint eventMinimum, uint eventMaximum)
        {
            IntPtr hook = SetWinEventHook(
                eventMinimum,
                eventMaximum,
                IntPtr.Zero,
                callback,
                0,
                0,
                WineventOutOfContext | WineventSkipOwnProcess);
            if (hook != IntPtr.Zero)
            {
                hooks.Add(hook);
            }
        }

        private void WinEventReceived(
            IntPtr hook,
            uint eventType,
            IntPtr hWnd,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            try
            {
                if (Interlocked.CompareExchange(ref disposed, 0, 0) != 0)
                {
                    return;
                }

                // A foreground transition covers app launches, closes, flyouts, Start,
                // calendar/action centre, and taskbar interactions without scanning UIA.
                if (eventType == EventSystemForeground)
                {
                    MarkChanged();
                    return;
                }

                if (hWnd == IntPtr.Zero)
                {
                    return;
                }

                IntPtr root = NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOT);
                if (root == IntPtr.Zero)
                {
                    root = hWnd;
                }

                string className = NativeMethods.GetWindowClassName(root);
                if (IsShellSurfaceClass(className))
                {
                    MarkChanged();
                }
            }
            catch
            {
                // This method is an unmanaged WinEvent callback. No managed
                // exception may cross that boundary and terminate the process.
            }
        }

        private static bool IsShellSurfaceClass(string className)
        {
            if (string.IsNullOrEmpty(className))
            {
                return false;
            }

            return className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase) ||
                className.IndexOf("Taskbar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("Tray", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private delegate void WinEventProc(
            IntPtr hook,
            uint eventType,
            IntPtr hWnd,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMinimum,
            uint eventMaximum,
            IntPtr eventHookModule,
            WinEventProc eventHookProc,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hook);
    }
}
