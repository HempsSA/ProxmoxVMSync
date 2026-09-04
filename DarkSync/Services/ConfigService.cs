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

    private static string GetConfigPath()
    {
        var baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "darksync_proxmox.json");
    }

    public static Config Load()
    {
        var path = GetConfigPath();
        if (!File.Exists(path))
            return new Config();

        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return ParseConfig(root);
        }
        catch
        {
            return new Config();
        }
    }

    private static Config ParseConfig(JsonElement d)
    {
        var c = new Config();

        if (d.TryGetProperty("sources", out var srcArr))
        {
            foreach (var s in srcArr.EnumerateArray())
            {
                c.Sources.Add(new Source
                {
                    Name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "PVE" : "PVE",
                    Path = s.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    Enabled = s.TryGetProperty("enabled", out var e) && e.GetBoolean(),
                    KeyFile = s.TryGetProperty("key_file", out var kf) ? kf.GetString() ?? "" : ""
                });
            }
        }

        c.Destination = d.TryGetProperty("destination", out var dest) ? dest.GetString() ?? "" : "";
        c.ArchiveId = d.TryGetProperty("archive_id", out var aid)
            ? aid.GetString() ?? ""
            : d.TryGetProperty("expected_archive_id", out var eaid) ? eaid.GetString() ?? "" : "";

        if (d.TryGetProperty("vms", out var vmArr) || d.TryGetProperty("protected_vms", out vmArr))
        {
            foreach (var v in vmArr.EnumerateArray())
            {
                var vm = new VmPolicy();
                if (v.TryGetProperty("vmid", out var vid)) vm.VmId = vid.GetInt32();
                if (v.TryGetProperty("name", out var vname)) vm.Name = vname.GetString() ?? "";
                if (v.TryGetProperty("enabled", out var ven)) vm.Enabled = ven.GetBoolean();
                if (v.TryGetProperty("importance", out var vi)) vm.Importance = vi.GetInt32();
                if (v.TryGetProperty("copies", out var vc)) vm.Copies = vc.GetInt32();
                else if (v.TryGetProperty("required_copies", out var rc)) vm.Copies = rc.GetInt32();
                if (v.TryGetProperty("max_age", out var ma)) vm.MaxAge = ma.GetInt32();
                else if (v.TryGetProperty("maximum_age_days", out var mad)) vm.MaxAge = mad.GetInt32();
                c.Vms.Add(vm);
            }
        }

        c.MinFreeGb = d.TryGetProperty("min_free_gb", out var mfg) ? mfg.GetInt32()
            : d.TryGetProperty("minimum_free_gb", out var mfg2) ? mfg2.GetInt32() : 5;
        c.Retention = d.TryGetProperty("retention", out var ret) ? ret.GetString() ?? "Keep all" : "Keep all";
        c.RecycleDays = d.TryGetProperty("recycle_days", out var rd) ? rd.GetInt32() : 7;
        c.NtfyEnabled = d.TryGetProperty("ntfy_enabled", out var ne) && ne.GetBoolean();
        c.NtfyServer = d.TryGetProperty("ntfy_server", out var ns) ? ns.GetString() ?? "https://ntfy.sh" : "https://ntfy.sh";
        c.NtfyTopic = d.TryGetProperty("ntfy_topic", out var nt) ? nt.GetString() ?? "" : "";
        c.NtfyToken = d.TryGetProperty("ntfy_token", out var nk) ? nk.GetString() ?? "" : "";
        c.NtfyPriority = d.TryGetProperty("ntfy_priority", out var np) ? np.GetString() ?? "high" : "high";
        c.NtfyOnSuccess = !d.TryGetProperty("ntfy_on_success", out var nos) || nos.GetBoolean();
        c.NtfyOnFailure = !d.TryGetProperty("ntfy_on_failure", out var nof) || nof.GetBoolean();
        c.ScheduleEnabled = d.TryGetProperty("schedule_enabled", out var se) && se.GetBoolean();
        c.ScheduleTime = d.TryGetProperty("schedule_time", out var st) ? st.GetString() ?? "02:00" : "02:00";
        c.ScheduleDryRun = d.TryGetProperty("schedule_dry_run", out var sdr) && sdr.GetBoolean();
        c.ScheduleLastRunDate = d.TryGetProperty("schedule_last_run_date", out var slr) ? slr.GetString() ?? "" : "";

        return c;
    }

    public static void Save(Config c)
    {
        var path = GetConfigPath();
        var tmp = path + ".tmp";
        try
        {
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
