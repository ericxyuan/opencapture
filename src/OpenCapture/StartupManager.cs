using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace OpenCapture
{
    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "OpenCapture";
        private const string SettingsKeyPath = @"Software\OpenCapture";
        private const string InitializedValueName = "AutostartChoiceInitialized";

        internal static string ExecutablePath
        {
            get
            {
                string path = Assembly.GetEntryAssembly().Location;
                return Path.GetFullPath(path);
            }
        }

        internal static string CommandLine
        {
            get { return "\"" + ExecutablePath + "\" --startup"; }
        }

        internal static bool IsEnabled
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                    {
                        string value = key == null ? null : key.GetValue(ValueName) as string;
                        return string.Equals(value, CommandLine, StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        internal static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Windows would not open the current-user startup list.");
                }

                if (enabled)
                {
                    key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
            MarkChoiceInitialized();
        }

        internal static void EnsureDefaultEnabled()
        {
            bool initialized = false;
            try
            {
                using (RegistryKey settings = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, false))
                {
                    object value = settings == null ? null : settings.GetValue(InitializedValueName);
                    initialized = value is int && (int)value != 0;
                }
            }
            catch
            {
            }

            if (initialized)
            {
                return;
            }

            if (!IsEnabled)
            {
                SetEnabled(true);
            }
            else
            {
                MarkChoiceInitialized();
            }
        }

        private static void MarkChoiceInitialized()
        {
            using (RegistryKey settings = Registry.CurrentUser.CreateSubKey(SettingsKeyPath))
            {
                if (settings != null)
                {
                    settings.SetValue(InitializedValueName, 1, RegistryValueKind.DWord);
                }
            }
        }
    }
}
