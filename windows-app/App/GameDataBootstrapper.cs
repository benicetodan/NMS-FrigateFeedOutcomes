using System.Diagnostics;
using System.IO;

namespace NmsFrigateFeedOutcomes.App;

internal static class GameDataBootstrapper
{
    private static readonly string[] RequiredFiles =
    [
        Path.Combine("data", "nms_reality_gcproducttable.MXML"),
        Path.Combine("assets", "metadata", "reality", "tables", "frigatetraittable.MXML"),
        Path.Combine("assets", "gcfleetglobals.global.MXML"),
        Path.Combine("windows-app", "Core", "Data", "localization-en.json")
    ];

    public static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NMS-FrigateFeedOutcomes",
        "GameData");

    public static async Task<string> EnsurePreparedAsync()
    {
        if (HasRequiredFiles())
        {
            return CacheRoot;
        }

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Prepare-GameData.ps1");
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("The game-data setup script is missing next to the application.", scriptPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-OutputRoot");
        startInfo.ArgumentList.Add(CacheRoot);
        startInfo.ArgumentList.Add("-Force");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start the local game-data setup process.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                ? $"Game-data setup failed with exit code {process.ExitCode}."
                : details);
        }

        if (!HasRequiredFiles())
        {
            throw new InvalidDataException("Game-data setup completed without producing all required local files.");
        }

        return CacheRoot;
    }

    private static bool HasRequiredFiles() =>
        RequiredFiles.All(relativePath => File.Exists(Path.Combine(CacheRoot, relativePath)));
}