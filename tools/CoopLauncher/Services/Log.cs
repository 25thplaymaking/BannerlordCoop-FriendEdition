using System.IO;

namespace CoopLauncher.Services;

/// <summary>
/// Tiny append-only launcher log so a friend whose game won't start can send one file back. Lives
/// beside the app data, never throws, and never records the server password.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CalradiaCoop", "launcher.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never break the launch.
        }
    }

    /// <summary>Start a fresh log for this run.</summary>
    public static void Begin()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  Calradia Co-op launcher started{Environment.NewLine}");
            }
        }
        catch { }
    }
}
