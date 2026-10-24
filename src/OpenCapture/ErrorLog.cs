using System;
using System.IO;
using System.Text;

namespace OpenCapture
{
    internal static class ErrorLog
    {
        private static readonly object SyncRoot = new object();

        internal static string FilePath
        {
            get { return Path.Combine(AppContext.BaseDirectory, "OpenCapture.log"); }
        }

        internal static void Write(string context, Exception exception)
        {
            try
            {
                StringBuilder entry = new StringBuilder();
                entry.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                entry.AppendLine(context ?? "OpenCapture error");
                if (exception != null)
                {
                    entry.AppendLine(exception.ToString());
                }
                entry.AppendLine();

                lock (SyncRoot)
                {
                    string text = entry.ToString();
                    try
                    {
                        File.AppendAllText(FilePath, text, Encoding.UTF8);
                    }
                    catch
                    {
                        string fallbackDirectory = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "OpenCapture");
                        Directory.CreateDirectory(fallbackDirectory);
                        File.AppendAllText(
                            Path.Combine(fallbackDirectory, "OpenCapture.log"),
                            text,
                            Encoding.UTF8);
                    }
                }
            }
            catch
            {
                // Logging must never take down the tray process.
            }
        }
    }
}
