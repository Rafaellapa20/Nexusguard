using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace NexusGuard.Core;

public sealed class DriveInfoSnapshot
{
    public string Letter { get; init; } = "";
    public string Label { get; init; } = "";
    public string Format { get; init; } = "";
    public DriveType Type { get; init; }
    public long Total { get; init; }
    public long Free { get; init; }

    public long Used => Total - Free;
    public double UsedPercent => Total > 0 ? Used * 100.0 / Total : 0;
    public string TotalText => Fmt.Bytes(Total);
    public string FreeText => Fmt.Bytes(Free);
    public string UsedText => Fmt.Bytes(Used);

    public string TypeText => Type switch
    {
        DriveType.Removable => "Removível",
        DriveType.Network => "Rede",
        DriveType.CDRom => "Ótico",
        DriveType.Ram => "RAM",
        _ => "Local"
    };

    public string Display => string.IsNullOrWhiteSpace(Label)
        ? $"{Letter}  ({TypeText}, {FreeText} livres de {TotalText})"
        : $"{Letter}  {Label} — {TypeText}, {FreeText} livres de {TotalText}";

    public override string ToString() => Display;
}

/// <summary>Amostragem periódica de CPU, memória e discos. Uma única instância partilhada pela app.</summary>
public sealed class SystemMonitor : Observable, IDisposable
{
    public static SystemMonitor Instance { get; } = new();

    private readonly DispatcherTimer _timer;
    private long _prevIdle, _prevKernel, _prevUser;
    private bool _firstSample = true;

