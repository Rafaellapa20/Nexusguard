using System.Runtime.InteropServices;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace NexusGuard.Core;

/// <summary>Leituras de uma placa gráfica. Tudo anulável: o que não vier fica em branco.</summary>
public sealed class GpuSensors
{
    public string Name { get; init; } = "";
    public double? CoreTempC { get; set; }
    public double? HotSpotTempC { get; set; }
    public double? FanRpm { get; set; }
    public double? FanPercent { get; set; }
    public double? CoreClockMhz { get; set; }
    public double? MemoryClockMhz { get; set; }
    public double? PowerW { get; set; }
    public double? LoadPercent { get; set; }
    public double? MemoryTotalMb { get; set; }
    public double? MemoryUsedMb { get; set; }
    public double? MemorySharedTotalMb { get; set; }

    /// <summary>
    /// Placas integradas partilham a RAM do sistema; mostrar a fatia reservada delas como se
    /// fosse VRAM dedicada dá números absurdos (meio GB numa máquina com 32 GB).
    /// </summary>
    public bool IsIntegrated { get; init; }
}

/// <summary>Leituras S.M.A.R.T. de um disco.</summary>
public sealed class DiskSensors
{
    public string Name { get; init; } = "";
    public double? TempC { get; set; }
    public double? WarningTempC { get; set; }
    public double? CriticalTempC { get; set; }

    /// <summary>Saúde restante em percentagem, como o CrystalDiskInfo a apresenta.</summary>
    public double? LifePercent { get; set; }
    public double? SparePercent { get; set; }
    public double? SpareThresholdPercent { get; set; }
    public double? UsedPercent { get; set; }

    public double? PowerOnHours { get; set; }
    public double? PowerOnCount { get; set; }
    public double? DataReadGb { get; set; }
    public double? DataWrittenGb { get; set; }
    public double? TotalSpaceGb { get; set; }
    public double? FreeSpaceGb { get; set; }
}

/// <summary>Leituras do processador.</summary>
public sealed class CpuSensors
{
    public string Name { get; init; } = "";
    public double? PackageTempC { get; set; }
    public double? PackagePowerW { get; set; }
    public double? AverageClockMhz { get; set; }
    public double? TotalLoadPercent { get; set; }
    public List<double> CoreLoads { get; } = new();
}

/// <summary>Um instante de todos os sensores que a máquina deixou ler.</summary>
public sealed class SensorSnapshot
{
    public bool Available { get; init; }

    /// <summary>Quando <see cref="Available"/> é falso, explica porquê numa frase.</summary>
    public string? Unavailable { get; init; }

    public string? MotherboardName { get; init; }
    public CpuSensors? Cpu { get; init; }
    public List<GpuSensors> Gpus { get; init; } = new();
    public List<DiskSensors> Disks { get; init; } = new();
    public double? MemoryUsedGb { get; init; }
    public double? MemoryAvailableGb { get; init; }

    /// <summary>
    /// Explicação para as leituras que dependem do driver de baixo nível (temperatura e potência
    /// do processador, ventoinhas do chassis). Nula quando essas leituras funcionaram.
    /// </summary>
    public string? LowLevelNote { get; init; }

    public static SensorSnapshot Off(string reason) => new() { Available = false, Unavailable = reason };
}

/// <summary>
/// Sensores reais de hardware, através da LibreHardwareMonitorLib (MPL-2.0, usada sem alterações).
///
/// Três coisas que esta classe resolve e que justificam existir em vez de se chamar a biblioteca
/// directamente das vistas:
///
/// 1. A biblioteca não é segura para acesso simultâneo — todas as leituras passam por um lock.
/// 2. Abrir e fechar a cada leitura é lento e perde o histórico dos sensores, por isso o objecto
///    <c>Computer</c> fica aberto durante a vida do processo e as leituras são limitadas no tempo.
/// 3. Boa parte das leituras do processador exige um driver de kernel (<c>WinRing0</c>) que o
///    Windows bloqueia quando a Integridade de Memória está ligada. Em vez de devolver zeros que
///    passam por avaria, essas leituras ficam nulas e <see cref="SensorSnapshot.LowLevelNote"/>
///    diz o motivo. O NexusGuard nunca pede ao utilizador para desligar essa protecção.
/// </summary>
public static class Sensors
{
    private static readonly object Gate = new();
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(900);

