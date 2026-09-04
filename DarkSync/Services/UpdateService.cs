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

    // Bump this whenever you publish a release — it's compared against GitHub release tags
    public const string CurrentVersion = "2.0.0";

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
    /// Checks GitHub Releases for a newer version.
    /// Returns (hasUpdate, versionTag, releaseName).
    /// </summary>
    public static async Task<(bool HasUpdate, string Version, string Message)> CheckForUpdateAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            var response = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);

            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var name = doc.RootElement.GetProperty("name").GetString() ?? tag;

            // Compare versions (e.g. "2.0.0" vs "2.1.0")
            if (Version.TryParse(CurrentVersion, out var current) &&
                Version.TryParse(tag.TrimStart('v', 'V'), out var latest) &&
                latest > current)
            {
                return (true, tag, name);
            }

            return (false, tag, "You are running the latest version.");
        }
        catch
        {
            return (false, "", "Unable to check for updates.");
        }
    }

    /// <summary>
    /// Downloads the release zip, extracts it, creates an updater script,
    /// and prepares for restart. Returns "OK" on success.
    /// </summary>
    public static async Task<string> DownloadAndUpdateAsync(IProgress<string>? progress = null)
    {
        try
        {
            progress?.Report("Checking for latest release...");

            // Get latest release info
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            var response = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);

            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var assets = doc.RootElement.GetProperty("assets");

            // Find the zip asset
            string? zipUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (zipUrl == null)
                return "No zip asset found in the latest release. Publish a release with a .zip file attached.";

            // Clean temp dir
            if (Directory.Exists(TempDir))
                Directory.Delete(TempDir, true);
            Directory.CreateDirectory(TempDir);

            progress?.Report($"Downloading {tag}...");
            var zipPath = Path.Combine(TempDir, "update.zip");

            using (var httpResponse = await Http.GetAsync(zipUrl))
            {
                httpResponse.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await httpResponse.Content.CopyToAsync(fs);
            }

            progress?.Report("Extracting update...");
            var extractDir = Path.Combine(TempDir, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // The zip may contain a top-level folder — find the one with DarkSync.exe
            var buildDir = extractDir;
            var exeInRoot = Path.Combine(extractDir, "DarkSync.exe");
            if (!File.Exists(exeInRoot))
            {
                var subDir = Directory.GetDirectories(extractDir).FirstOrDefault();
                if (subDir != null && File.Exists(Path.Combine(subDir, "DarkSync.exe")))
                    buildDir = subDir;
                else
                    return "Could not find DarkSync.exe in the downloaded zip.";
            }

            progress?.Report("Preparing updater...");

            // Get current app paths
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var currentDir = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;

            // Create updater batch script
            var bat = $"""
                @echo off
                timeout /t 2 /nobreak >nul
                xcopy /y /e /q "{buildDir}\*" "{currentDir}\"
                start "" "{currentExe}"
                del "%~f0"
                """;

            File.WriteAllText(UpdaterBatPath, bat);

            progress?.Report("Update ready! Restarting...");
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
}
