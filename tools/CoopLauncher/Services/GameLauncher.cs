using System.Diagnostics;
using System.IO;

namespace CoopLauncher.Services;

/// <summary>Starts Bannerlord with the co-op module set and the one-click direct-join argument.</summary>
public static class GameLauncher
{
    /// <summary>
    /// Launches the resolved <c>Bannerlord.exe</c> with <c>/singleplayer &lt;token&gt;</c> and
    /// <c>/coopjoin &lt;host&gt; &lt;port&gt; &lt;password&gt;</c>. The mod reads /coopjoin at the main
    /// menu and fires the join automatically — the same path as clicking Join by hand.
    /// </summary>
    public static void Launch(string bannerlordExe, LauncherConfig config)
    {
        var workingDir = Path.GetDirectoryName(bannerlordExe)!;

        var psi = new ProcessStartInfo
        {
            FileName = bannerlordExe,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
        };

        // ArgumentList quotes each element correctly, so a token with spaces or an odd password
        // can't split into stray arguments.
        psi.ArgumentList.Add("/singleplayer");
        psi.ArgumentList.Add(config.ModuleToken);
        psi.ArgumentList.Add("/coopjoin");
        psi.ArgumentList.Add(config.ServerHost);
        psi.ArgumentList.Add(config.ServerPort.ToString());
        if (!string.IsNullOrEmpty(config.ServerPassword))
            psi.ArgumentList.Add(config.ServerPassword);

        Process.Start(psi);
    }
}
