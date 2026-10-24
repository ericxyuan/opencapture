using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenCapture
{
    internal static class CopiedTextFileStore
    {
        private const int MaximumBaseNameLength = 140;

        private static readonly HashSet<string> ReservedNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5",
                "COM6", "COM7", "COM8", "COM9",
                "COM\u00b9", "COM\u00b2", "COM\u00b3",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
                "LPT6", "LPT7", "LPT8", "LPT9",
                "LPT\u00b9", "LPT\u00b2", "LPT\u00b3"
            };

        internal static string GetUniquePath(string directory, string copiedText)
        {
            if (string.IsNullOrEmpty(directory))
            {
                throw new ArgumentException("A copied-text directory is required.", "directory");
            }

            string baseName = MakeSafeBaseName(copiedText);
            if (baseName.Length == 0)
            {
                throw new ArgumentException("Recognised text is required.", "copiedText");
            }

            Directory.CreateDirectory(directory);
            string candidate = Path.Combine(directory, baseName + ".png");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            int suffix;
            for (suffix = 2; suffix < 10000; suffix++)
            {
                string suffixText = " (" + suffix + ")";
                int available = Math.Max(1, MaximumBaseNameLength - suffixText.Length);
                string shortened = baseName.Length > available
                    ? baseName.Substring(0, available).TrimEnd(' ', '.')
                    : baseName;
                candidate = Path.Combine(directory, shortened + suffixText + ".png");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new IOException("OpenCapture could not create a unique name for this copied text image.");
        }

        internal static string MakeSafeBaseName(string copiedText)
        {
            if (string.IsNullOrWhiteSpace(copiedText))
            {
                return string.Empty;
            }

            HashSet<char> invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            StringBuilder safe = new StringBuilder(Math.Min(copiedText.Length, MaximumBaseNameLength));
            bool pendingSpace = false;
            foreach (char character in copiedText)
            {
                if (safe.Length >= MaximumBaseNameLength)
                {
                    break;
                }
                if (char.IsWhiteSpace(character))
                {
                    pendingSpace = safe.Length > 0;
                    continue;
                }

                if (pendingSpace && safe.Length < MaximumBaseNameLength)
                {
                    safe.Append(' ');
                }
                pendingSpace = false;

                if (!char.IsControl(character) && !invalid.Contains(character) &&
                    safe.Length < MaximumBaseNameLength)
                {
                    safe.Append(character);
                }
            }

            string result = safe.ToString().Trim(' ', '.');
            int firstPeriod = result.IndexOf('.');
            string deviceName = firstPeriod < 0 ? result : result.Substring(0, firstPeriod);
            if (ReservedNames.Contains(deviceName.TrimEnd(' ')))
            {
                // Windows reserves device names even when followed by an
                // extension, so change the name before its first period.
                result = result.Insert(firstPeriod < 0 ? result.Length : firstPeriod, " text");
                if (result.Length > MaximumBaseNameLength)
                {
                    result = result.Substring(0, MaximumBaseNameLength).TrimEnd(' ', '.');
                }
            }
            return result;
        }
    }
}
