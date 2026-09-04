using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace DarkSync.Services;

public static class UpdateService
{
    private const string RepoOwner = "HempsSA";
    private const string RepoName = "ProxmoxVMSync";
    private const string Branch = "main";

    private static readonly HttpClient Http = new()
    {
        DefaultRequestHeaders =
        {
            { "User-Agent", "DarkSync-Updater" }
        }
    };

    private static string UpdaterBatPath =>
        Path.Combine(AppContext.BaseDirectory, "darksync_updater.bat");

    private static string TempDir =>
        Path.Combine(Path.GetTempPath(), "DarkSync_Update");

    /// <summary>
    /// Checks GitHub for the latest commit on main.
    /// Returns (hasUpdate, commitSha, commitMessage).
    /// </summary>
    public static async Task<(bool HasUpdate, string Sha, string Message)> CheckForUpdateAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/commits/{Branch}";
            var response = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);

            var sha = doc.RootElement.GetProperty("sha").GetString()?[..7] ?? "";
            var msg = doc.RootElement.GetProperty("commit")
                .GetProperty("message").GetString() ?? "";

            // Get current local commit
            var localSha = GetLocalCommitSha();

            if (string.IsNullOrEmpty(localSha))
                return (true, sha, msg); // Can't determine local version, assume update needed

            return (localSha != sha, sha, msg);
        }
        catch
        {
            return (false, "", "Unable to check for updates");
        }
    }

    /// <summary>
    /// Downloads the latest source, builds it, creates an updater script,
    /// and prepares for restart. Returns a status message.
    /// </summary>
    public static async Task<string> DownloadAndBuildAsync(IProgress<string>? progress = null)
    {
        try
        {
            progress?.Report("Downloading latest source from GitHub...");

            // Clean temp dir
            if (Directory.Exists(TempDir))
                Directory.Delete(TempDir, true);
            Directory.CreateDirectory(TempDir);

            // Download source zip
            var zipPath = Path.Combine(TempDir, "source.zip");
            var zipUrl = $"https://github.com/{RepoOwner}/{RepoName}/archive/refs/heads/{Branch}.zip";

            using (var response = await Http.GetAsync(zipUrl))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await response.Content.CopyToAsync(fs);
            }

            progress?.Report("Extracting source...");
            ZipFile.ExtractToDirectory(zipPath, TempDir);

            // Find the extracted folder (name pattern: RepoName-Branch)
            var extractedDir = Directory.GetDirectories(TempDir)
                .FirstOrDefault(d => Path.GetFileName(d).StartsWith(RepoName, StringComparison.OrdinalIgnoreCase));

            if (extractedDir == null)
                return "Failed to find extracted source directory";

            progress?.Report("Building update...");
            var buildDir = Path.Combine(TempDir, "build");

            var buildResult = await RunProcessAsync("dotnet",
                $"build \"{extractedDir}\" -c Release -o \"{buildDir}\" --nologo -v q");

            if (buildResult.ExitCode != 0)
                return $"Build failed:\n{buildResult.Output}";

            progress?.Report("Creating updater script...");

            // Get paths
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var currentDir = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;
            var newExe = Path.Combine(buildDir, "DarkSync.exe");

            // Create updater batch script
            var bat = $"""
                @echo off
                timeout /t 2 /nobreak >nul
                xcopy /y /e /q "{buildDir}\*" "{currentDir}\"
                start "" "{currentExe}"
                del "%~f0"
                """;

            File.WriteAllText(UpdaterBatPath, bat);

            progress?.Report("Update ready!");
            return "OK";
        }
        catch (Exception ex)
        {
            return $"Update failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Launches the updater script and exits the application.
    /// </summary>
    public static void LaunchUpdaterAndExit()
    {
        if (File.Exists(UpdaterBatPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = UpdaterBatPath,
                UseShellExecute = true,
                CreateNoWindow = true
            });
        }

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            System.Windows.Application.Current.Shutdown();
        });
    }

    private static string? GetLocalCommitSha()
    {
        try
        {
            var gitDir = FindGitRoot(AppContext.BaseDirectory);
            if (gitDir == null) return null;

            var headFile = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headFile)) return null;

            var head = File.ReadAllText(headFile).Trim();
            if (head.StartsWith("ref: "))
            {
                var refPath = Path.Combine(gitDir, head[5..]);
                if (File.Exists(refPath))
                    return File.ReadAllText(refPath).Trim()[..7];
            }

            return head[..7];
        }
        catch
        {
            return null;
        }
    }

    private static string? FindGitRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return Path.Combine(dir.FullName, ".git");
            dir = dir.Parent;
        }
        return null;
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, output + "\n" + error);
    }
}
