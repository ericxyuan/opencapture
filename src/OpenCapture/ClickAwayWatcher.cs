using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenCapture
{
    internal sealed class ClickAwayWatcher : IDisposable
    {
        private readonly HookProc callback;
        private readonly Action<Point> clicked;
        private IntPtr hook;

        internal ClickAwayWatcher(Action<Point> onClick)
        {
            clicked = onClick;
            callback = HandleMouse;
            hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        }

        private IntPtr HandleMouse(int code, IntPtr message, IntPtr data)
        {
            try
            {
                int id = unchecked((int)message.ToInt64());
                if (code >= 0 && (id == 0x0201 || id == 0x0204 || id == 0x0207 || id == 0x020B))
                {
                    // POINT is the first field of MSLLHOOKSTRUCT. The callback
                    // only queues a UI message; it never saves or consumes input.
                    NativeMethods.POINT point = (NativeMethods.POINT)Marshal.PtrToStructure(data, typeof(NativeMethods.POINT));
                    clicked(point.ToPoint());
                }
            }
            catch { }
            return CallNextHookEx(hook, code, message, data);
        }

        public void Dispose()
        {
            IntPtr previous = hook;
            hook = IntPtr.Zero;
            if (previous != IntPtr.Zero) UnhookWindowsHookEx(previous);
        }

        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
        private static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
    }
}