    private static Computer? _computer;
    private static UpdateVisitor? _visitor;
    private static bool _openFailed;
    private static string? _openError;
    private static SensorSnapshot? _last;
    private static DateTime _lastRead = DateTime.MinValue;

    /// <summary>Lê os sensores. Nunca atira: um problema devolve um instantâneo indisponível.</summary>
    public static SensorSnapshot Read(bool force = false)
    {
        lock (Gate)
        {
            if (!force && _last is not null && DateTime.UtcNow - _lastRead < MinInterval)
                return _last;

            try
            {
                if (!EnsureOpen())
                    return _last = SensorSnapshot.Off(_openError ?? "Não foi possível ler os sensores.");

                _computer!.Accept(_visitor!);

                // Carga e actividade são deltas entre duas amostras: colhidas no mesmo instante dão
                // valores sem sentido (100% num núcleo parado). Numa leitura forçada vale esperar
                // meio segundo e colher outra vez; nas leituras contínuas o intervalo já existe.
                if (force)
                {
                    Thread.Sleep(500);
                    _computer.Accept(_visitor!);
                }

                _last = Build(_computer);
                _lastRead = DateTime.UtcNow;
                return _last;
            }
            catch (Exception ex)
            {
                Logger.Warn("Sensores", $"leitura falhou — {ex.GetType().Name}: {ex.Message}");
                return _last = SensorSnapshot.Off("Não foi possível ler os sensores desta máquina.");
            }
        }
    }

    /// <summary>
    /// Frequência média actual e máxima do processador, em MHz, pela via do próprio Windows.
    /// Não passa pela biblioteca de sensores nem por driver nenhum, por isso é a única leitura de
    /// relógio que continua a funcionar com a Integridade de Memória ligada.
    /// </summary>
    public static (int CurrentMhz, int MaxMhz)? CpuClock()
    {
        try
        {
            var count = Environment.ProcessorCount;
            var size = Marshal.SizeOf<Native.PROCESSOR_POWER_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(size * count);

            try
            {
                var status = Native.CallNtPowerInformation(
                    Native.ProcessorInformation, IntPtr.Zero, 0, buffer, (uint)(size * count));

                if (status != 0) return null;

                long sum = 0, max = 0;
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<Native.PROCESSOR_POWER_INFORMATION>(buffer + (i * size));
                    sum += info.CurrentMhz;
                    max = Math.Max(max, info.MaxMhz);
                }

                var current = (int)(sum / count);
                return current > 0 ? (current, (int)max) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Sensores", $"frequência do processador indisponível — {ex.Message}");
            return null;
        }
    }

