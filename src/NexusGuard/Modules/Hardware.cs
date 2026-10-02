using NexusGuard.Core;

namespace NexusGuard.Modules;

public sealed class HardwareMetric
{
    public required string Label { get; init; }
    public required string Value { get; init; }
}

public enum HealthLevel { Healthy, Attention, Critical, Unknown }

public sealed class HardwareComponent
{
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public HealthLevel Level { get; init; } = HealthLevel.Unknown;
    public string Note { get; init; } = "";
    public List<HardwareMetric> Metrics { get; init; } = new();

    public string LevelText => Level switch
    {
        HealthLevel.Healthy => "Saudável",
        HealthLevel.Attention => "Atenção",
        HealthLevel.Critical => "Crítico",
        _ => "Desconhecido"
    };

    public bool IsHealthy => Level == HealthLevel.Healthy;
    public bool NeedsAttention => Level is HealthLevel.Attention or HealthLevel.Critical;
}

/// <summary>
/// Inventário e saúde do hardware a partir do WMI. As temperaturas de CPU/GPU não estão
/// disponíveis em WMI na maioria das máquinas — quando faltam, o campo aparece como indisponível
/// em vez de inventar um valor.
/// </summary>
public static class Hardware
{
    private sealed class Payload
    {
        public CpuInfo? Cpu { get; set; }
        public List<GpuInfo>? Gpus { get; set; }
        public List<MemInfo>? Memory { get; set; }
        public List<DiskInfo>? Disks { get; set; }
        public BatteryInfo? Battery { get; set; }
        public string? Error { get; set; }
    }

    private sealed class CpuInfo
    {
        public string? Name { get; set; }
        public int Cores { get; set; }
        public int Threads { get; set; }
        public int MaxClock { get; set; }
        public int CurrentClock { get; set; }
        public double? TemperatureC { get; set; }
        public int LoadPercent { get; set; }
    }

    private sealed class GpuInfo
    {
        public string? Name { get; set; }
        public long AdapterRam { get; set; }
        public string? DriverVersion { get; set; }
        public string? DriverDate { get; set; }
    }

    private sealed class MemInfo
    {
        public string? Manufacturer { get; set; }
        public long Capacity { get; set; }
        public int Speed { get; set; }
        public string? Slot { get; set; }
    }

    private sealed class DiskInfo
    {
        public string? Model { get; set; }
        public long Size { get; set; }
        public string? MediaType { get; set; }
        public bool PredictFailure { get; set; }
        public bool SmartAvailable { get; set; }
        public int? Temperature { get; set; }
        public long? PowerOnHours { get; set; }
        public int? Wear { get; set; }
    }

    private sealed class BatteryInfo
    {
        public string? Name { get; set; }
        public int ChargePercent { get; set; }
        public int DesignCapacity { get; set; }
        public int FullCapacity { get; set; }
        public string? Status { get; set; }
    }

    private const string Script = """
        $out = [ordered]@{}
        try {
            $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
            $temp = $null
            try {
                $t = Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction Stop |
                     Select-Object -First 1
                if ($t) { $temp = [math]::Round(($t.CurrentTemperature / 10) - 273.15, 1) }
            } catch {}

            $out.Cpu = [ordered]@{
                Name         = [string]$cpu.Name
                Cores        = [int]$cpu.NumberOfCores
                Threads      = [int]$cpu.NumberOfLogicalProcessors
                MaxClock     = [int]$cpu.MaxClockSpeed
                CurrentClock = [int]$cpu.CurrentClockSpeed
                TemperatureC = $temp
                LoadPercent  = [int]$cpu.LoadPercentage
            }

            $gpus = @()
            foreach ($g in @(Get-CimInstance Win32_VideoController)) {
                $gpus += [ordered]@{
                    Name          = [string]$g.Name
                    AdapterRam    = [int64]$g.AdapterRAM
                    DriverVersion = [string]$g.DriverVersion
                    DriverDate    = if ($g.DriverDate) { $g.DriverDate.ToString('yyyy-MM-dd') } else { '' }
                }
            }
            $out.Gpus = @($gpus)

            $mem = @()
            foreach ($m in @(Get-CimInstance Win32_PhysicalMemory)) {
                $mem += [ordered]@{
                    Manufacturer = [string]$m.Manufacturer
                    Capacity     = [int64]$m.Capacity
                    Speed        = [int]$m.Speed
                    Slot         = [string]$m.DeviceLocator
                }
            }
            $out.Memory = @($mem)

            $failures = @{}
            try {
                foreach ($f in @(Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_FailurePredictStatus -ErrorAction Stop)) {
                    $failures[[string]$f.InstanceName] = [bool]$f.PredictFailure
                }
            } catch {}

            $disks = @()
            foreach ($d in @(Get-CimInstance Win32_DiskDrive)) {
                $predict = $false
                $smart = $false
                foreach ($k in $failures.Keys) {
                    if ($k -like "*$($d.PNPDeviceID.Split('\')[-1])*") { $predict = $failures[$k]; $smart = $true }
                }

                $media = 'Desconhecido'
                try {
                    $pd = Get-PhysicalDisk -ErrorAction Stop | Where-Object { $_.DeviceId -eq $d.Index } | Select-Object -First 1
                    if ($pd) { $media = [string]$pd.MediaType }
                } catch {}

                $disks += [ordered]@{
                    Model          = [string]$d.Model
                    Size           = [int64]$d.Size
                    MediaType      = $media
                    PredictFailure = $predict
                    SmartAvailable = $smart
                    Temperature    = $null
                    PowerOnHours   = $null
                    Wear           = $null
                }
            }
            $out.Disks = @($disks)

            try {
                $b = Get-CimInstance Win32_Battery -ErrorAction Stop | Select-Object -First 1
                if ($b) {
                    $design = 0; $full = 0
                    try {
                        $s = Get-CimInstance -Namespace root\wmi -ClassName BatteryStaticData -ErrorAction Stop | Select-Object -First 1
                        if ($s) { $design = [int]$s.DesignedCapacity }
                        $fc = Get-CimInstance -Namespace root\wmi -ClassName BatteryFullChargedCapacity -ErrorAction Stop | Select-Object -First 1
                        if ($fc) { $full = [int]$fc.FullChargedCapacity }
                    } catch {}

                    $out.Battery = [ordered]@{
                        Name           = [string]$b.Name
                        ChargePercent  = [int]$b.EstimatedChargeRemaining
                        DesignCapacity = $design
                        FullCapacity   = $full
                        Status         = [string]$b.BatteryStatus
                    }
                }
            } catch {}
        } catch {
            $out.Error = $_.Exception.Message
        }
        $out | ConvertTo-Json -Depth 5 -Compress
        """;

