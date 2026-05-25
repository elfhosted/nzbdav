using Serilog;

namespace NzbWebDAV.Utils;

/// <summary>
/// Resolves the effective CPU count for the *current process* — i.e. the
/// container's cgroup quota, not the underlying host's core count.
///
/// On Kubernetes nodes (typically 88-core in our deployment) every nzbdav
/// pod has a CPU limit of 2 applied via the cgroup. Environment.ProcessorCount
/// returns 88 in that scenario, which makes any "scale work to cpu count"
/// heuristic wildly wrong (we'd fan 35-way parallel yEnc decode against a
/// 2-cpu cap and starve every other thread, including WebDAV streamers).
///
/// Reads cgroup v2 first (kernel 5.x+, modern container runtimes), falls
/// back to cgroup v1, and finally to Environment.ProcessorCount for bare
/// metal / dev. Result is cached after first read since cgroup limits do
/// not change for the life of the process.
/// </summary>
public static class CpuLimitUtil
{
    private static readonly Lazy<int> _effectiveCpuCount = new(ResolveEffectiveCpuCount);

    public static int EffectiveCpuCount => _effectiveCpuCount.Value;

    private static int ResolveEffectiveCpuCount()
    {
        var fromCgroup = ReadCgroupV2() ?? ReadCgroupV1();
        var fromOs = Environment.ProcessorCount;
        var effective = fromCgroup ?? fromOs;
        Log.Information(
            "Effective CPU count: {Effective} (cgroup={Cgroup}, host={Host})",
            effective,
            fromCgroup?.ToString() ?? "n/a",
            fromOs);
        return effective;
    }

    private static int? ReadCgroupV2()
    {
        try
        {
            const string path = "/sys/fs/cgroup/cpu.max";
            if (!File.Exists(path)) return null;
            var contents = File.ReadAllText(path).Trim();
            // "max <period>" means unlimited
            var parts = contents.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            if (parts[0] == "max") return null;
            if (!long.TryParse(parts[0], out var quota)) return null;
            if (!long.TryParse(parts[1], out var period)) return null;
            return QuotaToCpuCount(quota, period);
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadCgroupV1()
    {
        try
        {
            const string quotaPath = "/sys/fs/cgroup/cpu/cpu.cfs_quota_us";
            const string periodPath = "/sys/fs/cgroup/cpu/cpu.cfs_period_us";
            if (!File.Exists(quotaPath) || !File.Exists(periodPath)) return null;
            if (!long.TryParse(File.ReadAllText(quotaPath).Trim(), out var quota)) return null;
            if (!long.TryParse(File.ReadAllText(periodPath).Trim(), out var period)) return null;
            if (quota <= 0) return null; // -1 = unlimited
            return QuotaToCpuCount(quota, period);
        }
        catch
        {
            return null;
        }
    }

    private static int? QuotaToCpuCount(long quota, long period)
    {
        if (period <= 0) return null;
        // Round up so a 1.5-cpu cap reports 2 (matches what the scheduler
        // could actually run in parallel during a single period).
        var cpus = (int)((quota + period - 1) / period);
        return cpus > 0 ? cpus : null;
    }
}
