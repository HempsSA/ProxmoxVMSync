using System.IO;
using System.Text.Json;
using DarkSync.Models;

namespace DarkSync.Services;

public static class ConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string ConfigFileName = "darksync_proxmox.json";

    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DarkSync");

    /// <summary>
    /// Canonical config location. Previously the JSON lived next to the exe, which meant
    /// dev and published copies each had their own diverging settings file.
    /// </summary>
    public static string ConfigPath => Path.Combine(AppDataDir, ConfigFileName);

    private static string LegacyConfigPath()
    {
        try { return Path.Combine(AppContext.BaseDirectory, ConfigFileName); }
        catch { return ConfigFileName; }
    }

    public static Config Load()
    {
        var path = ConfigPath;

        // One-time migration from the legacy exe-adjacent location.
        try
        {
            if (!File.Exists(path))
            {
                var legacy = LegacyConfigPath();
                if (!string.Equals(legacy, path, StringComparison.OrdinalIgnoreCase) && File.Exists(legacy))
                {
                    Directory.CreateDirectory(AppDataDir);
                    File.Copy(legacy, path, overwrite: false);
                }
            }
        }
        catch { }

        if (!File.Exists(path))
            return new Config();

        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);
            return ParseConfig(doc.RootElement);
        }
        catch
        {
            // A corrupt file must never silently reset the user to a blank config
            // without a trace: back it up so settings can be recovered.
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.Copy(path, path + $".corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.bak", overwrite: false);
            }
            catch { }
            return new Config();
        }
    }

    private static string GetString(JsonElement d, string name, string @default = "")
    {
        try
        {
            if (d.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString() ?? @default;
        }
        catch { }
        return @default;
    }

    private static int GetInt(JsonElement d, string name, int @default)
    {
        try
        {
            if (!d.TryGetProperty(name, out var e)) return @default;
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)) return n;
            if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var s)) return s;
        }
        catch { }
        return @default;
    }

    private static int GetInt(JsonElement d, string name1, string name2, int @default)
    {
        if (d.TryGetProperty(name1, out _) || d.TryGetProperty(name2, out _))
        {
            var v1 = GetInt(d, name1, int.MinValue);
            if (v1 != int.MinValue) return v1;
            return GetInt(d, name2, @default);
        }
        return @default;
    }

    private static bool GetBool(JsonElement d, string name, bool @default)
    {
        try
        {
            if (!d.TryGetProperty(name, out var e)) return @default;
            return e.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => e.GetInt32() != 0,
                JsonValueKind.String => bool.TryParse(e.GetString(), out var b) ? b : @default,
                _ => @default,
            };
        }
        catch { return @default; }
    }

    private static Config ParseConfig(JsonElement d)
    {
        var c = new Config();

        if (d.TryGetProperty("sources", out var srcArr) && srcArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in srcArr.EnumerateArray())
            {
                try
                {
                    c.Sources.Add(new Source
                    {
                        Name = GetString(s, "name", "PVE"),
                        Path = GetString(s, "path"),
                        // Missing flag means enabled (old files predate the flag).
                        Enabled = GetBool(s, "enabled", true),
                        KeyFile = GetString(s, "key_file")
                    });
                }
                catch { }
            }
        }

        c.Destination = GetString(d, "destination");
        c.ArchiveId = GetString(d, "archive_id");
        if (string.IsNullOrEmpty(c.ArchiveId))
            c.ArchiveId = GetString(d, "expected_archive_id");

        JsonElement vmArr = default;
        var hasVms = false;
        if (d.TryGetProperty("vms", out var v1) && v1.ValueKind == JsonValueKind.Array) { vmArr = v1; hasVms = true; }
        else if (d.TryGetProperty("protected_vms", out var v2) && v2.ValueKind == JsonValueKind.Array) { vmArr = v2; hasVms = true; }

        if (hasVms)
        {
            foreach (var v in vmArr.EnumerateArray())
            {
                try
                {
                    var vm = new VmPolicy
                    {
                        Enabled = GetBool(v, "enabled", true)
                    };
                    if (v.TryGetProperty("vmid", out var vid) && vid.ValueKind == JsonValueKind.Number && vid.TryGetInt32(out var id))
                        vm.VmId = id;
                    vm.Name = GetString(v, "name");
                    vm.Importance = GetInt(v, "importance", 1);
                    vm.Copies = GetInt(v, "copies", "required_copies", 1);
                    vm.MaxAge = GetInt(v, "max_age", "maximum_age_days", 7);
                    c.Vms.Add(vm);
                }
                catch { }
            }
        }

        c.MinFreeGb = GetInt(d, "min_free_gb", "minimum_free_gb", 5);
        var retention = GetString(d, "retention", "Keep all");
        c.Retention = string.IsNullOrEmpty(retention) ? "Keep all" : retention;
        c.RecycleDays = GetInt(d, "recycle_days", 7);
        c.NtfyEnabled = GetBool(d, "ntfy_enabled", false);
        c.NtfyServer = GetString(d, "ntfy_server", "https://ntfy.sh");
        if (string.IsNullOrEmpty(c.NtfyServer)) c.NtfyServer = "https://ntfy.sh";
        c.NtfyTopic = GetString(d, "ntfy_topic");
        c.NtfyToken = GetString(d, "ntfy_token");
        c.NtfyPriority = GetString(d, "ntfy_priority", "high");
        if (string.IsNullOrEmpty(c.NtfyPriority)) c.NtfyPriority = "high";
        c.NtfyOnSuccess = GetBool(d, "ntfy_on_success", true);
        c.NtfyOnFailure = GetBool(d, "ntfy_on_failure", true);
        c.ScheduleEnabled = GetBool(d, "schedule_enabled", false);
        c.ScheduleTime = GetString(d, "schedule_time", "02:00");
        if (string.IsNullOrEmpty(c.ScheduleTime)) c.ScheduleTime = "02:00";
        c.ScheduleDryRun = GetBool(d, "schedule_dry_run", false);
        c.ScheduleLastRunDate = GetString(d, "schedule_last_run_date");
        c.ScheduleLastAttempt = GetString(d, "schedule_last_attempt");
        c.ScheduleConsecutiveFailures = GetInt(d, "schedule_consecutive_failures", 0);
        c.ScheduleLastResult = GetString(d, "schedule_last_result");
        c.ScheduleLastAbortNotifyDate = GetString(d, "schedule_last_abort_notify_date");

        return c;
    }

    public static void Save(Config c)
    {
        var path = ConfigPath;
        var tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(c, JsonOpts);
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
