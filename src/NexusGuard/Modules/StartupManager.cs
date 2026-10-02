using System.IO;
using Microsoft.Win32;
using NexusGuard.Core;

namespace NexusGuard.Modules;

public enum StartupLocation { CurrentUserRegistry, MachineRegistry, MachineRegistry32, UserFolder, CommonFolder }

public sealed class StartupItem : Observable
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public StartupLocation Location { get; init; }
    public string? FilePath { get; init; }

    private bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    public bool NeedsAdmin => Location is StartupLocation.MachineRegistry or StartupLocation.MachineRegistry32 or StartupLocation.CommonFolder;

    public string LocationText => Location switch
    {
        StartupLocation.CurrentUserRegistry => "Registro (usuário)",
        StartupLocation.MachineRegistry => "Registro (sistema)",
        StartupLocation.MachineRegistry32 => "Registro (sistema, 32 bits)",
        StartupLocation.UserFolder => "Pasta Iniciar (usuário)",
        _ => "Pasta Iniciar (todos)"
    };

    public string ScopeText => NeedsAdmin ? "Todos os usuários" : "Apenas este usuário";
}

/// <summary>
/// Le e alterna os programas de arranque, usando a mesma chave StartupApproved que o Gerenciador de Tarefas —
/// desativar é sempre reversível.
/// </summary>
public static class StartupManager
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public static List<StartupItem> Load()
    {
        var items = new List<StartupItem>();

        ReadRegistry(items, Registry.CurrentUser, RunPath, StartupLocation.CurrentUserRegistry);
        ReadRegistry(items, Registry.LocalMachine, RunPath, StartupLocation.MachineRegistry);
        ReadRegistry(items, Registry.LocalMachine, Run32Path, StartupLocation.MachineRegistry32);

        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupLocation.UserFolder);
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupLocation.CommonFolder);

        return items.OrderBy(i => i.Enabled ? 0 : 1).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void ReadRegistry(List<StartupItem> items, RegistryKey root, string path, StartupLocation loc)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null) return;

            var approvedPath = loc == StartupLocation.MachineRegistry32 ? ApprovedRun32 : ApprovedRun;
            using var approved = root.OpenSubKey(approvedPath);

            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var cmd = key.GetValue(name)?.ToString() ?? string.Empty;

                items.Add(new StartupItem
                {
                    Name = name,
                    Command = cmd,
                    Location = loc,
                    Enabled = IsApproved(approved, name)
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Arranque", $"Não foi possível ler {path}: {ex.Message}");
        }
    }

    private static void ReadFolder(List<StartupItem> items, string folder, StartupLocation loc)
    {
        try
        {
            if (!Directory.Exists(folder)) return;

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedFolder);

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                items.Add(new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = file,
                    FilePath = file,
                    Location = loc,
                    Enabled = IsApproved(approved, fileName)
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Arranque", $"Não foi possível ler {folder}: {ex.Message}");
        }
    }

    /// <summary>Sem registro em StartupApproved o item está ativo; o bit 0 do primeiro byte marca "desativado".</summary>
    private static bool IsApproved(RegistryKey? approved, string valueName)
    {
        if (approved?.GetValue(valueName) is not byte[] data || data.Length == 0) return true;
        return (data[0] & 1) == 0;
    }

    public static bool SetEnabled(StartupItem item, bool enable, out string message)
    {
        if (item.NeedsAdmin && !Fmt.IsAdmin)
        {
            message = $"'{item.Name}' pertence a todos os usuários — reinicie como administrador para o alterar.";
            return false;
        }

        try
        {
            var (root, approvedPath, valueName) = item.Location switch
            {
                StartupLocation.CurrentUserRegistry => (Registry.CurrentUser, ApprovedRun, item.Name),
                StartupLocation.MachineRegistry => (Registry.LocalMachine, ApprovedRun, item.Name),
                StartupLocation.MachineRegistry32 => (Registry.LocalMachine, ApprovedRun32, item.Name),
                StartupLocation.UserFolder => (Registry.CurrentUser, ApprovedFolder, Path.GetFileName(item.FilePath) ?? item.Name),
                _ => (Registry.CurrentUser, ApprovedFolder, Path.GetFileName(item.FilePath) ?? item.Name)
            };

            using var key = root.CreateSubKey(approvedPath, writable: true)
                            ?? throw new InvalidOperationException("chave StartupApproved indisponível");

            var data = new byte[12];
            data[0] = enable ? (byte)0x02 : (byte)0x03;

            if (!enable)
            {
                // Bytes 4..11 guardam o FILETIME em que o item foi desativado.
                var ft = BitConverter.GetBytes(DateTime.Now.ToFileTime());
                Array.Copy(ft, 0, data, 4, 8);
            }

            key.SetValue(valueName, data, RegistryValueKind.Binary);

            var previous = item.Enabled;
            item.Enabled = enable;
            item.Status = enable ? "Ativado" : "Desativado";

            message = enable
                ? $"«{item.Name}» volta a iniciar com o Windows."
                : $"«{item.Name}» deixa de iniciar com o Windows.";

            History.Add("Inicialização", message, enable ? "ativado" : "desativado",
                UndoKind.StartupItem,
                new Dictionary<string, string>
                {
                    ["name"] = item.Name,
                    ["previous"] = previous ? "1" : "0"
                });

            Logger.Ok("Inicialização", message);
            return true;
        }
        catch (Exception ex)
        {
            message = $"Não foi possível alterar '{item.Name}': {ex.Message}";
            Logger.Error("Arranque", message);
            return false;
        }
    }
}

