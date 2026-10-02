using Microsoft.Win32;
using NexusGuard.Core;

namespace NexusGuard.Modules;

public enum PerformanceMode { Balanced, Work, Gaming }

public sealed class Tweak : Observable
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Gain { get; init; }
    public bool NeedsAdmin { get; init; }

    private bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }
}

/// <summary>
/// Ajustes de desempenho que o Windows já suporta — plano de energia, efeitos visuais, serviços
/// e manutenção. Nenhum deles mexe em áreas não documentadas do sistema.
/// </summary>
public static class Tweaks
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string BackgroundKey = @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";
    private const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

    /// <summary>Serviços que podem passar a arranque manual sem consequências para a maioria.</summary>
    private static readonly string[] OptionalServices =
    {
        "DiagTrack", "dmwappushservice", "RetailDemo", "MapsBroker", "WalletService"
    };

    public static List<Tweak> Build() => new()
    {
        new Tweak
        {
            Key = "power",
            Name = "Plano de energia Alto Desempenho",
            Description = "Mantém o processador em frequência alta em vez de poupar energia.",
            Gain = "resposta imediata"
        },
        new Tweak
        {
            Key = "transparency",
            Name = "Desativar transparência",
            Description = "Remove o efeito de vidro do menu Iniciar e das janelas.",
            Gain = "menos carga na GPU"
        },
        new Tweak
        {
            Key = "services",
            Name = "Serviços não essenciais em manual",
            Description = "Telemetria, demonstração de loja, mapas offline e carteira passam a arrancar só quando precisos.",
            Gain = "arranque mais rápido",
            NeedsAdmin = true
        },
        new Tweak
        {
            Key = "background",
            Name = "Limitar aplicativos em segundo plano",
            Description = "Impede que aplicativos da Store corram sem estarem abertos.",
            Gain = "menos CPU em repouso"
        },
        new Tweak
        {
            Key = "sysmain",
            Name = "SysMain (SuperFetch)",
            Description = "Útil em discos mecânicos; em SSD costuma só gastar memória.",
            Gain = "menos uso de disco",
            NeedsAdmin = true
        },
        new Tweak
        {
            Key = "maintenance",
            Name = "Reparo SFC/DISM semanal",
            Description = "Agenda uma verificação de integridade do Windows aos domingos às 05:30.",
            Gain = "sistema saudável",
            NeedsAdmin = true
        }
    };

    public static async Task ReadStateAsync(IEnumerable<Tweak> tweaks, CancellationToken ct = default)
    {
        var plans = await PowerManager.ListAsync(ct).ConfigureAwait(true);
        var active = plans.FirstOrDefault(p => p.Active);

        var services = await ReadServicesAsync(ct).ConfigureAwait(true);

        foreach (var tweak in tweaks)
        {
            try
            {
                tweak.Status = "";

                switch (tweak.Key)
                {
                    case "power":
                        tweak.Enabled = active is not null &&
                                        active.Guid.Equals(HighPerformanceGuid, StringComparison.OrdinalIgnoreCase);
                        tweak.Status = active is null ? "" : active.Name;
                        break;

                    case "transparency":
                        tweak.Enabled = ReadDword(Registry.CurrentUser, PersonalizeKey, "EnableTransparency") == 0;
                        break;

                    case "services":
                        tweak.Enabled = OptionalServices.All(s =>
                            !services.TryGetValue(s, out var start) ||
                            !start.Equals("Automatic", StringComparison.OrdinalIgnoreCase));
                        break;

                    case "background":
                        tweak.Enabled = ReadDword(Registry.CurrentUser, BackgroundKey, "GlobalUserDisabled") == 1;
                        break;

                    case "sysmain":
                        tweak.Enabled = services.TryGetValue("SysMain", out var sysmain) &&
                                        !sysmain.Equals("Automatic", StringComparison.OrdinalIgnoreCase);
                        tweak.Status = services.TryGetValue("SysMain", out var s2) ? s2 : "";
                        break;

                    case "maintenance":
                        tweak.Enabled = await TaskExistsAsync("NexusGuard\\Manutencao", ct).ConfigureAwait(true);
                        break;
                }
            }
            catch (Exception ex)
            {
                tweak.Status = "indisponível";
                Logger.Warn("Performance", $"{tweak.Name}: {ex.Message}");
            }
        }
    }

    public static async Task<(bool ok, string message)> ApplyAsync(Tweak tweak, bool enable,
        CancellationToken ct = default)
    {
        if (tweak.NeedsAdmin && !Fmt.IsAdmin)
            return (false, $"«{tweak.Name}» precisa de privilégios de administrador.");

        if (Settings.Current.DryRun)
        {
            tweak.Enabled = enable;
            return (true, $"[simular] «{tweak.Name}» ficaria {(enable ? "ativo" : "desativado")}.");
        }

        try
        {
            switch (tweak.Key)
            {
                case "power":
                    await PowerManager.ActivateAsync(enable ? HighPerformanceGuid : BalancedGuid, ct)
                        .ConfigureAwait(true);
                    break;

                case "transparency":
                    WriteDword(Registry.CurrentUser, PersonalizeKey, "EnableTransparency", enable ? 0 : 1);
                    break;

                case "services":
                    foreach (var service in OptionalServices)
                        await SetServiceStartAsync(service, enable ? "demand" : "auto", ct).ConfigureAwait(true);
                    break;

                case "background":
                    WriteDword(Registry.CurrentUser, BackgroundKey, "GlobalUserDisabled", enable ? 1 : 0);
                    break;

                case "sysmain":
                    await SetServiceStartAsync("SysMain", enable ? "demand" : "auto", ct).ConfigureAwait(true);
                    break;

                case "maintenance":
                    if (!await SetMaintenanceTaskAsync(enable, ct).ConfigureAwait(true))
                        return (false, "Não foi possível alterar a tarefa de manutenção.");
                    break;
            }

            tweak.Enabled = enable;
            tweak.Status = enable ? "Aplicado" : "Padrão do Windows";

            History.Add("Performance", $"{tweak.Name}: {(enable ? "aplicado" : "revertido")}", tweak.Gain);
            Logger.Ok("Performance", $"{tweak.Name} {(enable ? "aplicado" : "revertido")}.");

            return (true, $"«{tweak.Name}» {(enable ? "aplicado" : "revertido")}.");
        }
        catch (Exception ex)
        {
            Logger.Error("Performance", $"Falha ao aplicar «{tweak.Name}»", ex);
            return (false, ex.Message);
        }
    }

    /// <summary>Aplica o conjunto de ajustes associado a um modo.</summary>
    public static async Task<string> ApplyModeAsync(PerformanceMode mode, IEnumerable<Tweak> tweaks,
        CancellationToken ct = default)
    {
        var wanted = mode switch
        {
            PerformanceMode.Gaming => new[] { "power", "transparency", "background", "services" },
            PerformanceMode.Work => new[] { "power", "background" },
            _ => Array.Empty<string>()
        };

        var applied = 0;

        foreach (var tweak in tweaks)
        {
            var enable = wanted.Contains(tweak.Key);
            if (tweak.Enabled == enable) continue;
            if (tweak.NeedsAdmin && !Fmt.IsAdmin) continue;

            var (ok, _) = await ApplyAsync(tweak, enable, ct).ConfigureAwait(true);
            if (ok) applied++;
        }

        if (mode == PerformanceMode.Balanced)
            await PowerManager.ActivateAsync(BalancedGuid, ct).ConfigureAwait(true);

        return mode switch
        {
            PerformanceMode.Gaming => $"Modo Jogos: {applied} ajuste(s) aplicados.",
            PerformanceMode.Work => $"Modo Trabalho: {applied} ajuste(s) aplicados.",
            _ => "Modo Equilibrado: o Windows gere tudo com as definições padrão."
        };
    }

    // ---------------- Auxiliares ----------------

    private static int ReadDword(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) is int value ? value : -1;
    }

    private static void WriteDword(RegistryKey root, string path, string name, int value)
    {
        using var key = root.CreateSubKey(path, writable: true)
                        ?? throw new InvalidOperationException($"não foi possível abrir {path}");
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    private sealed class ServiceInfo
    {
        public string? Name { get; set; }
        public string? StartType { get; set; }
    }

    private static async Task<Dictionary<string, string>> ReadServicesAsync(CancellationToken ct)
    {
        const string script = """
            $items = @()
            try {
                foreach ($s in @(Get-Service -ErrorAction Stop)) {
                    $items += [ordered]@{ Name = [string]$s.Name; StartType = [string]$s.StartType }
                }
            } catch {}
            ConvertTo-Json -InputObject @($items) -Depth 3 -Compress
            """;

        var list = await Shell.PowerShellJsonAsync<List<ServiceInfo>>(script, ct).ConfigureAwait(true)
                   ?? new List<ServiceInfo>();

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var service in list)
            if (!string.IsNullOrWhiteSpace(service.Name))
                map[service.Name!] = service.StartType ?? "";

        return map;
    }

    private static async Task SetServiceStartAsync(string service, string start, CancellationToken ct)
    {
        var sc = Shell.Which("sc.exe") ?? "sc.exe";
        await Shell.RunAsync(sc, $"config \"{service}\" start= {start}", null, ct).ConfigureAwait(true);

        if (start == "demand")
            await Shell.RunAsync(sc, $"stop \"{service}\"", null, ct).ConfigureAwait(true);
    }

    private static async Task<bool> TaskExistsAsync(string taskName, CancellationToken ct)
    {
        var schtasks = Shell.Which("schtasks.exe") ?? "schtasks.exe";
        var r = await Shell.RunAsync(schtasks, $"/Query /TN \"{taskName}\"", null, ct).ConfigureAwait(true);
        return r.Success;
    }

    private static async Task<bool> SetMaintenanceTaskAsync(bool enable, CancellationToken ct)
    {
        var schtasks = Shell.Which("schtasks.exe") ?? "schtasks.exe";
        const string name = "NexusGuard\\Manutencao";

        if (!enable)
        {
            await Shell.RunAsync(schtasks, $"/Delete /F /TN \"{name}\"", null, ct).ConfigureAwait(true);
            return true;
        }

        var command = "cmd /c sfc /scannow && Dism /Online /Cleanup-Image /RestoreHealth";
        var args = $"/Create /F /TN \"{name}\" /SC WEEKLY /D SUN /ST 05:30 /RL HIGHEST /TR \"{command}\"";

        var r = await Shell.RunAsync(schtasks, args, null, ct).ConfigureAwait(true);
        return r.Success;
    }
}