    /// <summary>Fecha a biblioteca. Chamar ao sair, para o driver não ficar carregado.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            try { _computer?.Close(); }
            catch (Exception ex) { Logger.Warn("Sensores", $"fecho falhou — {ex.Message}"); }
            _computer = null;
            _visitor = null;
            _last = null;
        }
    }

    private static bool EnsureOpen()
    {
        if (_computer is not null) return true;
        if (_openFailed) return false;

        try
        {
            var c = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
            };

            c.Open();
            _visitor = new UpdateVisitor();

            // A primeira passagem só prepara os sensores; muitos deles só têm valor na segunda.
            c.Accept(_visitor);

            _computer = c;
            return true;
        }
        catch (Exception ex)
        {
            _openFailed = true;
            _openError = "Não foi possível iniciar a leitura de sensores nesta máquina.";
            Logger.Warn("Sensores", $"Open() falhou — {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static SensorSnapshot Build(Computer computer)
    {
        CpuSensors? cpu = null;
        string? board = null;
        var gpus = new List<GpuSensors>();
        var disks = new List<DiskSensors>();
        double? memUsed = null, memAvail = null;

        foreach (var hw in computer.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Motherboard:
                    board = Clean(hw.Name);
                    break;

                case HardwareType.Cpu:
                    cpu = ReadCpu(hw);
                    break;

                case HardwareType.Memory:
                    // Há dois nós de memória: "Total Memory" é a física, "Virtual Memory" não serve aqui.
                    if (hw.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) break;
                    memUsed ??= Value(hw, SensorType.Data, "Memory Used");
                    memAvail ??= Value(hw, SensorType.Data, "Memory Available");
                    break;

                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    gpus.Add(ReadGpu(hw));
                    break;

                case HardwareType.Storage:
                    disks.Add(ReadDisk(hw));
                    break;
            }
        }

        return new SensorSnapshot
        {
            Available = true,
            MotherboardName = board,
            Cpu = cpu,
            Gpus = gpus,
            Disks = disks,
            MemoryUsedGb = memUsed,
            MemoryAvailableGb = memAvail,
            LowLevelNote = cpu?.PackageTempC is null ? LowLevelReason() : null,
        };
    }

    private static CpuSensors ReadCpu(IHardware hw)
    {
        var cpu = new CpuSensors { Name = Clean(hw.Name) };

        // O nome do sensor muda entre fabricantes: Intel dá "CPU Package", AMD dá "Core (Tctl/Tdie)".
        cpu.PackageTempC = Positive(Value(hw, SensorType.Temperature, "CPU Package"))
                        ?? Positive(Value(hw, SensorType.Temperature, "Core (Tctl/Tdie)"))
                        ?? Positive(First(hw, SensorType.Temperature));

        cpu.PackagePowerW = Positive(Value(hw, SensorType.Power, "CPU Package"))
                         ?? Positive(Value(hw, SensorType.Power, "Package"));

        cpu.AverageClockMhz = Positive(Value(hw, SensorType.Clock, "Cores (Average)"))
                           ?? Positive(Value(hw, SensorType.Clock, "Core #1"));

        cpu.TotalLoadPercent = Value(hw, SensorType.Load, "CPU Total");

        foreach (var s in hw.Sensors
                            .Where(s => s.SensorType == SensorType.Load
                                     && s.Name.StartsWith("CPU Core #", StringComparison.Ordinal)
                                     && !s.Name.Contains("Thread", StringComparison.Ordinal))
                            .OrderBy(s => s.Index))
        {
            if (s.Value.HasValue) cpu.CoreLoads.Add(s.Value.Value);
        }

        return cpu;
    }

    private static GpuSensors ReadGpu(IHardware hw)
    {
        // Integradas aparecem com nomes como "AMD Radeon(TM) Graphics" ou "Intel(R) UHD Graphics";
        // o que as distingue de facto é não terem memória dedicada própria.
        var totalMb = Positive(Value(hw, SensorType.SmallData, "GPU Memory Total"))
                   ?? Positive(Value(hw, SensorType.SmallData, "D3D Dedicated Memory Total"));

        var dedicated = Positive(Value(hw, SensorType.SmallData, "D3D Dedicated Memory Total"));
        var shared = Positive(Value(hw, SensorType.SmallData, "D3D Shared Memory Total"));

        // Distinguir integrada de dedicada pelo nome é frágil. O que as separa de facto é a
        // proporção: numa integrada a memória reservada é uma fracção da partilhada (meio GB
        // contra 16), numa dedicada é da mesma ordem ou maior.
        var integrated = dedicated is null || (shared is > 0 && dedicated < shared / 4);

        var gpu = new GpuSensors
        {
            Name = Clean(hw.Name),
            IsIntegrated = integrated,
            MemorySharedTotalMb = shared,
            CoreTempC = Positive(Value(hw, SensorType.Temperature, "GPU Core")),
            HotSpotTempC = Positive(Value(hw, SensorType.Temperature, "GPU Hot Spot")),
            // Ao contrario das temperaturas, 0 rpm e uma leitura valida: as placas modernas
            // param a ventoinha quando estao frias. Nulo aqui significa "nao ha sensor".
            FanRpm = Value(hw, SensorType.Fan, "GPU Fan") ?? First(hw, SensorType.Fan),
            FanPercent = Value(hw, SensorType.Control, "GPU Fan"),
            CoreClockMhz = Positive(Value(hw, SensorType.Clock, "GPU Core")),
            MemoryClockMhz = Positive(Value(hw, SensorType.Clock, "GPU Memory")),
            PowerW = Positive(Value(hw, SensorType.Power, "GPU Package"))
                  ?? Positive(Value(hw, SensorType.Power, "GPU Core")),
            LoadPercent = Value(hw, SensorType.Load, "GPU Core")
                       ?? Value(hw, SensorType.Load, "D3D 3D"),
            MemoryTotalMb = totalMb,
            MemoryUsedMb = Positive(Value(hw, SensorType.SmallData, "GPU Memory Used"))
                        ?? Positive(Value(hw, SensorType.SmallData, "D3D Dedicated Memory Used")),
        };

        return gpu;
    }

    private static DiskSensors ReadDisk(IHardware hw) => new()
    {
        Name = Clean(hw.Name),
        TempC = Positive(Value(hw, SensorType.Temperature, "Composite Temperature"))
             ?? Positive(First(hw, SensorType.Temperature)),
        WarningTempC = Positive(Value(hw, SensorType.Temperature, "Warning Temperature")),
        CriticalTempC = Positive(Value(hw, SensorType.Temperature, "Critical Temperature")),
        LifePercent = Value(hw, SensorType.Level, "Life"),
        SparePercent = Value(hw, SensorType.Level, "Available Spare"),
        SpareThresholdPercent = Value(hw, SensorType.Level, "Available Spare Threshold"),
        UsedPercent = Value(hw, SensorType.Level, "Percentage Used"),
        PowerOnHours = Value(hw, SensorType.Factor, "Power On Hours"),
        PowerOnCount = Value(hw, SensorType.Factor, "Power On Count"),
        DataReadGb = Value(hw, SensorType.Data, "Data Read"),
        DataWrittenGb = Value(hw, SensorType.Data, "Data Written"),
        TotalSpaceGb = Value(hw, SensorType.Data, "Total Space"),
        FreeSpaceGb = Value(hw, SensorType.Data, "Free Space"),
    };

    /// <summary>
    /// Diz por que razão as leituras de baixo nível do processador não vieram. A causa mais comum
    /// no Windows 11 é a Integridade de Memória, que impede o carregamento do driver que as lê.
    /// </summary>
    private static string LowLevelReason()
    {
        if (!IsAdministrator())
            return "A temperatura do processador precisa que o NexusGuard corra como administrador.";

        if (MemoryIntegrityOn())
            return "A temperatura e a potência do processador vêm de um driver que o Windows bloqueia "
                 + "com a Integridade de Memória ligada. Não desligue essa protecção por causa disto — "
                 + "é uma defesa real contra drivers maliciosos, e aqui só custa um número.";

        return "Esta placa-mãe não expõe a temperatura do processador de forma legível.";
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static bool MemoryIntegrityOn()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            return k?.GetValue("Enabled") is int v && v == 1;
        }
        catch { return false; }
    }

    // ---------------- auxiliares de leitura ----------------

    private static double? Value(IHardware hw, SensorType type, string name)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType == type && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                return s.Value;
        }
        return null;
    }

    private static double? First(IHardware hw, SensorType type)
    {
        foreach (var s in hw.Sensors)
            if (s.SensorType == type && s.Value.HasValue) return s.Value;
        return null;
    }

    /// <summary>
    /// Zero, nestes sensores, quer quase sempre dizer "não li" e não "está a zero graus": nenhum
    /// processador em funcionamento está a 0 °C nem a 0 W. Tratar como ausente evita mostrar avarias
    /// que não existem.
    /// </summary>
    private static double? Positive(double? v) => v is > 0 ? v : null;

    private static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();

    /// <summary>A biblioteca exige este padrão de visitante para actualizar as leituras.</summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
