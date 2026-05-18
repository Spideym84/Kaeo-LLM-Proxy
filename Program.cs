using Kaeo.LlmProxy.Core.Models;

namespace Kaeo.LlmProxy;

internal static class Program
{
    private const string MutexName = "Global\\Kaeo.LlmProxy.SingleInstance";
    private static readonly string _appIconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);

        AppSettings settings = AppSettings.Load();

        if (!settings.AllowMultipleInstances)
        {
            Mutex mutex = new(initiallyOwned: true, MutexName, out bool createdNew);

            if (!createdNew)
            {
                MessageBox.Show(
                    "Kaeo LLM Proxy is already running.\n\n" +
                    "Only one instance is allowed at a time. Check the system tray for the existing instance.\n\n" +
                    "To run multiple instances simultaneously, set \"AllowMultipleInstances\": true in settings.jsonc.",
                    "Already Running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            // Keep the mutex alive for the lifetime of the process.
            GC.KeepAlive(mutex);
        }

        Application.Run(new TrayApplicationContext(settings));
    }

    internal static Icon GetApplicationIcon()
    {
        if (!File.Exists(_appIconPath))
            return SystemIcons.Application;

        try
        {
            return new Icon(_appIconPath);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }
}