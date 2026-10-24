using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace OpenCapture
{
    /// <summary>
    /// Persistent ownership tracking shared by screenshots and recordings.
    /// A file is clearable only while its original path and immutable identity
    /// still match. Renames, replacements, and edits retire it permanently.
    /// </summary>
    internal sealed class ManagedMediaFileStore
    {
        private static readonly object OwnershipSync = new object();

        private readonly string directory;
        private readonly string indexName;
        private readonly string indexHeader;
        private readonly string searchPattern;
        private readonly Func<string, bool> migrationPredicate;

        private sealed class OwnershipRecord
        {
            internal string FileName;
            internal long Length;
            internal long CreationTimeUtcTicks;
            internal long LastWriteTimeUtcTicks;
            internal bool Immune;
        }

        internal ManagedMediaFileStore(
            string mediaDirectory,
            string ownershipIndexName,
            string ownershipIndexHeader,
            string mediaSearchPattern,
            Func<string, bool> existingFileMigrationPredicate)
        {
            if (string.IsNullOrEmpty(mediaDirectory))
            {
                throw new ArgumentException("A media directory is required.", "mediaDirectory");
            }
            directory = Path.GetFullPath(mediaDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            indexName = ownershipIndexName;
            indexHeader = ownershipIndexHeader;
            searchPattern = mediaSearchPattern;
            migrationPredicate = existingFileMigrationPredicate;
            Directory.CreateDirectory(directory);
        }

        internal void Register(string mediaPath)
        {
            lock (OwnershipSync)
            {
                string resolvedMedia = ResolveTopLevelFile(mediaPath);
                if (!File.Exists(resolvedMedia))
                {
                    throw new FileNotFoundException(
                        "The completed OpenCapture media file could not be registered.",
                        resolvedMedia);
                }

                Dictionary<string, OwnershipRecord> records = LoadOwnershipRecords();
                OwnershipRecord record = CreateRecord(resolvedMedia);
                records[record.FileName] = record;
                SaveOwnershipRecords(records);
            }
        }

        internal void MarkRenamed(string oldPath, string newPath)
        {
            lock (OwnershipSync)
            {
                string oldName = GetTopLevelNameIfInside(oldPath);
                string newName = GetTopLevelNameIfInside(newPath);
                if (oldName == null && newName == null)
                {
                    return;
                }

                Dictionary<string, OwnershipRecord> records = LoadOwnershipRecords();
                bool changed = MarkImmune(records, oldName);
                changed = MarkImmune(records, newName) || changed;
                if (changed)
                {
                    SaveOwnershipRecords(records);
                }
            }
        }

        internal IList<string> GetClearableFiles()
        {
            lock (OwnershipSync)
            {
                Dictionary<string, OwnershipRecord> records = LoadOwnershipRecords();
                bool changed;
                IList<string> files = ReconcileClearableFiles(records, out changed);
                if (changed)
                {
                    SaveOwnershipRecords(records);
                }
                return files;
            }
        }

        internal int ClearToRecycleBin()
        {
            return ClearToRecycleBin(delegate(string path)
            {
                FileSystem.DeleteFile(
                    path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);
            });
        }

        internal int ClearToRecycleBin(Action<string> recycleFile)
        {
            if (recycleFile == null)
            {
                throw new ArgumentNullException("recycleFile");
            }
            lock (OwnershipSync)
            {
                Dictionary<string, OwnershipRecord> records = LoadOwnershipRecords();
                bool changed;
                IList<string> files = ReconcileClearableFiles(records, out changed);
                int deleted = 0;
                try
                {
                    int fileIndex;
                    for (fileIndex = 0; fileIndex < files.Count; fileIndex++)
                    {
                        string path = files[fileIndex];
                        string fileName = Path.GetFileName(path);
                        OwnershipRecord record = records[fileName];
                        // Recycling earlier files can take time. A later file
                        // may have been edited or replaced since reconciliation.
                        if (record.Immune || !File.Exists(path) || !MatchesIdentity(path, record))
                        {
                            record.Immune = true;
                            changed = true;
                            continue;
                        }
                        recycleFile(path);
                        records.Remove(fileName);
                        changed = true;
                        deleted++;
                    }
                }
                finally
                {
                    if (changed)
                    {
                        SaveOwnershipRecords(records);
                    }
                }
                return deleted;
            }
        }

        private string ResolveTopLevelFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("A media path is required.", "path");
            }
            string resolvedPath = Path.GetFullPath(path);
            string parent = Path.GetDirectoryName(resolvedPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Only files directly inside the OpenCapture media folder can be managed.");
            }
            return resolvedPath;
        }

        private string GetTopLevelNameIfInside(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            try
            {
                string resolvedPath = Path.GetFullPath(path);
                string parent = Path.GetDirectoryName(resolvedPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(resolvedPath)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private Dictionary<string, OwnershipRecord> LoadOwnershipRecords()
        {
            Dictionary<string, OwnershipRecord> records =
                new Dictionary<string, OwnershipRecord>(StringComparer.OrdinalIgnoreCase);
            string indexPath = Path.Combine(directory, indexName);
            if (!File.Exists(indexPath))
            {
                AdoptExistingMedia(records);
                SaveOwnershipRecords(records);
                return records;
            }

            string[] lines = File.ReadAllLines(indexPath, Encoding.UTF8);
            int lineIndex;
            for (lineIndex = 1; lineIndex < lines.Length; lineIndex++)
            {
                string[] fields = lines[lineIndex].Split('\t');
                if (fields.Length != 5)
                {
                    continue;
                }
                try
                {
                    string fileName = Encoding.UTF8.GetString(Convert.FromBase64String(fields[1]));
                    if (string.IsNullOrEmpty(fileName) || fileName != Path.GetFileName(fileName))
                    {
                        continue;
                    }
                    OwnershipRecord record = new OwnershipRecord();
                    record.Immune = string.Equals(fields[0], "I", StringComparison.Ordinal);
                    record.FileName = fileName;
                    record.Length = long.Parse(fields[2], CultureInfo.InvariantCulture);
                    record.CreationTimeUtcTicks = long.Parse(fields[3], CultureInfo.InvariantCulture);
                    record.LastWriteTimeUtcTicks = long.Parse(fields[4], CultureInfo.InvariantCulture);
                    records[fileName] = record;
                }
                catch (FormatException)
                {
                }
                catch (ArgumentException)
                {
                }
                catch (OverflowException)
                {
                }
            }
            return records;
        }

        private void AdoptExistingMedia(IDictionary<string, OwnershipRecord> records)
        {
            string[] candidates = Directory.GetFiles(
                directory,
                searchPattern,
                System.IO.SearchOption.TopDirectoryOnly);
            int candidateIndex;
            for (candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
            {
                string name = Path.GetFileName(candidates[candidateIndex]);
                if (migrationPredicate(name))
                {
                    records[name] = CreateRecord(candidates[candidateIndex]);
                }
            }
        }

        private static OwnershipRecord CreateRecord(string path)
        {
            FileInfo info = new FileInfo(path);
            OwnershipRecord record = new OwnershipRecord();
            record.FileName = info.Name;
            record.Length = info.Length;
            record.CreationTimeUtcTicks = info.CreationTimeUtc.Ticks;
            record.LastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks;
            record.Immune = false;
            return record;
        }

        private IList<string> ReconcileClearableFiles(
            IDictionary<string, OwnershipRecord> records,
            out bool changed)
        {
            List<string> result = new List<string>();
            changed = false;
            foreach (OwnershipRecord record in records.Values)
            {
                if (record.Immune)
                {
                    continue;
                }

                string candidate = Path.Combine(directory, record.FileName);
                if (!File.Exists(candidate) || !MatchesIdentity(candidate, record))
                {
                    record.Immune = true;
                    changed = true;
                    continue;
                }
                result.Add(candidate);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static bool MatchesIdentity(string path, OwnershipRecord record)
        {
            FileInfo info = new FileInfo(path);
            return info.Length == record.Length &&
                info.CreationTimeUtc.Ticks == record.CreationTimeUtcTicks &&
                info.LastWriteTimeUtc.Ticks == record.LastWriteTimeUtcTicks;
        }

        private static bool MarkImmune(
            IDictionary<string, OwnershipRecord> records,
            string fileName)
        {
            OwnershipRecord record;
            if (string.IsNullOrEmpty(fileName) ||
                !records.TryGetValue(fileName, out record) ||
                record.Immune)
            {
                return false;
            }
            record.Immune = true;
            return true;
        }

        private void SaveOwnershipRecords(IDictionary<string, OwnershipRecord> records)
        {
            string indexPath = Path.Combine(directory, indexName);
            string temporaryPath = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool indexExisted = File.Exists(indexPath);
            FileAttributes previousAttributes = indexExisted
                ? File.GetAttributes(indexPath)
                : FileAttributes.Normal;
            List<string> lines = new List<string>();
            lines.Add(indexHeader);
            foreach (OwnershipRecord record in records.Values)
            {
                lines.Add(string.Join(
                    "\t",
                    record.Immune ? "I" : "M",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(record.FileName)),
                    record.Length.ToString(CultureInfo.InvariantCulture),
                    record.CreationTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
                    record.LastWriteTimeUtcTicks.ToString(CultureInfo.InvariantCulture)));
            }

            try
            {
                File.WriteAllLines(temporaryPath, lines.ToArray(), Encoding.UTF8);
                if (indexExisted)
                {
                    File.SetAttributes(
                        indexPath,
                        previousAttributes & ~FileAttributes.Hidden & ~FileAttributes.ReadOnly);
                    try
                    {
                        File.Replace(temporaryPath, indexPath, null, true);
                    }
                    catch (IOException)
                    {
                        File.Copy(temporaryPath, indexPath, true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(temporaryPath, indexPath, true);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        File.Copy(temporaryPath, indexPath, true);
                    }
                }
                else
                {
                    File.Move(temporaryPath, indexPath);
                }

            }
            finally
            {
                FinishOwnershipSave(indexPath, temporaryPath, previousAttributes);
            }
        }

        internal static void FinishOwnershipSave(
            string indexPath,
            string temporaryPath,
            FileAttributes previousAttributes)
        {
            // Cloud sync and scanners can lock either file after the index was
            // saved. Housekeeping must not fail startup or mask a save error.
            try
            {
                if (File.Exists(indexPath))
                {
                    FileAttributes attributes = File.GetAttributes(indexPath);
                    File.SetAttributes(
                        indexPath,
                        attributes | FileAttributes.Hidden |
                        (previousAttributes & FileAttributes.ReadOnly));
                }
            }
            catch (IOException exception)
            {
                ErrorLog.Write("Could not restore ownership index attributes", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                ErrorLog.Write("Could not restore ownership index attributes", exception);
            }

            try
            {
                // Delete is already a no-op when Replace/Move consumed the file.
                File.Delete(temporaryPath);
            }
            catch (IOException exception)
            {
                ErrorLog.Write("Could not remove temporary ownership file; leaving it unused", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                ErrorLog.Write("Could not remove temporary ownership file; leaving it unused", exception);
            }
        }
    }
}
