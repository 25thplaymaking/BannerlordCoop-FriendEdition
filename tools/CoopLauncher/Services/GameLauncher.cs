using System.Diagnostics;
using System.IO;

namespace CoopLauncher.Services;

/// <summary>Starts Bannerlord with the co-op module set and the one-click direct-join argument.</summary>
public static class GameLauncher
{
    private const string BannerlordAppId = "261550";

    /// <summary>
    /// Launches the resolved <c>Bannerlord.exe</c> with <c>/singleplayer &lt;token&gt;</c> and
    /// <c>/coopjoin &lt;host&gt; &lt;port&gt; &lt;password&gt;</c>. Returns the started process so the caller
    /// can notice an instant exit (a crash or a failed Steam init) instead of silently doing nothing.
    /// </summary>
    public static Process Launch(string bannerlordExe, LauncherConfig config, string enteredPassword)
    {
        var workingDir = Path.GetDirectoryName(bannerlordExe)!;

        // Launching the exe directly (not through Steam) needs steam_appid.txt beside it, or
        // SteamAPI_Init fails and the game closes instantly — the usual "nothing happens" on a fresh
        // install. Writing it is harmless when it's already there.
        EnsureSteamAppId(workingDir);

        var psi = CreateStartInfo(bannerlordExe, config, enteredPassword);

        // Log the command without the password.
        Log.Write($"Launching: {bannerlordExe}");
        Log.Write($"  workdir: {workingDir}");
        Log.Write($"  args: /singleplayer <token> /coopjoin {config.ServerHost} {config.ServerPort} <pw>");

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned no process");
        Log.Write($"Started Bannerlord pid {process.Id}");
        return process;
    }

    internal static ProcessStartInfo CreateStartInfo(
        string bannerlordExe,
        LauncherConfig config,
        string enteredPassword)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        string? blockedModule = config.GetBlockedModuleInToken();
        if (blockedModule is not null)
            throw new InvalidOperationException(
                $"The launch token contains held module '{blockedModule}'. {config.CompatibilityHoldNotice}");

        var psi = new ProcessStartInfo
        {
            FileName = bannerlordExe,
            WorkingDirectory = Path.GetDirectoryName(bannerlordExe)!,
            UseShellExecute = false,
        };
        // The game-side crash collector inherits this path and can reopen the launcher only
        // after an unexpected exit. No crash data leaves the machine until the player confirms.
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            psi.Environment["COOP_LAUNCHER_PATH"] = Environment.ProcessPath;

        // ArgumentList quotes each element correctly, so a token with spaces or an odd password
        // can't split into stray arguments.
        psi.ArgumentList.Add("/singleplayer");
        psi.ArgumentList.Add(config.ModuleToken);
        psi.ArgumentList.Add("/coopjoin");
        psi.ArgumentList.Add(config.ServerHost);
        psi.ArgumentList.Add(config.ServerPort.ToString());
        if (!string.IsNullOrEmpty(enteredPassword))
            psi.ArgumentList.Add(enteredPassword);
        return psi;
    }

    private static void EnsureSteamAppId(string binDir)
    {
        try
        {
            var path = Path.Combine(binDir, "steam_appid.txt");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, BannerlordAppId);
                Log.Write($"Wrote missing steam_appid.txt ({BannerlordAppId}) to {binDir}");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not ensure steam_appid.txt: {ex.Message}");
        }
    }

    /// <summary>Is the Steam client running? The game's Steam init needs it.</summary>
    public static bool IsSteamRunning()
    {
        try { return Process.GetProcessesByName("steam").Length > 0; }
        catch { return false; }
    }
}
