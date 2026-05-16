namespace Kaeo.LlmProxy;

internal static class Program
{
    private static readonly string _appIconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        Application.Run(new TrayApplicationContext());
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