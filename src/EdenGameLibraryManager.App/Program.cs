using Avalonia;
using EdenGameLibraryManager.Infrastructure.Logging;
using EdenGameLibraryManager.Infrastructure.Storage;

namespace EdenGameLibraryManager.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Last-resort crash log; normal logging is configured in App.
            try
            {
                var paths = new AppPaths();
                Directory.CreateDirectory(paths.LogsDirectory);
                File.AppendAllText(Path.Combine(paths.LogsDirectory, "crash.log"), $"{DateTimeOffset.Now:O} {ex}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            throw;
        }
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
