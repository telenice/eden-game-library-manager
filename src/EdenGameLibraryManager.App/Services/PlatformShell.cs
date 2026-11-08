using System.Diagnostics;

namespace EdenGameLibraryManager.App.Services;

/// <summary>
/// Reveals files in the OS file manager. Game files themselves are never opened or executed:
/// only the containing folder is shown (with the file selected where the platform supports it).
/// </summary>
public static class PlatformShell
{
    public static void RevealInFileManager(string path)
    {
        var full = Path.GetFullPath(path);
        var folder = File.Exists(full) ? Path.GetDirectoryName(full)! : full;

        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            if (File.Exists(full)) psi.ArgumentList.Add($"/select,{full}");
            else psi.ArgumentList.Add(folder);
            Process.Start(psi)?.Dispose();
        }
        else if (OperatingSystem.IsMacOS())
        {
            var psi = new ProcessStartInfo("open") { UseShellExecute = false };
            if (File.Exists(full)) psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add(File.Exists(full) ? full : folder);
            Process.Start(psi)?.Dispose();
        }
        else
        {
            // xdg-open on a directory opens the user's file manager; it is never given the game file.
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(folder);
            Process.Start(psi)?.Dispose();
        }
    }
}
