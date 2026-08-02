using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace AmbilightEngine.Services
{
    public sealed class StartupRegistrationService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppValueName = "AmbilightEngine";

        public void Apply(bool enabled, bool startMinimizedToTray)
        {
            try
            {
                using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (runKey == null)
                {
                    return;
                }

                if (!enabled)
                {
                    if (runKey.GetValue(AppValueName) != null)
                    {
                        runKey.DeleteValue(AppValueName, throwOnMissingValue: false);
                    }

                    return;
                }

                string executablePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    return;
                }

                string arguments = startMinimizedToTray ? " --tray" : string.Empty;
                string value = $"\"{executablePath}\"{arguments}";

                runKey.SetValue(AppValueName, value, RegistryValueKind.String);
            }
            catch
            {
            }
        }
    }
}