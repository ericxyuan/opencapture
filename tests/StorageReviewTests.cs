using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenCapture.Tests
{
    internal static class StorageReviewTests
    {
        internal static void Run()
        {
            TestReservedCopiedTextNames();
            foreach (string extension in new string[] { ".png", ".avi" })
            {
                TestIdentityChangesDuringClear(extension);
                TestRenameRetirement(extension);
                TestRecycleFailure(extension);
                TestOverflowOwnershipRows(extension);
            }
            Console.WriteLine("STORAGE_REVIEW_PASS");
        }

        private static void TestReservedCopiedTextNames()
        {
            Require(CopiedTextFileStore.MakeSafeBaseName("CON") == "CON text",
                "plain device names remain safe");
            foreach (string text in new string[]
                { "CON.txt", "NUL.jpeg", "COM1.log", "lpt9.backup", "COM\u00b9.log", "LPT\u00b2.txt", "COM\u00b3.tmp" })
            {
                int period = text.IndexOf('.');
                Require(CopiedTextFileStore.MakeSafeBaseName(text) == text.Insert(period, " text"),
                    "device names are changed before their extension: " + text);
            }
            Require(CopiedTextFileStore.MakeSafeBaseName("CON .txt") == "CON  text.txt",
                "a device name with a trailing space before the extension is safe");
            string bounded = CopiedTextFileStore.MakeSafeBaseName("CON." + new string('x', 200));
            Require(bounded.Length <= 140 && bounded.StartsWith("CON text.", StringComparison.Ordinal),
                "making a long reserved name safe retains the length limit");
            Require(CopiedTextFileStore.MakeSafeBaseName(new string('a', 140) + " \r\nignored") ==
                new string('a', 140), "long OCR names stop at the existing filename limit");

            string directory = NewDirectory();
            try
            {
                string path = CopiedTextFileStore.GetUniquePath(directory, "CON.txt");
                File.WriteAllText(path, "test");
                Require(File.Exists(path) && Path.GetFileName(path) == "CON text.txt.png",
                    "reserved OCR text produces a writable Windows path");
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        private static void TestIdentityChangesDuringClear(string extension)
        {
            string directory = NewDirectory();
            try
            {
                ManagedMediaFileStore store = NewStore(directory, extension);
                string first = Path.Combine(directory, "01" + extension);
                string later = Path.Combine(directory, "02" + extension);
                File.WriteAllText(first, "first capture");
                File.WriteAllText(later, "later capture");
                store.Register(first);
                store.Register(later);
                byte[] original = File.ReadAllBytes(later);
                DateTime originalCreation = File.GetCreationTimeUtc(later);
                DateTime originalWrite = File.GetLastWriteTimeUtc(later);
                int recycleCalls = 0;
                int deleted = store.ClearToRecycleBin(delegate(string path)
                {
                    recycleCalls++;
                    Require(string.Equals(path, first, StringComparison.OrdinalIgnoreCase),
                        "a later edited file is never passed to recycling " + extension);
                    File.Delete(path);
                    File.AppendAllText(later, " user edit while clearing");
                });
                Require(deleted == 1 && recycleCalls == 1 && File.Exists(later),
                    "clearing rechecks later files after a slow deletion " + extension);

                File.WriteAllBytes(later, original);
                File.SetCreationTimeUtc(later, originalCreation);
                File.SetLastWriteTimeUtc(later, originalWrite);
                Require(NewStore(directory, extension).GetClearableFiles().Count == 0,
                    "an edited file stays retired after its original identity is restored " + extension);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        private static void TestRenameRetirement(string extension)
        {
            string directory = NewDirectory();
            try
            {
                ManagedMediaFileStore store = NewStore(directory, extension);
                string original = Path.Combine(directory, "capture" + extension);
                string renamed = Path.Combine(directory, "keep" + extension);
                File.WriteAllText(original, "capture");
                store.Register(original);
                File.Move(original, renamed);
                store.MarkRenamed(original, renamed);
                File.Move(renamed, original);
                int calls = 0;
                int deleted = NewStore(directory, extension).ClearToRecycleBin(delegate(string path)
                {
                    calls++;
                    File.Delete(path);
                });
                Require(deleted == 0 && calls == 0 && File.Exists(original),
                    "rename retirement persists after restoring the original name " + extension);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        private static void TestRecycleFailure(string extension)
        {
            string directory = NewDirectory();
            try
            {
                ManagedMediaFileStore store = NewStore(directory, extension);
                string first = Path.Combine(directory, "01" + extension);
                string second = Path.Combine(directory, "02" + extension);
                File.WriteAllText(first, "first");
                File.WriteAllText(second, "second");
                store.Register(first);
                store.Register(second);
                bool failed = false;
                try
                {
                    store.ClearToRecycleBin(delegate(string path)
                    {
                        if (path == second)
                        {
                            throw new IOException("Simulated recycle failure.");
                        }
                        File.Delete(path);
                    });
                }
                catch (IOException)
                {
                    failed = true;
                }
                IList<string> remaining = NewStore(directory, extension).GetClearableFiles();
                Require(failed && !File.Exists(first) && File.Exists(second) &&
                    remaining.Count == 1 && remaining[0] == second,
                    "recycle failures surface and completed progress remains durable " + extension);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        private static void TestOverflowOwnershipRows(string extension)
        {
            string directory = NewDirectory();
            try
            {
                ManagedMediaFileStore store = NewStore(directory, extension);
                string path = Path.Combine(directory, "valid" + extension);
                File.WriteAllText(path, "valid capture");
                store.Register(path);
                string indexPath = Path.Combine(directory, ".ownership");
                File.SetAttributes(indexPath, FileAttributes.Normal);
                string encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes("overflow" + extension));
                File.AppendAllText(indexPath,
                    "M\t" + encodedName + "\t9223372036854775808\t0\t0\r\n" +
                    "M\t" + encodedName + "\t0\t9223372036854775808\t0\r\n" +
                    "M\t" + encodedName + "\t0\t0\t9223372036854775808\r\n", Encoding.UTF8);
                IList<string> files = store.GetClearableFiles();
                Require(files.Count == 1 && files[0] == path,
                    "overflowing identity rows are skipped beside a valid record " + extension);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        private static ManagedMediaFileStore NewStore(string directory, string extension)
        {
            ManagedMediaFileStore store = new ManagedMediaFileStore(
                directory, ".ownership", "test ownership", "*" + extension,
                delegate(string name) { return true; });
            store.GetClearableFiles();
            return store;
        }

        private static string NewDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                "OpenCapture-storage-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static void DeleteDirectory(string directory)
        {
            foreach (string path in Directory.GetFiles(directory))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            Directory.Delete(directory, true);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("FAIL: " + message);
            }
        }
    }
}
