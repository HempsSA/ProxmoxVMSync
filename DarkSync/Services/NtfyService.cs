using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using DarkSync.Models;

namespace DarkSync.Services;

public static class NtfyService
{
    private const string AppName = "DarkSync Proxmox Archive";
    private const string AppVersion = "2.0.0";

    private static readonly HttpClient Http = new();

    public static async Task SendAsync(Config config, string title, string message, bool success, CancellationToken ct = default)
    {
        if (!config.NtfyEnabled) return;
        if (success && !config.NtfyOnSuccess) return;
        if (!success && !config.NtfyOnFailure) return;

        var server = (config.NtfyServer ?? "https://ntfy.sh").Trim().TrimEnd('/');
        var topic = config.NtfyTopic.Trim();
        if (string.IsNullOrEmpty(topic))
            throw new ArgumentException("ntfy topic is required");

        var url = $"{server}/{Uri.EscapeDataString(topic)}";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(message, Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("Title", title);
        request.Headers.Add("Priority", config.NtfyPriority ?? "high");
        request.Headers.Add("Tags", success ? "white_check_mark" : "warning");
        request.Headers.UserAgent.ParseAdd($"{AppName}/{AppVersion}");

        if (!string.IsNullOrWhiteSpace(config.NtfyToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.NtfyToken.Trim());

        var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