    private SystemMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Sample();
        LoadCpuName();
    }

    public void Start()
    {
        if (!_timer.IsEnabled)
        {
            Sample();
            RefreshDrives();
            _timer.Start();
        }
    }

    public void Stop() => _timer.Stop();

    // ---------------- CPU ----------------
    private double _cpuPercent;
    public double CpuPercent { get => _cpuPercent; private set { if (Set(ref _cpuPercent, value)) Raise(nameof(CpuText)); } }
    public string CpuText => Fmt.Percent(CpuPercent);

    public int CpuCores { get; } = Environment.ProcessorCount;

    private string _cpuName = "Processador";
    public string CpuName { get => _cpuName; private set => Set(ref _cpuName, value); }

    // ---------------- Memória ----------------
    private double _ramPercent;
    public double RamPercent { get => _ramPercent; private set { if (Set(ref _ramPercent, value)) Raise(nameof(RamText)); } }
    public string RamText => Fmt.Percent(RamPercent);

    private long _ramTotal;
    public long RamTotal { get => _ramTotal; private set { if (Set(ref _ramTotal, value)) Raise(nameof(RamTotalText)); } }
    public string RamTotalText => Fmt.Bytes(RamTotal);

    private long _ramUsed;
    public long RamUsed { get => _ramUsed; private set { if (Set(ref _ramUsed, value)) Raise(nameof(RamUsedText)); } }
    public string RamUsedText => Fmt.Bytes(RamUsed);

    private long _ramFree;
    public long RamFree { get => _ramFree; private set { if (Set(ref _ramFree, value)) Raise(nameof(RamFreeText)); } }
    public string RamFreeText => Fmt.Bytes(RamFree);

    private long _cacheBytes;
    public long CacheBytes { get => _cacheBytes; private set { if (Set(ref _cacheBytes, value)) Raise(nameof(CacheText)); } }
    public string CacheText => Fmt.Bytes(CacheBytes);

    public double CachePercent => RamTotal > 0 ? Math.Clamp(CacheBytes * 100.0 / RamTotal, 0, 100) : 0;

    private int _processCount;
    public int ProcessCount { get => _processCount; private set => Set(ref _processCount, value); }

    private int _threadCount;
    public int ThreadCount { get => _threadCount; private set => Set(ref _threadCount, value); }

    private int _handleCount;
    public int HandleCount { get => _handleCount; private set => Set(ref _handleCount, value); }

    // ---------------- Disco do sistema ----------------
    private double _systemDiskPercent;
    public double SystemDiskPercent { get => _systemDiskPercent; private set { if (Set(ref _systemDiskPercent, value)) Raise(nameof(SystemDiskText)); } }
    public string SystemDiskText => Fmt.Percent(SystemDiskPercent);

    private string _systemDiskDetail = "—";
    public string SystemDiskDetail { get => _systemDiskDetail; private set => Set(ref _systemDiskDetail, value); }

    public List<DriveInfoSnapshot> Drives { get; private set; } = new();

    public string Uptime
    {
        get
        {
            try { return Fmt.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64)); }
            catch { return "—"; }
        }
    }

    public string MachineSummary => $"{Environment.MachineName} · {Fmt.WindowsName}";

    private void Sample()
    {
        SampleCpu();
        SampleMemory();
        SampleSystemDisk();
        Raise(nameof(Uptime));
    }

    private void SampleCpu()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return;

        if (_firstSample)
        {
            _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
            _firstSample = false;
            return;
        }

        var dIdle = idle - _prevIdle;
        var dKernel = kernel - _prevKernel;
        var dUser = user - _prevUser;
        _prevIdle = idle; _prevKernel = kernel; _prevUser = user;

        // dKernel inclui o tempo em idle.
        var total = dKernel + dUser;
        if (total <= 0) return;

        CpuPercent = Math.Clamp((total - dIdle) * 100.0 / total, 0, 100);
    }

    private void LoadCpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name && name.Length > 0)
                CpuName = name.Trim();
        }
        catch
        {
            // Mantém o valor por omissão.
        }
    }

    private void SampleMemory()
    {
        var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref ms))
        {
            RamTotal = (long)ms.ullTotalPhys;
            RamFree = (long)ms.ullAvailPhys;
            RamUsed = RamTotal - RamFree;
            RamPercent = ms.dwMemoryLoad;
        }

        var pi = new Native.PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>() };
        if (Native.GetPerformanceInfo(ref pi, pi.cb))
        {
            var page = (long)pi.PageSize;
            CacheBytes = (long)pi.SystemCache * page;
            Raise(nameof(CachePercent));
            ProcessCount = (int)pi.ProcessCount;
            ThreadCount = (int)pi.ThreadCount;
            HandleCount = (int)pi.HandleCount;
        }
    }

    private void SampleSystemDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            if (Native.GetDiskFreeSpaceEx(root, out _, out var total, out var free) && total > 0)
            {
                var used = (long)total - (long)free;
                SystemDiskPercent = used * 100.0 / (long)total;
                SystemDiskDetail = $"{Fmt.Bytes(free)} livres de {Fmt.Bytes((long)total)}";
            }
        }
        catch
        {
            // Sem leitura, mantém o último valor conhecido.
        }
    }

    public void RefreshDrives()
    {
        var list = new List<DriveInfoSnapshot>();

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is DriveType.CDRom or DriveType.Unknown) continue;

                list.Add(new DriveInfoSnapshot
                {
                    Letter = d.Name.TrimEnd('\\'),
                    Label = SafeLabel(d),
                    Format = d.DriveFormat,
                    Type = d.DriveType,
                    Total = d.TotalSize,
                    Free = d.AvailableFreeSpace
                });
            }
            catch
            {
                // Unidade inacessível: ignorada.
            }
        }

        Drives = list;
        Raise(nameof(Drives));
    }

    private static string SafeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel; } catch { return string.Empty; }
    }

    /// <summary>Mede a utilização de CPU por processo ao longo de um intervalo.</summary>
    public static async Task<List<ProcessSnapshot>> SampleProcessesAsync(int sampleMs = 500, CancellationToken ct = default)
    {
        var first = new Dictionary<int, (TimeSpan cpu, Process p)>();

        foreach (var p in Process.GetProcesses())
        {
            try { first[p.Id] = (p.TotalProcessorTime, p); }
            catch { p.Dispose(); }
        }

        var sw = Stopwatch.StartNew();
        await Task.Delay(sampleMs, ct).ConfigureAwait(false);
        sw.Stop();

        var cores = Math.Max(1, Environment.ProcessorCount);
        var result = new List<ProcessSnapshot>(first.Count);

        foreach (var (id, entry) in first)
        {
            var p = entry.p;
            try
            {
                p.Refresh();
                var cpuDelta = (p.TotalProcessorTime - entry.cpu).TotalMilliseconds;
                var cpu = Math.Clamp(cpuDelta / (sw.Elapsed.TotalMilliseconds * cores) * 100.0, 0, 100);

                result.Add(new ProcessSnapshot
                {
                    Pid = id,
                    Name = p.ProcessName,
                    WorkingSet = p.WorkingSet64,
                    PrivateBytes = p.PrivateMemorySize64,
                    Threads = p.Threads.Count,
                    CpuPercent = cpu,
                    Description = TryDescription(p)
                });
            }
            catch
            {
                // Processo terminou ou e inacessível.
            }
            finally
            {
                p.Dispose();
            }
        }

        return result;
    }

    private static string TryDescription(Process p)
    {
        try
        {
            var module = p.MainModule;
            if (module is null) return string.Empty;
            var desc = module.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(desc) ? string.Empty : desc;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose() => _timer.Stop();
}

public sealed class ProcessSnapshot
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public long WorkingSet { get; init; }
    public long PrivateBytes { get; init; }
    public int Threads { get; init; }
    public double CpuPercent { get; init; }

    public string WorkingSetText => Fmt.Bytes(WorkingSet);
    public string CpuText => Fmt.Percent(CpuPercent, 1);
    public string Label => string.IsNullOrWhiteSpace(Description) ? Name : $"{Name} — {Description}";
}
