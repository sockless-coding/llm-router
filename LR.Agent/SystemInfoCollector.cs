using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

using LR.Core.Agents;

namespace LR.Agent;

/// <summary>
/// Best-effort hardware and usage snapshot: CPU, memory, the data directory's disk, IP addresses,
/// and NVIDIA GPUs via <c>nvidia-smi</c> when it's installed. Anything that can't be read is left null.
/// </summary>
public static class SystemInfoCollector
{
    private static (ulong Idle, ulong Total)? _lastCpu;
    private static readonly object CpuLock = new();
    private static List<AgentGpuInfo>? _cachedGpus;
    private static DateTime _gpuCachedAt;

    public static AgentSystemInfo Collect(string dataDirectory)
    {
        var info = new AgentSystemInfo
        {
            ProcessorCount = Environment.ProcessorCount,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        };

        try { ReadMemory(info); } catch { /* unavailable */ }
        try
        {
            var full = Path.GetFullPath(dataDirectory);
            var root = Path.GetPathRoot(full);
            // On Windows ask only for the data folder's own drive: enumerating every drive probes
            // network/offline ones and can take tens of seconds. On Linux the drive list is the mount
            // table (cheap), and the longest matching mount point is the filesystem the data lives on.
            var drive = OperatingSystem.IsWindows()
                ? (root is null ? null : new DriveInfo(root))
                : DriveInfo.GetDrives()
                    .Where(d => full.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal) && d.IsReady)
                    .OrderByDescending(d => d.RootDirectory.FullName.Length)
                    .FirstOrDefault() ?? (root is null ? null : new DriveInfo(root));
            if (drive is not null)
            {
                info.DiskTotalBytes = drive.TotalSize;
                info.DiskFreeBytes = drive.AvailableFreeSpace;
            }
        }
        catch { /* unavailable */ }
        try { info.CpuPercent = SampleCpuPercent(); } catch { /* unavailable */ }
        try
        {
            info.IpAddresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch { /* unavailable */ }
        info.Gpus = ReadGpus();
        return info;
    }

    private static void ReadMemory(AgentSystemInfo info)
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var parts = line.Split(':', 2);
                if (parts.Length != 2) continue;
                var kb = long.TryParse(parts[1].Trim().Split(' ')[0], out var v) ? v * 1024 : (long?)null;
                if (parts[0] == "MemTotal") info.TotalMemoryBytes = kb;
                else if (parts[0] == "MemAvailable") info.AvailableMemoryBytes = kb;
            }
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                info.TotalMemoryBytes = (long)status.TotalPhys;
                info.AvailableMemoryBytes = (long)status.AvailPhys;
                return;
            }
        }

        info.TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    }

    /// <summary>CPU busy % since the previous sample (null on the very first one).</summary>
    private static double? SampleCpuPercent()
    {
        (ulong Idle, ulong Total)? now = null;
        if (OperatingSystem.IsLinux() && File.Exists("/proc/stat"))
        {
            var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                .Select(f => ulong.Parse(f, CultureInfo.InvariantCulture)).ToArray();
            ulong idle = fields[3] + (fields.Length > 4 ? fields[4] : 0);
            now = (idle, (ulong)fields.Aggregate(0UL, (a, b) => a + b));
        }
        else if (OperatingSystem.IsWindows() && GetSystemTimes(out var idleTime, out var kernel, out var user))
        {
            // Kernel time includes idle time.
            now = (idleTime, kernel + user);
        }
        if (now is null) return null;

        lock (CpuLock)
        {
            var previous = _lastCpu;
            _lastCpu = now;
            if (previous is null || now.Value.Total <= previous.Value.Total) return null;
            double total = now.Value.Total - previous.Value.Total;
            double idle = now.Value.Idle - previous.Value.Idle;
            return Math.Round(Math.Clamp((1 - idle / total) * 100, 0, 100), 1);
        }
    }

    /// <summary>NVIDIA GPUs via nvidia-smi, cached for 10s (it's slow to start).</summary>
    private static List<AgentGpuInfo> ReadGpus()
    {
        if (_cachedGpus is not null && DateTime.UtcNow - _gpuCachedAt < TimeSpan.FromSeconds(10))
            return _cachedGpus;

        var gpus = new List<AgentGpuInfo>();
        try
        {
            var startInfo = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total,memory.used,utilization.gpu --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var p = line.Split(',', StringSplitOptions.TrimEntries);
                    if (p.Length < 4) continue;
                    gpus.Add(new AgentGpuInfo
                    {
                        Name = p[0],
                        Vendor = "NVIDIA",
                        MemoryTotalBytes = long.TryParse(p[1], out var t) ? t * 1024 * 1024 : null,
                        MemoryUsedBytes = long.TryParse(p[2], out var u) ? u * 1024 * 1024 : null,
                        UtilizationPercent = int.TryParse(p[3], out var g) ? g : null,
                    });
                }
            }
        }
        catch
        {
            // No NVIDIA driver / nvidia-smi not on PATH.
        }

        _cachedGpus = gpus;
        _gpuCachedAt = DateTime.UtcNow;
        return gpus;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);
}
