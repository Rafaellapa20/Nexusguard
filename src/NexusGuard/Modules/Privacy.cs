using System.IO;
using Microsoft.Win32;
using NexusGuard.Core;

namespace NexusGuard.Modules;

/// <summary>Uma definição de privacidade: uma chave de registo documentada, sempre reversível.</summary>
public sealed class PrivacyToggle : Observable
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Chave onde a definição vive, em formato "HKCU\Software\...".</summary>
    public required string KeyPath { get; init; }

    public required string ValueName { get; init; }

    /// <summary>Valor que aplica a proteção (toggle ligado).</summary>
    public required int ProtectedValue { get; init; }

    /// <summary>Valor por omissão do Windows (toggle desligado).</summary>
    public required int DefaultValue { get; init; }

    public bool NeedsAdmin { get; init; }

    /// <summary>Ações especiais que não são uma simples chave (ex.: ficheiro hosts).</summary>
    public Func<bool, bool>? CustomApply { get; init; }

    public Func<bool>? CustomRead { get; init; }

    private bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    public string Scope => NeedsAdmin ? "Todo o computador" : "Apenas este utilizador";
}

/// <summary>
/// Definições de privacidade do Windows. Cada uma corresponde a uma chave de registo conhecida;
/// nada é escondido e tudo pode ser revertido pelo Histórico.
/// </summary>
public static class Privacy
{
    private const string HostsMarker = "# NexusGuard - bloqueio de telemetria de terceiros";

    private static readonly string[] TelemetryHosts =
    {
        "vortex.data.microsoft.com",
        "vortex-win.data.microsoft.com",
        "telecommand.telemetry.microsoft.com",
        "oca.telemetry.microsoft.com",
        "sqm.telemetry.microsoft.com",
        "watson.telemetry.microsoft.com",
        "settings-sandbox.data.microsoft.com",
        "telemetry.appex.bing.net",
        "telemetry.urs.microsoft.com"
    };

    public static List<PrivacyToggle> Build() => new()
    {
        new PrivacyToggle
        {
            Name = "Telemetria do Windows (nível básico)",
            Description = "Limita a recolha de dados de diagnóstico ao mínimo que a Microsoft permite.",
            KeyPath = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
            ValueName = "AllowTelemetry",
            ProtectedValue = 0,
            DefaultValue = 3,
            NeedsAdmin = true
        },
        new PrivacyToggle
        {
            Name = "ID de publicidade",
            Description = "Impede que os programas usem um identificador único para anúncios dirigidos.",
            KeyPath = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
            ValueName = "Enabled",
            ProtectedValue = 0,
            DefaultValue = 1
        },
        new PrivacyToggle
        {
            Name = "Cortana e pesquisa por voz",
            Description = "Desliga a assistente de voz e o envio de consultas de pesquisa para a nuvem.",
            KeyPath = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Search",
            ValueName = "BingSearchEnabled",
            ProtectedValue = 0,
            DefaultValue = 1
        },
        new PrivacyToggle
        {
            Name = "Localização em segundo plano",
            Description = "Impede que programas leiam a sua localização quando não estão em uso.",
            KeyPath = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location",
            ValueName = "Value",
            ProtectedValue = 0,
            DefaultValue = 1
        },
        new PrivacyToggle
        {
            Name = "Sugestões, dicas e anúncios",
            Description = "Remove as sugestões do menu Iniciar, da ecrã de bloqueio e das notificações.",
            KeyPath = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
            ValueName = "SilentInstalledAppsEnabled",
            ProtectedValue = 0,
            DefaultValue = 1
        },
        new PrivacyToggle
        {
            Name = "Histórico de atividades",
            Description = "Deixa de guardar e enviar o histórico de atividades dos programas que usa.",
            KeyPath = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System",
            ValueName = "PublishUserActivities",
            ProtectedValue = 0,
            DefaultValue = 1,
            NeedsAdmin = true
        },
        new PrivacyToggle
        {
            Name = "Bloquear telemetria de terceiros (hosts)",
            Description = "Acrescenta ao ficheiro hosts os servidores de telemetria mais conhecidos.",
            KeyPath = "(ficheiro hosts)",
            ValueName = "",
            ProtectedValue = 1,
            DefaultValue = 0,
            NeedsAdmin = true,
            CustomRead = HostsBlocked,
            CustomApply = SetHostsBlock
        },
        new PrivacyToggle
        {
            Name = "Câmera e microfone por programa",
            Description = "Abre as permissões do Windows para revisar que programas têm acesso.",
            KeyPath = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam",
            ValueName = "Value",
            ProtectedValue = 0,
            DefaultValue = 1
        }
    };

