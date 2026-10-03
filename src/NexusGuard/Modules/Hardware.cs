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
        public string? PartNumber { get; set; }
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

            # Win32_VideoController.AdapterRAM e um inteiro de 32 bits e satura nos 4 GB:
            # uma placa de 16 GB aparece com 4. O valor real esta no registo do driver de video.
            $vram = New-Object 'System.Collections.Generic.Dictionary[string,long]' ([StringComparer]::OrdinalIgnoreCase)
            try {
                $displayClass = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
                # SilentlyContinue e nao Stop: o cabecalho destes scripts poe
                # $ErrorActionPreference = 'Stop', e uma unica subchave sem permissao abortaria
                # a enumeracao inteira, deixando o dicionario vazio sem dar nas vistas.
                foreach ($k in @(Get-ChildItem $displayClass -ErrorAction SilentlyContinue)) {
                    try {
                        if ($k.PSChildName -notmatch '^\d{4}$') { continue }

                        $pr = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
                        if (-not $pr -or -not $pr.DriverDesc) { continue }

                        $size = 0
                        if ($pr.'HardwareInformation.qwMemorySize') {
                            $size = [int64]$pr.'HardwareInformation.qwMemorySize'
                        } elseif ($pr.'HardwareInformation.MemorySize') {
                            $raw = $pr.'HardwareInformation.MemorySize'
                            if ($raw -is [byte[]]) { $size = [int64][System.BitConverter]::ToUInt32($raw, 0) }
                            else { $size = [int64]$raw }
                        }

                        if ($size -gt 0) { $vram[([string]$pr.DriverDesc).Trim()] = $size }
                    } catch { }
                }
            } catch {}

            $gpus = @()
            foreach ($g in @(Get-CimInstance Win32_VideoController)) {
                $gpuName = ([string]$g.Name).Trim()
                $gpuRam = [int64]$g.AdapterRAM
                if ($vram.ContainsKey($gpuName)) { $gpuRam = $vram[$gpuName] }

                $gpus += [ordered]@{
                    Name          = $gpuName
                    AdapterRam    = $gpuRam
                    DriverVersion = [string]$g.DriverVersion
                    DriverDate    = if ($g.DriverDate) { $g.DriverDate.ToString('yyyy-MM-dd') } else { '' }
                }
            }
            $out.Gpus = @($gpus)

            $mem = @()
            foreach ($m in @(Get-CimInstance Win32_PhysicalMemory)) {
                $mem += [ordered]@{
                    Manufacturer = [string]$m.Manufacturer
                    PartNumber   = [string]$m.PartNumber
                    Capacity     = [int64]$m.Capacity
                    # ConfiguredClockSpeed e a velocidade a que o modulo esta mesmo a correr;
                    # Speed e so o rotulo do SPD.
                    Speed        = if ($m.ConfiguredClockSpeed) { [int]$m.ConfiguredClockSpeed } else { [int]$m.Speed }
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

        // Duas fontes independentes: o inventário (nomes, drivers, módulos de memória) vem do WMI,
        // os valores vivos (temperaturas, VRAM real, S.M.A.R.T.) vêm dos sensores. Correm ao mesmo
        // tempo porque não dependem uma da outra, e a leitura de sensores bloqueia — daí o Task.Run.
        var inventory = Shell.PowerShellJsonAsync<Payload>(Script, ct);
        var reading = Task.Run(() => Sensors.Read(force: true), ct);

        var payload = await inventory.ConfigureAwait(true);
        var sensors = await reading.ConfigureAwait(true);
        var list = new List<HardwareComponent>();

        if (payload is null)
        {
            Logger.Warn("Hardware", "Não foi possível ler o inventário de hardware.");
            return list;
        }

        if (payload.Cpu is { } cpu) list.Add(BuildCpu(cpu, sensors.Cpu, sensors.LowLevelNote));
        foreach (var gpu in payload.Gpus ?? new()) list.Add(BuildGpu(gpu, Match(sensors.Gpus, g => g.Name, gpu.Name)));
        if (payload.Memory is { Count: > 0 }) list.Add(BuildMemory(payload.Memory));
        foreach (var disk in payload.Disks ?? new()) list.Add(BuildDisk(disk, Match(sensors.Disks, d => d.Name, disk.Model)));
        if (payload.Battery is { } battery) list.Add(BuildBattery(battery));

        Logger.Ok("Hardware", $"{list.Count} componentes inventariados.");
        return list;
    }

    private static HardwareComponent BuildCpu(CpuInfo cpu, CpuSensors? sensors, string? lowLevelNote)
    {
        var tempC = sensors?.PackageTempC ?? cpu.TemperatureC;

        var metrics = new List<HardwareMetric>
        {
            new() { Label = "Núcleos", Value = $"{cpu.Cores} físicos · {cpu.Threads} lógicos" }
        };

        // O WMI devolve CurrentClockSpeed igual ao máximo, o que é só a frequência base repetida.
        // A via do powrprof dá o valor verdadeiro e não depende de driver nenhum.
        // Quando a frequência actual vem igual à máxima, não é uma medição: é a frequência nominal
        // repetida, e apresentá-la como "4201 de 4201 MHz" só dá a ilusão de uma leitura ao vivo.
        var clock = Sensors.CpuClock();

        if (clock is { MaxMhz: > 0 } live && live.CurrentMhz != live.MaxMhz)
        {
            metrics.Add(new HardwareMetric
            {
                Label = "Frequência",
                Value = $"{live.CurrentMhz} MHz de {live.MaxMhz} MHz"
            });
        }
        else
        {
            var baseMhz = clock?.MaxMhz > 0 ? clock.Value.MaxMhz : cpu.MaxClock;
            metrics.Add(new HardwareMetric
            {
                Label = "Frequência base",
                Value = baseMhz > 0 ? $"{baseMhz} MHz" : "—"
            });
        }

        metrics.Add(new HardwareMetric { Label = "Carga", Value = $"{cpu.LoadPercent}%" });

        if (sensors is { CoreLoads.Count: > 1 })
        {
            metrics.Add(new HardwareMetric
            {
                Label = "Carga por núcleo",
                Value = $"mais ocupado {sensors.CoreLoads.Max():0}% · mais livre {sensors.CoreLoads.Min():0}%"
            });
        }

        metrics.Add(new HardwareMetric
        {
            Label = "Temperatura",
            Value = tempC is { } t ? $"{t:0.#} °C" : "indisponível"
        });

        if (sensors?.PackagePowerW is { } watts)
            metrics.Add(new HardwareMetric { Label = "Consumo", Value = $"{watts:0.#} W" });

        var level = tempC switch
        {
            null => HealthLevel.Healthy,
            >= 90 => HealthLevel.Critical,
            >= 80 => HealthLevel.Attention,
            _ => HealthLevel.Healthy
        };

        return new HardwareComponent
        {
            Kind = "Processador",
            Name = sensors?.Name is { Length: > 0 } named ? named : cpu.Name ?? "Processador",
            Level = level,
            Note = tempC is null ? lowLevelNote ?? "" : "",
            Metrics = metrics
        };
    }

    private static HardwareComponent BuildGpu(GpuInfo gpu, GpuSensors? sensors)
    {
        var old = DateTime.TryParse(gpu.DriverDate, out var date) && (DateTime.Now - date).TotalDays > 540;
        var metrics = new List<HardwareMetric>();

        // AdapterRAM do WMI é um campo de 32 bits: satura nos 4 GB e mente em qualquer placa maior
        // do que isso. Quando os sensores dão a memória, é essa que vale — e vem com o uso ao vivo.
        if (sensors is { IsIntegrated: true })
        {
            metrics.Add(new HardwareMetric
            {
                Label = "Memória",
                Value = "partilhada com a memória do sistema"
            });
        }
        else if (sensors?.MemoryTotalMb is { } totalMb and > 0)
        {
            metrics.Add(new HardwareMetric
            {
                Label = "Memória",
                Value = sensors.MemoryUsedMb is { } used
                    ? $"{totalMb / 1024.0:0.#} GB · {used / 1024.0:0.#} GB em uso"
                    : $"{totalMb / 1024.0:0.#} GB"
            });
        }
        else if (gpu.AdapterRam is > 0 and < 4L * 1024 * 1024 * 1024)
        {
            metrics.Add(new HardwareMetric { Label = "Memória", Value = Fmt.Bytes(gpu.AdapterRam) });
        }
        else
        {
            metrics.Add(new HardwareMetric { Label = "Memória", Value = "indisponível" });
        }

        if (sensors?.CoreTempC is { } temp)
        {
            metrics.Add(new HardwareMetric
            {
                Label = "Temperatura",
                Value = sensors.HotSpotTempC is { } hot
                    ? $"{temp:0} °C · ponto quente {hot:0} °C"
                    : $"{temp:0} °C"
            });
        }

        if (sensors?.FanRpm is { } rpm)
        {
            metrics.Add(new HardwareMetric
            {
                Label = "Ventoinha",
                Value = rpm > 0 ? $"{rpm:0} rpm" : "parada — a placa está fria"
            });
        }

        if (sensors?.LoadPercent is { } load)
            metrics.Add(new HardwareMetric { Label = "Uso", Value = $"{load:0}%" });

        if (sensors?.PowerW is { } watts)
            metrics.Add(new HardwareMetric { Label = "Consumo", Value = $"{watts:0.#} W" });

        metrics.Add(new HardwareMetric { Label = "Driver", Value = gpu.DriverVersion ?? "—" });
        metrics.Add(new HardwareMetric
        {
            Label = "Data do driver",
            Value = string.IsNullOrWhiteSpace(gpu.DriverDate) ? "—" : gpu.DriverDate!
        });

        var level = sensors?.CoreTempC switch
        {
            >= 95 => HealthLevel.Critical,
            >= 85 => HealthLevel.Attention,
            _ => old ? HealthLevel.Attention : HealthLevel.Healthy
        };

        return new HardwareComponent
        {
            Kind = "Placa gráfica",
            Name = gpu.Name ?? "GPU",
            Level = level,
            Note = sensors?.CoreTempC >= 85
                ? "A placa está quente. Verifique a ventilação da caixa e o pó nos dissipadores."
                : old ? "O driver tem mais de 18 meses." : "",
            Metrics = metrics
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
                Value = $"{Fmt.Bytes(m.Capacity)} · {m.Speed} MT/s · {DescribeModule(m)}"
            }).Append(new HardwareMetric { Label = "Velocidade", Value = $"{speed} MT/s" }).ToList()
        };
    }

    /// <summary>
    /// Muitas placas não preenchem o fabricante do módulo e devolvem "Unknown"; o part number
    /// identifica o kit muito melhor do que isso.
    /// </summary>
    private static string DescribeModule(MemInfo m)
    {
        var maker = m.Manufacturer?.Trim() ?? string.Empty;

        var useful = maker.Length > 0 &&
                     !maker.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                     !maker.StartsWith("Undefined", StringComparison.OrdinalIgnoreCase);

        if (useful) return maker;

        var part = m.PartNumber?.Trim() ?? string.Empty;
        return part.Length > 0 ? part : "fabricante não identificado";
    }

    private static HardwareComponent BuildDisk(DiskInfo disk, DiskSensors? sensors)
    {
        var metrics = new List<HardwareMetric>
        {
            new() { Label = "Capacidade", Value = disk.Size > 0 ? Fmt.Bytes(disk.Size) : "—" },
            new() { Label = "Tipo", Value = string.IsNullOrWhiteSpace(disk.MediaType) ? "—" : disk.MediaType! }
        };

        // Num NVMe a vida restante vem directa; nos que só dão desgaste acumulado, é o complemento.
        var life = sensors?.LifePercent ?? (sensors?.UsedPercent is { } used ? 100 - used : null);

        if (life is { } remaining)
            metrics.Add(new HardwareMetric { Label = "Saúde", Value = $"{remaining:0}%" });

        if (sensors?.SparePercent is { } spare)
            metrics.Add(new HardwareMetric { Label = "Blocos de reserva", Value = $"{spare:0}% disponíveis" });

        if (sensors?.TempC is { } temp)
        {
            metrics.Add(new HardwareMetric
            {
                Label = "Temperatura",
                Value = sensors.WarningTempC is { } warn
                    ? $"{temp:0} °C · avisa aos {warn:0} °C"
                    : $"{temp:0} °C"
            });
        }

        if (sensors?.PowerOnHours is { } hours and > 0)
        {
            metrics.Add(new HardwareMetric
            {
                Label = "Tempo ligado",
                Value = sensors.PowerOnCount is { } starts and > 0
                    ? $"{hours:0} h em {starts:0} arranques"
                    : $"{hours:0} h"
            });
        }

        if (sensors?.DataWrittenGb is { } written and > 0)
        {
            metrics.Add(new HardwareMetric
            {
                Label = "Escrito desde novo",
                Value = written >= 1024 ? $"{written / 1024.0:0.#} TB" : $"{written:0} GB"
            });
        }

        metrics.Add(new HardwareMetric
        {
            Label = "S.M.A.R.T.",
            Value = disk.PredictFailure ? "falha prevista"
                  : life is not null ? "lido em detalhe"
                  : disk.SmartAvailable ? "sem avisos"
                  : "não disponível"
        });

        var level = disk.PredictFailure
            ? HealthLevel.Critical
            : life switch
            {
                < 10 => HealthLevel.Critical,
                < 30 => HealthLevel.Attention,
                not null => HealthLevel.Healthy,
                null => disk.SmartAvailable ? HealthLevel.Healthy : HealthLevel.Unknown
            };

        // Reserva no limite que o próprio disco declara é o aviso mais fiável que um NVMe dá.
        if (sensors is { SparePercent: { } sp, SpareThresholdPercent: { } threshold } && sp <= threshold)
            level = HealthLevel.Critical;

        var note = disk.PredictFailure
            ? "O disco prevê falha. Faça uma cópia de segurança agora e substitua-o."
            : level == HealthLevel.Critical
                ? "O disco está no fim da vida útil. Faça uma cópia de segurança e planeie a troca."
                : level == HealthLevel.Attention
                    ? "O disco já gastou boa parte da vida prevista. Vale ir acompanhando."
                    : life is not null || disk.SmartAvailable
                        ? ""
                        : Fmt.IsAdmin
                            ? "Este disco não publica dados S.M.A.R.T."
                            : "Os dados S.M.A.R.T. só ficam visíveis com privilégios de administrador.";

        return new HardwareComponent
        {
            Kind = "Disco",
            Name = disk.Model?.Trim() ?? sensors?.Name ?? "Disco",
            Level = level,
            Note = note,
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

    /// <summary>
    /// O WMI e os sensores dão nomes parecidos mas não iguais — "Samsung SSD 9100 PRO with Heatsink
    /// 2TB" contra "Samsung SSD 9100 PRO 2TB" — por isso a correspondência é por palavras em comum
    /// e não por igualdade. Com uma só peça do género, não há nada para desambiguar.
    /// </summary>
    private static T? Match<T>(List<T> candidates, Func<T, string> nameOf, string? target) where T : class
    {
        if (candidates.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(target)) return candidates.Count == 1 ? candidates[0] : null;

        var wanted = Words(target);
        if (wanted.Count == 0) return candidates.Count == 1 ? candidates[0] : null;

        T? best = null;
        var bestScore = 0;

        foreach (var candidate in candidates)
        {
            var score = Words(nameOf(candidate)).Count(wanted.Contains);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        // Uma palavra em comum é coincidência ("AMD" aparece no processador e na gráfica);
        // duas já identificam a peça.
        return bestScore >= 2 ? best : candidates.Count == 1 ? candidates[0] : null;
    }

    private static HashSet<string> Words(string text) =>
        text.Split(new[] { ' ', '(', ')', '-', '/', '.', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .Where(w => w.Length > 1 && w is not ("tm" or "with" or "the" or "and"))
            .ToHashSet();

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
