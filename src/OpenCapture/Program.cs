using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace OpenCapture
{
    internal static class Program
    {
        private const string SingleInstanceName = "Local\\OpenCapture-9C3931F2-37D5-45A7-A177-9C6FE9264580";

        [STAThread]
        private static int Main(string[] args)
        {
            CrashRecovery.Prepare(args);
            NativeMethods.TryEnablePerMonitorV2DpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            bool ownsMutex;
            using (Mutex mutex = new Mutex(true, SingleInstanceName, out ownsMutex))
            {
                if (!ownsMutex)
                {
                    MessageBox.Show(
                        "OpenCapture is already running in the notification area.",
                        "OpenCapture",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return 0;
                }

                Application.ThreadException += OnThreadException;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                try
                {
                    using (OpenCaptureApplication application = new OpenCaptureApplication(args))
                    {
                        CrashRecovery.ArmStableRunReset();
                        Application.Run(application);
                    }
                    return 0;
                }
                catch (Exception exception)
                {
                    ErrorLog.Write("Fatal startup error", exception);
                    MessageBox.Show(
                        "OpenCapture could not start.\r\n\r\n" + exception.Message,
                        "OpenCapture",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return 1;
                }
                finally
                {
                    Application.ThreadException -= OnThreadException;
                    AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                }
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs eventArgs)
        {
            ErrorLog.Write("Unhandled user-interface error", eventArgs.Exception);
            MessageBox.Show(
                "OpenCapture hit an unexpected error but will remain available in the notification area.\r\n\r\n" +
                eventArgs.Exception.Message,
                "OpenCapture",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
        {
            Exception exception = eventArgs.ExceptionObject as Exception;
            ErrorLog.Write("Unhandled background error", exception ?? new Exception("Unknown background error."));
            if (eventArgs.IsTerminating)
            {
                CrashRecovery.TryLaunchAfterFatalError();
            }
        }
    }
}