    public static async Task<List<HardwareComponent>> ScanAsync(CancellationToken ct = default)
    {
        Logger.Info("Hardware", "Inventariando componentes…");

        var payload = await Shell.PowerShellJsonAsync<Payload>(Script, ct).ConfigureAwait(true);
        var list = new List<HardwareComponent>();

        if (payload is null)
        {
            Logger.Warn("Hardware", "Não foi possível ler o inventário de hardware.");
            return list;
        }

        if (payload.Cpu is { } cpu) list.Add(BuildCpu(cpu));
        foreach (var gpu in payload.Gpus ?? new()) list.Add(BuildGpu(gpu));
        if (payload.Memory is { Count: > 0 }) list.Add(BuildMemory(payload.Memory));
        foreach (var disk in payload.Disks ?? new()) list.Add(BuildDisk(disk));
        if (payload.Battery is { } battery) list.Add(BuildBattery(battery));

        Logger.Ok("Hardware", $"{list.Count} componentes inventariados.");
        return list;
    }

    private static HardwareComponent BuildCpu(CpuInfo cpu)
    {
        var metrics = new List<HardwareMetric>
        {
            new() { Label = "Núcleos", Value = $"{cpu.Cores} físicos · {cpu.Threads} lógicos" },
            new() { Label = "Frequência", Value = cpu.CurrentClock > 0 ? $"{cpu.CurrentClock} MHz de {cpu.MaxClock} MHz" : "—" },
            new() { Label = "Carga", Value = $"{cpu.LoadPercent}%" },
            new()
            {
                Label = "Temperatura",
                Value = cpu.TemperatureC is { } t ? $"{t:0.#} °C" : "não exposta pelo WMI"
            }
        };

        var level = cpu.TemperatureC switch
        {
            null => HealthLevel.Healthy,
            >= 90 => HealthLevel.Critical,
            >= 80 => HealthLevel.Attention,
            _ => HealthLevel.Healthy
        };

        return new HardwareComponent
        {
            Kind = "Processador",
            Name = cpu.Name ?? "Processador",
            Level = level,
            Note = cpu.TemperatureC is null
                ? "A maioria das placas não publica a temperatura no WMI."
                : "",
            Metrics = metrics
        };
    }

    private static HardwareComponent BuildGpu(GpuInfo gpu)
    {
        var old = DateTime.TryParse(gpu.DriverDate, out var date) && (DateTime.Now - date).TotalDays > 540;

        return new HardwareComponent
        {
            Kind = "Placa gráfica",
            Name = gpu.Name ?? "GPU",
            Level = old ? HealthLevel.Attention : HealthLevel.Healthy,
            Note = old ? "O driver tem mais de 18 meses." : "",
            Metrics = new List<HardwareMetric>
            {
                new() { Label = "Memória", Value = gpu.AdapterRam > 0 ? Fmt.Bytes(gpu.AdapterRam) : "—" },
                new() { Label = "Driver", Value = gpu.DriverVersion ?? "—" },
                new() { Label = "Data do driver", Value = string.IsNullOrWhiteSpace(gpu.DriverDate) ? "—" : gpu.DriverDate! }
            }
        };
    }

