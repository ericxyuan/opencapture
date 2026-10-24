using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenCapture
{
    /// <summary>
    /// Win32 interop used by capture, the selection overlay, and zone discovery.
    /// Coordinates returned by this class are physical desktop pixels when the
    /// process is initialized with per-monitor-v2 awareness before creating a HWND.
    /// </summary>
    internal static class NativeMethods
    {
        internal const int WM_HOTKEY = 0x0312;

        internal const uint MOD_ALT = 0x0001;
        internal const uint MOD_CONTROL = 0x0002;
        internal const uint MOD_SHIFT = 0x0004;
        internal const uint MOD_WIN = 0x0008;
        internal const uint MOD_NOREPEAT = 0x4000;
        internal const uint VK_Q = 0x51;

        internal const int SM_XVIRTUALSCREEN = 76;
        internal const int SM_YVIRTUALSCREEN = 77;
        internal const int SM_CXVIRTUALSCREEN = 78;
        internal const int SM_CYVIRTUALSCREEN = 79;

        internal const int GWL_STYLE = -16;
        internal const int GWL_EXSTYLE = -20;
        internal const long WS_CHILD = 0x40000000L;
        internal const long WS_DISABLED = 0x08000000L;
        internal const long WS_EX_TOOLWINDOW = 0x00000080L;
        internal const long WS_EX_TRANSPARENT = 0x00000020L;
        internal const long WS_EX_LAYERED = 0x00080000L;
        internal const long WS_EX_NOACTIVATE = 0x08000000L;

        internal const uint GA_ROOT = 2;
        internal const uint MONITOR_DEFAULTTONULL = 0;
        internal const uint MONITOR_DEFAULTTOPRIMARY = 1;
        internal const uint MONITOR_DEFAULTTONEAREST = 2;

        internal const uint CWP_SKIPINVISIBLE = 0x0001;
        internal const uint CWP_SKIPDISABLED = 0x0002;
        internal const uint CWP_SKIPTRANSPARENT = 0x0004;

        internal const uint SRCCOPY = 0x00CC0020;
        internal const uint CAPTUREBLT = 0x40000000;

        internal const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        internal const uint DWMWA_CLOAKED = 14;

        internal const int MDT_EFFECTIVE_DPI = 0;
        internal const uint USER_DEFAULT_SCREEN_DPI = 96;

        internal const int ERROR_ACCESS_DENIED = 5;
        internal const int S_OK = 0;
        internal const int E_ACCESSDENIED = unchecked((int)0x80070005);

        // Pseudo-handles are documented negative constants; callers never own them.
        internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        internal delegate bool MonitorEnumProc(
            IntPtr hMonitor,
            IntPtr hdcMonitor,
            ref RECT monitorRect,
            IntPtr userData);

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            internal int X;
            internal int Y;

            internal POINT(int x, int y)
            {
                X = x;
                Y = y;
            }

            internal POINT(Point point)
            {
                X = point.X;
                Y = point.Y;
            }

            internal Point ToPoint()
            {
                return new Point(X, Y);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;

            internal RECT(int left, int top, int right, int bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }

            internal int Width
            {
                get { return Right - Left; }
            }

            internal int Height
            {
                get { return Bottom - Top; }
            }

            internal bool HasArea
            {
                get { return Right > Left && Bottom > Top; }
            }

            internal Rectangle ToRectangle()
            {
                return Rectangle.FromLTRB(Left, Top, Right, Bottom);
            }

            internal static RECT FromRectangle(Rectangle rectangle)
            {
                return new RECT(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MONITORINFOEX
        {
            internal int cbSize;
            internal RECT rcMonitor;
            internal RECT rcWork;
            internal uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            internal string szDevice;

            internal static MONITORINFOEX Create()
            {
                MONITORINFOEX info = new MONITORINFOEX();
                info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                info.szDevice = string.Empty;
                return info;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WINDOWINFO
        {
            internal int cbSize;
            internal RECT rcWindow;
            internal RECT rcClient;
            internal uint dwStyle;
            internal uint dwExStyle;
            internal uint dwWindowStatus;
            internal uint cxWindowBorders;
            internal uint cyWindowBorders;
            internal ushort atomWindowType;
            internal ushort wCreatorVersion;

            internal static WINDOWINFO Create()
            {
                WINDOWINFO info = new WINDOWINFO();
                info.cbSize = Marshal.SizeOf(typeof(WINDOWINFO));
                return info;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WINDOWPLACEMENT
        {
            internal int length;
            internal int flags;
            internal int showCmd;
            internal POINT ptMinPosition;
            internal POINT ptMaxPosition;
            internal RECT rcNormalPosition;

            internal static WINDOWPLACEMENT Create()
            {
                WINDOWPLACEMENT placement = new WINDOWPLACEMENT();
                placement.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                return placement;
            }
        }

        internal enum ProcessDpiAwareness
        {
            ProcessDpiUnaware = 0,
            ProcessSystemDpiAware = 1,
            ProcessPerMonitorDpiAware = 2
        }

        /// <summary>
        /// Acquires a screen/window DC. A nonzero result is owned by the caller and
        /// must be paired with ReleaseDC for the same HWND; never call DeleteDC on it.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetDC(IntPtr hWnd);

        /// <summary>
        /// Releases a DC obtained from GetDC. This does not accept Graphics.GetHdc
        /// handles, which must instead be returned through Graphics.ReleaseHdc.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BitBlt(
            IntPtr destinationDC,
            int destinationX,
            int destinationY,
            int width,
            int height,
            IntPtr sourceDC,
            int sourceX,
            int sourceY,
            uint rasterOperation);

        [DllImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKeyNative(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", EntryPoint = "UnregisterHotKey", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKeyNative(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint RegisterClipboardFormat(string formatName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenClipboard(IntPtr newOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(
            IntPtr parentWindow,
            EnumWindowsProc callback,
            IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(
            IntPtr hdc,
            IntPtr clipRect,
            MonitorEnumProc callback,
            IntPtr userData);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rectangle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr hWnd, out RECT rectangle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowInfo(IntPtr hWnd, ref WINDOWINFO info);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowTextLengthW(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder className, int maximumCount);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        internal static extern IntPtr RealChildWindowFromPoint(IntPtr parent, POINT point);

        [DllImport("user32.dll")]
        internal static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT point, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEX info);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", EntryPoint = "GetSystemMetricsForDpi")]
        private static extern int GetSystemMetricsForDpiNative(int index, uint dpi);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll", EntryPoint = "GetPhysicalCursorPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPhysicalCursorPosNative(out POINT point);

        [DllImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDpiAwarenessContextNative(IntPtr value);

        [DllImport("user32.dll", EntryPoint = "GetThreadDpiAwarenessContext")]
        private static extern IntPtr GetThreadDpiAwarenessContextNative();

        [DllImport("user32.dll", EntryPoint = "AreDpiAwarenessContextsEqual")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AreDpiAwarenessContextsEqualNative(IntPtr first, IntPtr second);

        [DllImport("user32.dll", EntryPoint = "SetProcessDPIAware", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDPIAwareNative();

        [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
        private static extern uint GetDpiForWindowNative(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetDpiForSystem")]
        private static extern uint GetDpiForSystemNative();

        [DllImport("user32.dll", EntryPoint = "AdjustWindowRectExForDpi", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustWindowRectExForDpiNative(
            ref RECT rectangle,
            uint style,
            [MarshalAs(UnmanagedType.Bool)] bool hasMenu,
            uint extendedStyle,
            uint dpi);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(ProcessDpiAwareness awareness);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(
            IntPtr monitor,
            int dpiType,
            out uint dpiX,
            out uint dpiY);

        [DllImport("dwmapi.dll", EntryPoint = "DwmFlush")]
        private static extern int DwmFlushNative();

        [DllImport("dwmapi.dll", EntryPoint = "DwmIsCompositionEnabled")]
        private static extern int DwmIsCompositionEnabledNative(
            [MarshalAs(UnmanagedType.Bool)] out bool enabled);

        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int DwmGetWindowAttributeRect(
            IntPtr hWnd,
            uint attribute,
            out RECT value,
            int valueSize);

        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int DwmGetWindowAttributeInt(
            IntPtr hWnd,
            uint attribute,
            out int value,
            int valueSize);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryPerformanceCounter(out long value);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryPerformanceFrequency(out long value);

        [DllImport("kernel32.dll")]
        internal static extern int MulDiv(int number, int numerator, int denominator);

        /// <summary>
        /// Enables the best available DPI mode. Call once, before Application.Run and
        /// before any control or hidden native window is constructed. The return value
        /// is true only when PMv2 is active; older systems are left in their best
        /// available (PMv1 or system-aware) mode.
        /// </summary>
        internal static bool TryEnablePerMonitorV2DpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContextNative(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                {
                    return true;
                }

                if (Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED && IsPerMonitorV2DpiAware())
                {
                    return true;
                }
            }
            catch (EntryPointNotFoundException)
            {
                // Windows versions before the Creators Update do not expose PMv2.
            }
            catch (DllNotFoundException)
            {
                // The user32 fallback below still applies.
            }

            try
            {
                int result = SetProcessDpiAwareness(ProcessDpiAwareness.ProcessPerMonitorDpiAware);
                if (result == S_OK || result == E_ACCESSDENIED)
                {
                    return false;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }

            try
            {
                SetProcessDPIAwareNative();
            }
            catch (EntryPointNotFoundException)
            {
            }

            return false;
        }

        internal static bool IsPerMonitorV2DpiAware()
        {
            try
            {
                IntPtr current = GetThreadDpiAwarenessContextNative();
                return current != IntPtr.Zero &&
                    AreDpiAwarenessContextsEqualNative(
                        current,
                        DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        /// <summary>
        /// Registers a process-global key owned by <paramref name="ownerWindow"/>.
        /// The owner must call UnregisterHotKey with the same id before destroying its HWND.
        /// </summary>
        internal static bool RegisterHotKey(IntPtr ownerWindow, int id, uint modifiers, uint virtualKey)
        {
            return RegisterHotKeyNative(ownerWindow, id, modifiers, virtualKey);
        }

        internal static void RegisterHotKeyOrThrow(
            IntPtr ownerWindow,
            int id,
            uint modifiers,
            uint virtualKey)
        {
            if (!RegisterHotKey(ownerWindow, id, modifiers, virtualKey))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the global hotkey.");
            }
        }

        internal static bool UnregisterHotKey(IntPtr ownerWindow, int id)
        {
            return UnregisterHotKeyNative(ownerWindow, id);
        }

        internal static Rectangle GetVirtualScreenBounds()
        {
            return new Rectangle(
                GetSystemMetrics(SM_XVIRTUALSCREEN),
                GetSystemMetrics(SM_YVIRTUALSCREEN),
                GetSystemMetrics(SM_CXVIRTUALSCREEN),
                GetSystemMetrics(SM_CYVIRTUALSCREEN));
        }

        internal static int GetSystemMetricForDpi(int index, uint dpi)
        {
            try
            {
                return GetSystemMetricsForDpiNative(index, dpi);
            }
            catch (EntryPointNotFoundException)
            {
                return MulDiv(GetSystemMetrics(index), (int)dpi, (int)USER_DEFAULT_SCREEN_DPI);
            }
        }

        internal static uint GetDpiForWindowSafe(IntPtr hWnd)
        {
            if (hWnd != IntPtr.Zero)
            {
                try
                {
                    uint windowDpi = GetDpiForWindowNative(hWnd);
                    if (windowDpi != 0)
                    {
                        return windowDpi;
                    }
                }
                catch (EntryPointNotFoundException)
                {
                }
            }

            return GetSystemDpiSafe();
        }

        internal static uint GetDpiForPoint(Point desktopPoint)
        {
            IntPtr monitor = MonitorFromPoint(new POINT(desktopPoint), MONITOR_DEFAULTTONEAREST);
            uint dpiX;
            uint dpiY;
            if (monitor != IntPtr.Zero && TryGetMonitorDpi(monitor, out dpiX, out dpiY))
            {
                return dpiX;
            }

            return GetSystemDpiSafe();
        }

        internal static uint GetSystemDpiSafe()
        {
            try
            {
                uint dpi = GetDpiForSystemNative();
                if (dpi != 0)
                {
                    return dpi;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }

            return USER_DEFAULT_SCREEN_DPI;
        }

        internal static bool TryGetMonitorDpi(IntPtr monitor, out uint dpiX, out uint dpiY)
        {
            dpiX = USER_DEFAULT_SCREEN_DPI;
            dpiY = USER_DEFAULT_SCREEN_DPI;

            try
            {
                return GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) == S_OK;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }

        internal static int ScaleForDpi(int logicalPixels, uint dpi)
        {
            return MulDiv(logicalPixels, (int)dpi, (int)USER_DEFAULT_SCREEN_DPI);
        }

        internal static bool TryAdjustWindowRectForDpi(
            ref RECT rectangle,
            uint style,
            bool hasMenu,
            uint extendedStyle,
            uint dpi)
        {
            try
            {
                return AdjustWindowRectExForDpiNative(
                    ref rectangle,
                    style,
                    hasMenu,
                    extendedStyle,
                    dpi);
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        internal static bool TryGetPhysicalCursorPosition(out Point position)
        {
            POINT point;
            try
            {
                if (GetPhysicalCursorPosNative(out point))
                {
                    position = point.ToPoint();
                    return true;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }

            if (GetCursorPos(out point))
            {
                position = point.ToPoint();
                return true;
            }

            position = Point.Empty;
            return false;
        }

        internal static IList<IntPtr> EnumerateTopLevelWindows()
        {
            List<IntPtr> windows = new List<IntPtr>();
            EnumWindowsProc callback = delegate(IntPtr hWnd, IntPtr ignored)
            {
                windows.Add(hWnd);
                return true;
            };

            if (!EnumWindows(callback, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    throw new Win32Exception(error, "Could not enumerate top-level windows.");
                }
            }

            return windows.AsReadOnly();
        }

        internal static IList<IntPtr> EnumerateMonitors()
        {
            List<IntPtr> monitors = new List<IntPtr>();
            MonitorEnumProc callback = delegate(
                IntPtr monitor,
                IntPtr ignoredHdc,
                ref RECT ignoredBounds,
                IntPtr ignoredData)
            {
                monitors.Add(monitor);
                return true;
            };

            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate monitors.");
            }

            return monitors.AsReadOnly();
        }

        internal static bool TryGetMonitorInfo(IntPtr monitor, out MONITORINFOEX info)
        {
            info = MONITORINFOEX.Create();
            return monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info);
        }

        internal static bool TryGetWindowRectangle(IntPtr hWnd, out Rectangle bounds)
        {
            RECT rectangle;
            if (hWnd != IntPtr.Zero && GetWindowRect(hWnd, out rectangle) && rectangle.HasArea)
            {
                bounds = rectangle.ToRectangle();
                return true;
            }

            bounds = Rectangle.Empty;
            return false;
        }

        /// <summary>
        /// Gets the visible DWM frame, excluding the invisible resize border. Falls
        /// back to GetWindowRect when DWM does not expose an extended-frame value.
        /// </summary>
        internal static bool TryGetExtendedFrameBounds(IntPtr hWnd, out Rectangle bounds)
        {
            RECT rectangle;
            try
            {
                int result = DwmGetWindowAttributeRect(
                    hWnd,
                    DWMWA_EXTENDED_FRAME_BOUNDS,
                    out rectangle,
                    Marshal.SizeOf(typeof(RECT)));
                if (result >= 0 && rectangle.HasArea)
                {
                    bounds = rectangle.ToRectangle();
                    return true;
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }

            return TryGetWindowRectangle(hWnd, out bounds);
        }

        internal static bool IsWindowCloaked(IntPtr hWnd)
        {
            int cloaked;
            try
            {
                int result = DwmGetWindowAttributeInt(
                    hWnd,
                    DWMWA_CLOAKED,
                    out cloaked,
                    sizeof(int));
                return result >= 0 && cloaked != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        // Raw overloads are retained for the immutable zone snapshot. Prefer the
        // higher-level helpers elsewhere so HRESULT and fallback handling stay local.
        internal static int DwmGetWindowAttribute(
            IntPtr hWnd,
            int attribute,
            out RECT value,
            int valueSize)
        {
            return DwmGetWindowAttributeRect(hWnd, (uint)attribute, out value, valueSize);
        }

        internal static int DwmGetWindowAttribute(
            IntPtr hWnd,
            int attribute,
            out int value,
            int valueSize)
        {
            return DwmGetWindowAttributeInt(hWnd, (uint)attribute, out value, valueSize);
        }

        internal static bool IsUsableTopLevelWindow(IntPtr hWnd, IntPtr excludedWindow)
        {
            if (hWnd == IntPtr.Zero || hWnd == excludedWindow || !IsWindowVisible(hWnd) || IsIconic(hWnd))
            {
                return false;
            }

            if (IsWindowCloaked(hWnd) || (GetWindowStyle(hWnd, GWL_STYLE) & WS_CHILD) != 0)
            {
                return false;
            }

            Rectangle bounds;
            return TryGetExtendedFrameBounds(hWnd, out bounds) &&
                bounds.IntersectsWith(GetVirtualScreenBounds());
        }

        internal static long GetWindowStyle(IntPtr hWnd, int index)
        {
            if (IntPtr.Size == 8)
            {
                return GetWindowLongPtr64(hWnd, index).ToInt64();
            }

            return GetWindowLong32(hWnd, index);
        }

        internal static string GetWindowTitle(IntPtr hWnd)
        {
            int length = GetWindowTextLengthW(hWnd);
            if (length <= 0)
            {
                return string.Empty;
            }

            StringBuilder text = new StringBuilder(length + 1);
            int copied = GetWindowTextW(hWnd, text, text.Capacity);
            return copied > 0 ? text.ToString(0, copied) : string.Empty;
        }

        internal static string GetWindowClassName(IntPtr hWnd)
        {
            StringBuilder className = new StringBuilder(256);
            int copied = GetClassNameW(hWnd, className, className.Capacity);
            return copied > 0 ? className.ToString(0, copied) : string.Empty;
        }

        internal static bool TryDwmFlush()
        {
            try
            {
                return DwmFlushNative() >= 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        internal static bool IsDwmCompositionEnabled()
        {
            bool enabled;
            try
            {
                return DwmIsCompositionEnabledNative(out enabled) >= 0 && enabled;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }
    }
}
