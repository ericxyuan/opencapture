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
                TestReusedPaintBuffer(shortcuts, autostart, captures, recordings, clipboard, quit);
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
                    // Inspect the real WM_PAINT path as well as DrawToBitmap:
                    // repeated row invalidation exposed the stale native buffer.
                    menu.ShowAt(new Point(work.Right, work.Bottom));
                    for (int repaint = 0; repaint < 12; repaint++)
                        foreach (Control item in menu.Controls) { item.Invalidate(); item.Update(); }
                    menu.Update();
                    using (Bitmap live = new Bitmap(menu.Width, menu.Height, PixelFormat.Format32bppArgb))
                    using (Graphics graphics = Graphics.FromImage(live))
                    {
                        graphics.CopyFromScreen(menu.Location, Point.Empty, menu.Size);
                        string livePath = Path.Combine(Path.GetTempPath(), "OpenCapture-tray-menu-live.png");
                        live.Save(livePath, ImageFormat.Png);
                        Console.WriteLine("LIVE_TRAY_PREVIEW=" + livePath);
                    }
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
        private static void TestReusedPaintBuffer(params Button[] items)
        {
            // Native double buffering can reuse a bitmap that held another row.
            // Each owner-painted row must produce the same pixels regardless of
            // the buffer's old contents, including its rounded outer corners.
            MethodInfo paint = items[0].GetType().GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic);
            using (Bitmap reused = new Bitmap(items[0].Width, items[0].Height, PixelFormat.Format32bppArgb))
            using (Graphics reusedGraphics = Graphics.FromImage(reused))
            {
                reusedGraphics.Clear(Color.Magenta);
                foreach (Button item in items)
                using (Bitmap clean = new Bitmap(item.Width, item.Height, PixelFormat.Format32bppArgb))
                using (Graphics cleanGraphics = Graphics.FromImage(clean))
                {
                    cleanGraphics.Clear(item.Parent.BackColor);
                    paint.Invoke(item, new object[] { new PaintEventArgs(cleanGraphics, item.ClientRectangle) });
                    paint.Invoke(item, new object[] { new PaintEventArgs(reusedGraphics, item.ClientRectangle) });
                    for (int y = 0; y < item.Height; y++)
                        for (int x = 0; x < item.Width; x++)
                            Require(clean.GetPixel(x, y) == reused.GetPixel(x, y),
                                "menu row repaint must erase previous buffer contents: " + item.Text + " at " + x + "," + y);
                }
            }
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
