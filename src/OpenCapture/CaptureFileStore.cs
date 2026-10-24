using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace OpenCapture
{
    internal static class CaptureFileStore
    {
        private static readonly Regex CurrentName = new Regex(
            @"^\d{2}-\d{2}-\d{2}(?:\.\d{3})? --- \d{2}-\d{2}-\d{2}\.png$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex LegacyName = new Regex(
            @"^OpenCapture_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}-\d{3}(?:_\d+)?\.png$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        internal static string GetUniqueCapturePath(string capturesDirectory, DateTime capturedAt)
        {
            if (string.IsNullOrEmpty(capturesDirectory))
            {
                throw new ArgumentException("A capture directory is required.", "capturesDirectory");
            }

            Directory.CreateDirectory(capturesDirectory);
            string candidate = Path.Combine(
                capturesDirectory,
                capturedAt.ToString("HH-mm-ss --- dd-MM-yy", CultureInfo.InvariantCulture) + ".png");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            DateTime millisecondName = capturedAt;
            int attempt;
            for (attempt = 0; attempt < 1000; attempt++)
            {
                candidate = Path.Combine(
                    capturesDirectory,
                    millisecondName.ToString("HH-mm-ss.fff --- dd-MM-yy", CultureInfo.InvariantCulture) + ".png");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
                millisecondName = millisecondName.AddMilliseconds(1);
            }

            throw new IOException("OpenCapture could not create a unique timestamp name for this screenshot.");
        }

        internal static void RegisterScreenshot(string capturesDirectory, string screenshotPath)
        {
            CreateStore(capturesDirectory).Register(screenshotPath);
        }

        internal static void MarkScreenshotRenamed(
            string capturesDirectory,
            string oldPath,
            string newPath)
        {
            CreateStore(capturesDirectory).MarkRenamed(oldPath, newPath);
        }

        internal static IList<string> GetScreenshotFiles(string capturesDirectory)
        {
            return CreateStore(capturesDirectory).GetClearableFiles();
        }

        internal static int ClearScreenshotsToRecycleBin(string capturesDirectory)
        {
            return CreateStore(capturesDirectory).ClearToRecycleBin();
        }

        internal static bool IsScreenshotFileName(string fileName)
        {
            return !string.IsNullOrEmpty(fileName) &&
                (CurrentName.IsMatch(fileName) || LegacyName.IsMatch(fileName));
        }

        private static ManagedMediaFileStore CreateStore(string capturesDirectory)
        {
            return new ManagedMediaFileStore(
                capturesDirectory,
                ".opencapture-ownership-v1",
                "OpenCapture capture ownership v1",
                "*.png",
                IsScreenshotFileName);
        }
    }
}
