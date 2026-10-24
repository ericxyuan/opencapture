using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenCapture
{
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int HotkeyId = 0x4F43;
        private bool registered;
        private bool disposed;
        private readonly ShortcutBinding shortcut;

        internal HotkeyWindow()
            : this(ShortcutSettings.DefaultCapture)
        {
        }

        internal HotkeyWindow(ShortcutBinding captureShortcut)
        {
            if (captureShortcut == null || captureShortcut.Kind != ShortcutInputKind.Keyboard)
            {
                throw new ArgumentException("A keyboard capture shortcut is required.", "captureShortcut");
            }

            shortcut = captureShortcut;
            CreateParams parameters = new CreateParams();
            parameters.Caption = "OpenCapture hotkey receiver";
            parameters.Parent = new IntPtr(-3); // HWND_MESSAGE
            CreateHandle(parameters);

            registered = NativeMethods.RegisterHotKey(
                Handle,
                HotkeyId,
                ToNativeModifiers(shortcut.Modifiers) | NativeMethods.MOD_NOREPEAT,
                (uint)shortcut.KeyCode);
            if (!registered)
            {
                int error = Marshal.GetLastWin32Error();
                DestroyHandle();
                throw new Win32Exception(
                    error,
                    shortcut.DisplayText + " is already being used by Windows or another application.");
            }
        }

        internal event EventHandler Pressed;

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WM_HOTKEY && message.WParam.ToInt32() == HotkeyId)
            {
                EventHandler handler = Pressed;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
                return;
            }

            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (registered && Handle != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(Handle, HotkeyId);
                registered = false;
            }

            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }

        private static uint ToNativeModifiers(Keys modifiers)
        {
            uint result = 0;
            if ((modifiers & Keys.Alt) == Keys.Alt) result |= NativeMethods.MOD_ALT;
            if ((modifiers & Keys.Control) == Keys.Control) result |= NativeMethods.MOD_CONTROL;
            if ((modifiers & Keys.Shift) == Keys.Shift) result |= NativeMethods.MOD_SHIFT;
            return result;
        }
    }
}
