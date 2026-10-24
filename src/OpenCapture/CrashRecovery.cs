using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace OpenCapture
{
    /// <summary>
    /// Restarts the same standalone executable after a terminating background
    /// exception. No resident watchdog is used: a replacement process exists
    /// only after the CLR has already declared the current process terminal.
    /// </summary>
    internal static class CrashRecovery
    {
        private const string RecoverAfterPrefix = "--recover-after=";
        private const string RecoveryDepthPrefix = "--recovery-depth=";
        private const int MaximumRapidRecoveryDepth = 3;
        private const int ParentExitWaitMilliseconds = 30000;
        private const int StableRunMilliseconds = 60000;

        private static int recoveryDepth;
        private static int launchGate;
        private static int suppressed;
        private static Timer stableTimer;

        internal static bool RecoveryRequested { get; private set; }

        internal static bool Prepare(string[] args)
        {
            int parentProcessId;
            int parsedDepth;
            RecoveryRequested = TryParseRecoveryArguments(
                args,
                out parentProcessId,
                out parsedDepth);
            recoveryDepth = Math.Max(0, parsedDepth);
            if (RecoveryRequested && parentProcessId > 0)
            {
                WaitForParentExit(parentProcessId);
            }
            return RecoveryRequested;
        }

        internal static void ArmStableRunReset()
        {
            Timer previous = Interlocked.Exchange(
                ref stableTimer,
                new Timer(
                    delegate
                    {
                        Interlocked.Exchange(ref recoveryDepth, 0);
                        Timer completed = Interlocked.Exchange(ref stableTimer, null);
                        if (completed != null)
                        {
                            completed.Dispose();
                        }
                    },
                    null,
                    StableRunMilliseconds,
                    Timeout.Infinite));
            if (previous != null)
            {
                previous.Dispose();
            }
        }

        internal static void Suppress()
        {
            Interlocked.Exchange(ref suppressed, 1);
        }

        internal static bool TryLaunchAfterFatalError()
        {
            int depth = Volatile.Read(ref recoveryDepth);
            if (!IsRestartAllowed(depth, Volatile.Read(ref suppressed) != 0) ||
                Interlocked.Exchange(ref launchGate, 1) != 0)
            {
                return false;
            }

            try
            {
                string executable = Path.Combine(
                    AppContext.BaseDirectory,
                    "OpenCapture.exe");
                if (!File.Exists(executable))
                {
                    return false;
                }

                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = executable;
                start.Arguments = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}{1} {2}{3}",
                    RecoverAfterPrefix,
                    Process.GetCurrentProcess().Id,
                    RecoveryDepthPrefix,
                    depth + 1);
                start.WorkingDirectory = AppContext.BaseDirectory;
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                return Process.Start(start) != null;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("Could not start crash recovery", exception);
                return false;
            }
        }

        internal static bool TryParseRecoveryArguments(
            string[] args,
            out int parentProcessId,
            out int depth)
        {
            parentProcessId = 0;
            depth = 0;
            if (args == null)
            {
                return false;
            }

            int index;
            for (index = 0; index < args.Length; index++)
            {
                string argument = args[index] ?? string.Empty;
                if (argument.StartsWith(
                    RecoverAfterPrefix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(
                        argument.Substring(RecoverAfterPrefix.Length),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out parentProcessId);
                }
                else if (argument.StartsWith(
                    RecoveryDepthPrefix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(
                        argument.Substring(RecoveryDepthPrefix.Length),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out depth);
                }
            }
            return parentProcessId > 0;
        }

        internal static bool IsRestartAllowed(int depth, bool isSuppressed)
        {
            return !isSuppressed && depth >= 0 && depth < MaximumRapidRecoveryDepth;
        }

        private static void WaitForParentExit(int parentProcessId)
        {
            if (parentProcessId == Process.GetCurrentProcess().Id)
            {
                return;
            }

            try
            {
                using (Process parent = Process.GetProcessById(parentProcessId))
                {
                    parent.WaitForExit(ParentExitWaitMilliseconds);
                }
            }
            catch (ArgumentException)
            {
                // The failed process has already exited.
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