public sealed class PowerPlan
{
    public required string Guid { get; init; }
    public required string Name { get; init; }
    public bool Active { get; set; }

    public string Display => Active ? $"{Name}  (ativo)" : Name;

    // A ComboBox mostra o resultado de ToString quando não há modelo de item aplicado.
    public override string ToString() => Display;
}

/// <summary>Leitura e troca de planos de energia atraves do powercfg.</summary>
public static class PowerManager
{
    private const string UltimateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private sealed class CimPlan
    {
        public string? InstanceID { get; set; }
        public string? ElementName { get; set; }
        public bool IsActive { get; set; }
    }

    public static async Task<List<PowerPlan>> ListAsync(CancellationToken ct = default)
    {
        // Via CIM os nomes chegam em Unicode correto; o powercfg escreve na página OEM.
        var cim = await Shell.PowerShellJsonAsync<List<CimPlan>>(
            "$items = @()\n" +
            "try {\n" +
            "  foreach ($p in @(Get-CimInstance -Namespace root\\cimv2\\power -ClassName Win32_PowerPlan -ErrorAction Stop)) {\n" +
            "    $items += [ordered]@{ InstanceID = [string]$p.InstanceID; ElementName = [string]$p.ElementName; IsActive = [bool]$p.IsActive }\n" +
            "  }\n" +
            "} catch {}\n" +
            "ConvertTo-Json -InputObject @($items) -Depth 3 -Compress", ct).ConfigureAwait(false);

        if (cim is { Count: > 0 })
        {
            var fromCim = new List<PowerPlan>();

            foreach (var p in cim)
            {
                var id = p.InstanceID ?? string.Empty;
                var start = id.IndexOf('{');
                var end = id.IndexOf('}');
                if (start < 0 || end <= start) continue;

                var guid = id.Substring(start + 1, end - start - 1);
                if (!Guid.TryParse(guid, out _)) continue;

                fromCim.Add(new PowerPlan
                {
                    Guid = guid,
                    Name = string.IsNullOrWhiteSpace(p.ElementName) ? guid : p.ElementName!,
                    Active = p.IsActive
                });
            }

            if (fromCim.Count > 0) return fromCim;
        }

        return await ListViaPowercfgAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Alternativa quando o fornecedor CIM de energia não responde.</summary>
    private static async Task<List<PowerPlan>> ListViaPowercfgAsync(CancellationToken ct)
    {
        var exe = Shell.Which("powercfg.exe") ?? "powercfg.exe";
        var r = await Shell.RunAsync(exe, "/list", null, ct).ConfigureAwait(false);
        var plans = new List<PowerPlan>();

        foreach (var line in r.All.Split('\n'))
        {
            // Formato: "Esquema de energia GUID: <guid>  (Nome) *"
            var idx = line.IndexOf(':');
            if (idx < 0) continue;

            var rest = line[(idx + 1)..].Trim();
            var space = rest.IndexOf(' ');
            if (space < 0) continue;

            var guid = rest[..space].Trim();
            if (!Guid.TryParse(guid, out _)) continue;

            var tail = rest[space..].Trim();
            var active = tail.EndsWith('*');
            if (active) tail = tail[..^1].Trim();

            var name = tail.Trim('(', ')', ' ');
            if (name.Length == 0) name = guid;

            plans.Add(new PowerPlan { Guid = guid, Name = name, Active = active });
        }

        return plans;
    }

    public static async Task<bool> ActivateAsync(string guid, CancellationToken ct = default)
    {
        var exe = Shell.Which("powercfg.exe") ?? "powercfg.exe";
        var r = await Shell.RunAsync(exe, $"/setactive {guid}", null, ct).ConfigureAwait(false);

        if (r.Success) Logger.Ok("Energia", $"Plano de energia ativado ({guid}).");
        else Logger.Warn("Energia", $"powercfg devolveu {r.ExitCode}: {r.All.Trim()}");

        return r.Success;
    }

    /// <summary>Cria o plano "Desempenho Máximo", oculto por omissão no Windows.</summary>
    public static async Task<bool> EnableUltimateAsync(CancellationToken ct = default)
    {
        var exe = Shell.Which("powercfg.exe") ?? "powercfg.exe";
        var r = await Shell.RunAsync(exe, $"-duplicatescheme {UltimateGuid}", null, ct).ConfigureAwait(false);

        if (r.Success) Logger.Ok("Energia", "Plano 'Desempenho Máximo' disponibilizado.");
        else Logger.Warn("Energia", "Este sistema não permite criar o plano 'Desempenho Máximo'.");

        return r.Success;
    }
}
