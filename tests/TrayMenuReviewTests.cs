using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace OpenCapture.Tests
{
    internal static class TrayMenuReviewTests
    {
        internal static void Run()
        {
            using (TrayMenuForm menu = new TrayMenuForm())
            using (ManualResetEvent release = new ManualResetEvent(false))
            using (ManualResetEvent started = new ManualResetEvent(false))
            {
                menu.AutostartEnabled = true;
                menu.ClipboardClearable = false;
                int shortcutActions = 0;
                int quitActions = 0;
                menu.EditKeybindsRequested += delegate { shortcutActions++; };
                menu.QuitRequested += delegate { quitActions++; };
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                menu.ShowAt(new Point(work.Right, work.Bottom));
                Application.DoEvents();
                Require(work.Contains(menu.Bounds), "tray menu fits inside the monitor work area");
                Button shortcuts = (Button)menu.Controls[0];
                Button autostart = (Button)menu.Controls[1];
                Button captures = (Button)menu.Controls[2];
                Button recordings = (Button)menu.Controls[3];
                Button clipboard = (Button)menu.Controls[4];
                Button quit = (Button)menu.Controls[5];
                Require(shortcuts.Focused && autostart.AccessibleName.EndsWith("On"),
                    "menu opens with keyboard focus and an explicit startup state");
                try
                {
                    int scans = 0;
                    menu.RefreshMediaState(delegate
                    {
                        Interlocked.Increment(ref scans);
                        started.Set();
                        if (!release.WaitOne(5000)) throw new TimeoutException("test storage was not released");
                        return true;
                    }, delegate { return false; });
                    Require(started.WaitOne(1500), "storage refresh starts in the background");
                    menu.RefreshMediaState(delegate { Interlocked.Increment(ref scans); return false; }, delegate { return true; });
                    Require(!captures.Enabled && !recordings.Enabled && shortcuts.Enabled && quit.Enabled,
                        "pending storage only disables file cleanup actions");
                    SendKey(menu, Keys.End);
                    Require(quit.Focused, "End reaches Quit while storage is still blocked");
                    SendKey(menu, Keys.Up);
                    Require(autostart.Focused, "arrow navigation skips unavailable cleanup actions");
                    SendKey(menu, Keys.Home);
                    Require(shortcuts.Focused, "Home returns to the first action");
                    release.Set();
                    PumpUntil(delegate { return captures.Enabled; }, 2000);
                    Require(captures.Enabled && !recordings.Enabled && !clipboard.Enabled && scans == 1,
                        "one coalesced refresh independently updates screenshot and recording availability");
                    using (Bitmap preview = new Bitmap(menu.Width, menu.Height, PixelFormat.Format32bppArgb))
                    {
                        menu.DrawToBitmap(preview, menu.ClientRectangle);
                        string path = Path.Combine(Path.GetTempPath(), "OpenCapture-refined-tray-menu.png");
                        preview.Save(path, ImageFormat.Png);
                        Console.WriteLine("REFINED_TRAY_PREVIEW=" + path);
                    }
                    SendKey(menu, Keys.Home);
                    SendKey(menu, Keys.Enter);
                    Require(shortcutActions == 1 && quitActions == 0 && !menu.Visible,
                        "Enter activates the focused action once and closes the menu");
                    menu.ShowAt(new Point(work.Right, work.Bottom));
                    SendKey(menu, Keys.Escape);
                    Require(!menu.Visible && quitActions == 0, "Escape closes without activating an action");
                    menu.ShowAt(new Point(work.Right, work.Bottom));
                    SendKey(menu, Keys.End);
                    SendKey(menu, Keys.Space);
                    Require(quitActions == 1 && !menu.Visible, "Space activates the focused Quit action once");
                }
                finally { release.Set(); }
            }
            // A refresh may complete after Quit has disposed the menu.
            using (ManualResetEvent completed = new ManualResetEvent(false))
            {
                TrayMenuForm closing = new TrayMenuForm();
                closing.ShowAt(Screen.PrimaryScreen.WorkingArea.Location);
                closing.RefreshMediaState(delegate { return true; }, delegate { completed.Set(); return true; });
                closing.Dispose();
                Require(completed.WaitOne(1500), "menu shutdown does not block a pending worker");
                Application.DoEvents();
            }
            Console.WriteLine("TRAY_MENU_REVIEW_PASS");
        }
        private static void SendKey(TrayMenuForm menu, Keys key)
        { typeof(TrayMenuForm).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(menu, new object[] { new Message(), key }); }
        private static void PumpUntil(Func<bool> done, int timeout)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!done() && timer.ElapsedMilliseconds < timeout) { Application.DoEvents(); Thread.Sleep(1); }
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