    private static HardwareComponent BuildMemory(List<MemInfo> modules)
    {
        var total = modules.Sum(m => m.Capacity);
        var speed = modules.Max(m => m.Speed);

        return new HardwareComponent
        {
            Kind = "Memória",
            Name = modules.Count == 1
                ? $"{Fmt.Bytes(total)} num módulo"
                : $"{Fmt.Bytes(total)} em {modules.Count} módulos",
            Level = HealthLevel.Healthy,
            Metrics = modules.Select(m => new HardwareMetric
            {
                Label = string.IsNullOrWhiteSpace(m.Slot) ? "Módulo" : m.Slot!,
                Value = $"{Fmt.Bytes(m.Capacity)} · {m.Speed} MT/s" +
                        (string.IsNullOrWhiteSpace(m.Manufacturer) ? "" : $" · {m.Manufacturer!.Trim()}")
            }).Append(new HardwareMetric { Label = "Velocidade", Value = $"{speed} MT/s" }).ToList()
        };
    }

    private static HardwareComponent BuildDisk(DiskInfo disk)
    {
        var level = disk.PredictFailure
            ? HealthLevel.Critical
            : disk.SmartAvailable ? HealthLevel.Healthy : HealthLevel.Unknown;

        var metrics = new List<HardwareMetric>
        {
            new() { Label = "Capacidade", Value = disk.Size > 0 ? Fmt.Bytes(disk.Size) : "—" },
            new() { Label = "Tipo", Value = string.IsNullOrWhiteSpace(disk.MediaType) ? "—" : disk.MediaType! },
            new()
            {
                Label = "S.M.A.R.T.",
                Value = disk.SmartAvailable
                    ? (disk.PredictFailure ? "falha prevista" : "sem avisos")
                    : "não disponível"
            }
        };

        return new HardwareComponent
        {
            Kind = "Disco",
            Name = disk.Model?.Trim() ?? "Disco",
            Level = level,
            Note = disk.PredictFailure
                ? "O disco prevê falha. Faça uma cópia de segurança agora e substitua-o."
                : disk.SmartAvailable
                    ? ""
                    : Fmt.IsAdmin
                        ? "Este disco não publica dados S.M.A.R.T. pelo WMI."
                        : "Os dados S.M.A.R.T. só ficam visíveis com privilégios de administrador.",
            Metrics = metrics
        };
    }

    private static HardwareComponent BuildBattery(BatteryInfo battery)
    {
        var health = battery.DesignCapacity > 0 && battery.FullCapacity > 0
            ? battery.FullCapacity * 100.0 / battery.DesignCapacity
            : (double?)null;

        var level = health switch
        {
            null => HealthLevel.Unknown,
            < 60 => HealthLevel.Critical,
            < 80 => HealthLevel.Attention,
            _ => HealthLevel.Healthy
        };

        return new HardwareComponent
        {
            Kind = "Bateria",
            Name = battery.Name ?? "Bateria",
            Level = level,
            Note = health is < 80 ? "A bateria perdeu capacidade face ao valor de fábrica." : "",
            Metrics = new List<HardwareMetric>
            {
                new() { Label = "Carga", Value = $"{battery.ChargePercent}%" },
                new()
                {
                    Label = "Saúde",
                    Value = health is { } h ? $"{h:0}% da capacidade original" : "não disponível"
                },
                new()
                {
                    Label = "Capacidade",
                    Value = battery.FullCapacity > 0
                        ? $"{battery.FullCapacity} mWh de {battery.DesignCapacity} mWh"
                        : "—"
                }
            }
        };
    }

    /// <summary>Teste de carga: ocupa todos os núcleos durante o tempo pedido.</summary>
    public static async Task<string> StressTestAsync(TimeSpan duration, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var cores = Environment.ProcessorCount;
        var end = DateTime.UtcNow + duration;
        var peak = 0.0;

        progress?.Report($"Teste a usar {cores} núcleos durante {Fmt.Duration(duration)}…");

        var workers = Enumerable.Range(0, cores).Select(_ => Task.Run(() =>
        {
            var x = 0.0;
            while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
                for (var i = 0; i < 1_000_000; i++) x += Math.Sqrt(i);
            return x;
        }, ct)).ToArray();

        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct).ConfigureAwait(true);

            peak = Math.Max(peak, SystemMonitor.Instance.CpuPercent);
            var left = end - DateTime.UtcNow;
            progress?.Report($"{Fmt.Duration(left)} restantes · CPU {SystemMonitor.Instance.CpuText} · pico {Fmt.Percent(peak)}");
        }

        try { await Task.WhenAll(workers).ConfigureAwait(true); } catch { }

        var summary = $"Teste concluído. Pico de CPU: {Fmt.Percent(peak)}.";
        History.Add("Hardware", "Teste de estresse concluído", Fmt.Duration(duration));
        Logger.Ok("Hardware", summary);

        return summary;
    }
}
