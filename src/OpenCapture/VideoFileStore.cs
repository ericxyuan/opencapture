using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OpenCapture
{
    internal static class VideoFileStore
    {
        private static readonly Regex CurrentName = new Regex(
            @"^\d{2}-\d{2}-\d{2}(?:\.\d{3})? --- \d{2}-\d{2}-\d{2}\.avi$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        internal static string GetUniqueVideoPath(string videosDirectory, DateTime startedAt)
        {
            if (string.IsNullOrEmpty(videosDirectory))
            {
                throw new ArgumentException("A video directory is required.", "videosDirectory");
            }

            Directory.CreateDirectory(videosDirectory);
            string candidate = Path.Combine(
                videosDirectory,
                startedAt.ToString("HH-mm-ss --- dd-MM-yy", CultureInfo.InvariantCulture) + ".avi");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            DateTime millisecondName = startedAt;
            int attempt;
            for (attempt = 0; attempt < 1000; attempt++)
            {
                candidate = Path.Combine(
                    videosDirectory,
                    millisecondName.ToString("HH-mm-ss.fff --- dd-MM-yy", CultureInfo.InvariantCulture) + ".avi");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
                millisecondName = millisecondName.AddMilliseconds(1);
            }

            throw new IOException("OpenCapture could not create a unique timestamp name for this recording.");
        }

        internal static void RegisterRecording(string videosDirectory, string recordingPath)
        {
            CreateStore(videosDirectory).Register(recordingPath);
        }

        internal static void MarkRecordingRenamed(
            string videosDirectory,
            string oldPath,
            string newPath)
        {
            CreateStore(videosDirectory).MarkRenamed(oldPath, newPath);
        }

        internal static IList<string> GetRecordingFiles(string videosDirectory)
        {
            return CreateStore(videosDirectory).GetClearableFiles();
        }

        internal static int ClearRecordingsToRecycleBin(string videosDirectory)
        {
            return CreateStore(videosDirectory).ClearToRecycleBin();
        }

        internal static bool IsRecordingFileName(string fileName)
        {
            return !string.IsNullOrEmpty(fileName) && CurrentName.IsMatch(fileName);
        }

        private static ManagedMediaFileStore CreateStore(string videosDirectory)
        {
            return new ManagedMediaFileStore(
                videosDirectory,
                ".opencapture-recording-ownership-v1",
                "OpenCapture recording ownership v1",
                "*.avi",
                IsRecordingFileName);
        }
    }
}
