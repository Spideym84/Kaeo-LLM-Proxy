namespace Kaeo.LlmProxy;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        Application.Run(new TrayApplicationContext());
    }
}