    public static void ReadState(IEnumerable<PrivacyToggle> toggles)
    {
        foreach (var toggle in toggles)
        {
            try
            {
                if (toggle.CustomRead is not null)
                {
                    toggle.Enabled = toggle.CustomRead();
                    toggle.Status = "";
                    continue;
                }

                var (root, sub) = Split(toggle.KeyPath);
                using var key = root?.OpenSubKey(sub);
                var raw = key?.GetValue(toggle.ValueName);

                toggle.Enabled = raw switch
                {
                    int i => i == toggle.ProtectedValue,
                    string s when int.TryParse(s, out var v) => v == toggle.ProtectedValue,
                    _ => false
                };

                toggle.Status = "";
            }
            catch (Exception ex)
            {
                toggle.Status = "indisponível";
                Logger.Warn("Privacidade", $"{toggle.Name}: {ex.Message}");
            }
        }
    }

    public static bool Apply(PrivacyToggle toggle, bool enable, out string message)
    {
        if (toggle.NeedsAdmin && !Fmt.IsAdmin)
        {
            message = $"«{toggle.Name}» é uma definição de todo o computador e precisa de administrador.";
            return false;
        }

        if (Settings.Current.DryRun)
        {
            message = $"[simular] «{toggle.Name}» ficaria {(enable ? "protegido" : "no padrão do Windows")}.";
            toggle.Enabled = enable;
            return true;
        }

        try
        {
            var previous = toggle.Enabled;

            if (toggle.CustomApply is not null)
            {
                if (!toggle.CustomApply(enable))
                {
                    message = $"Não foi possível aplicar «{toggle.Name}».";
                    return false;
                }
            }
            else
            {
                var (root, sub) = Split(toggle.KeyPath);
                if (root is null) throw new InvalidOperationException("ramo do registo desconhecido");

                using var key = root.CreateSubKey(sub, writable: true)
                                ?? throw new InvalidOperationException("não foi possível abrir a chave");

                key.SetValue(toggle.ValueName, enable ? toggle.ProtectedValue : toggle.DefaultValue,
                    RegistryValueKind.DWord);
            }

            toggle.Enabled = enable;
            toggle.Status = enable ? "Protegido" : "Padrão do Windows";

            History.Add("Privacidade", $"{toggle.Name}: {(enable ? "protegido" : "reposto no padrão")}",
                enable ? "ligado" : "desligado", UndoKind.RegistryValue,
                new Dictionary<string, string>
                {
                    ["key"] = toggle.KeyPath,
                    ["value"] = toggle.ValueName,
                    ["previous"] = previous ? "1" : "0",
                    ["name"] = toggle.Name
                });

            message = enable
                ? $"«{toggle.Name}» está agora protegido."
                : $"«{toggle.Name}» voltou ao padrão do Windows.";

            Logger.Ok("Privacidade", message);
            return true;
        }
        catch (Exception ex)
        {
            message = $"Não foi possível alterar «{toggle.Name}»: {ex.Message}";
            Logger.Error("Privacidade", message);
            return false;
        }
    }

    private static (RegistryKey? root, string sub) Split(string path)
    {
        var parts = path.Split('\\', 2);
        if (parts.Length < 2) return (null, string.Empty);

        RegistryKey? root = parts[0].ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
            _ => null
        };

        return (root, parts[1]);
    }

    // ---------------- Ficheiro hosts ----------------

    private static string HostsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    private static bool HostsBlocked()
    {
        try { return File.Exists(HostsFile) && File.ReadAllText(HostsFile).Contains(HostsMarker); }
        catch { return false; }
    }

    private static bool SetHostsBlock(bool enable)
    {
        try
        {
            var lines = File.Exists(HostsFile)
                ? File.ReadAllLines(HostsFile).ToList()
                : new List<string>();

            // Remove sempre o bloco antigo antes de decidir o que escrever.
            var start = lines.FindIndex(l => l.Contains(HostsMarker));

            if (start >= 0)
            {
                var end = start;
                while (end < lines.Count &&
                       (lines[end].Contains(HostsMarker) || lines[end].TrimStart().StartsWith("0.0.0.0")))
                    end++;

                lines.RemoveRange(start, end - start);
            }

            if (enable)
            {
                lines.Add(HostsMarker);
                foreach (var host in TelemetryHosts) lines.Add($"0.0.0.0 {host}");
            }

            File.WriteAllLines(HostsFile, lines);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Privacidade", "Não foi possível alterar o ficheiro hosts", ex);
            return false;
        }
    }

    public static void OpenWindowsPrivacy() => Shell.OpenExternal("ms-settings:privacy");
}
