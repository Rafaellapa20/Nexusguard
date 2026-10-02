using System.IO;
using NexusGuard.Core;

namespace NexusGuard.Modules;

public sealed class DefenderStatus
{
    public bool AntivirusEnabled { get; set; }
    public bool RealTimeProtectionEnabled { get; set; }
    public bool AntispywareEnabled { get; set; }
    public bool BehaviorMonitorEnabled { get; set; }
    public bool IoavProtectionEnabled { get; set; }
    public bool TamperProtected { get; set; }
    public bool IsTamperProtected { get; set; }
    public string? AntivirusSignatureVersion { get; set; }
    public string? AMRunningMode { get; set; }
    public string? AntivirusSignatureLastUpdated { get; set; }
    public long AntivirusSignatureAge { get; set; }
    public long QuickScanAge { get; set; }
    public long FullScanAge { get; set; }
    public string? QuickScanEndTime { get; set; }
    public string? FullScanEndTime { get; set; }
    public bool Available { get; set; } = true;
    public string? Error { get; set; }

    public string SignatureText => string.IsNullOrWhiteSpace(AntivirusSignatureVersion) ? "—" : AntivirusSignatureVersion;

    public string SignatureAgeText => AntivirusSignatureAge switch
    {
        <= 0 => "atualizadas hoje",
        1 => "ha 1 dia",
        >= 9000 => "desconhecido",
        _ => $"ha {AntivirusSignatureAge} dias"
    };

    public string QuickScanText => QuickScanAge switch
    {
        <= 0 => "hoje",
        1 => "ha 1 dia",
        >= 9000 => "nunca",
        _ => $"ha {QuickScanAge} dias"
    };

    public string FullScanText => FullScanAge switch
    {
        <= 0 => "hoje",
        1 => "ha 1 dia",
        >= 9000 => "nunca",
        _ => $"ha {FullScanAge} dias"
    };

    public string ModeText => AMRunningMode switch
    {
        null or "" => "—",
        "Normal" => "Proteção ativa (modo normal)",
        "Passive Mode" => "Modo passivo (outro antivirus esta a proteger)",
        "SxS Passive Mode" => "Modo passivo lado-a-lado",
        "EDR Block Mode" => "Modo de bloqueio EDR",
        _ => AMRunningMode
    };
}

public sealed class ThreatRecord
{
    public string? ThreatName { get; set; }
    public string? Resources { get; set; }
    public int SeverityID { get; set; }
    public int ThreatStatusID { get; set; }
    public string? ThreatStatus { get; set; }
    public string? InitialDetectionTime { get; set; }

    public string SeverityText => SeverityID switch
    {
        1 => "Baixa",
        2 => "Moderada",
        4 => "Alta",
        5 => "Grave",
        _ => "—"
    };

    public string Name => string.IsNullOrWhiteSpace(ThreatName) ? "Ameaça desconhecida" : ThreatName;

    public string StatusText => string.IsNullOrWhiteSpace(ThreatStatus) ? MapStatus(ThreatStatusID) : ThreatStatus;

    private static string MapStatus(int id) => id switch
    {
        0 => "Desconhecido",
        1 => "Detetada",
        2 => "Limpa",
        3 => "Em quarentena",
        4 => "Removida",
        5 => "Permitida",
        6 => "Bloqueada",
        _ => $"Estado {id}"
    };

    public string When => string.IsNullOrWhiteSpace(InitialDetectionTime) ? "—" : InitialDetectionTime;
}

public sealed class FirewallProfile
{
    public string? Name { get; set; }
    public bool Enabled { get; set; }

    public string NameText => Name switch
    {
        "Domain" => "Domínio",
        "Private" => "Privada",
        "Public" => "Pública",
        _ => Name ?? "—"
    };

    public string StateText => Enabled ? "Ativa" : "Desativada";
}

public enum ScanKind { Quick = 1, Full = 2, Custom = 3, BootSector = 4 }

/// <summary>Integra o Microsoft Defender, a firewall e as ferramentas de integridade do sistema (SFC/DISM).</summary>
public static class SecurityManager
{
    /// <summary>MpCmdRun.exe vive numa pasta versionada em ProgramData; usa-se sempre a plataforma mais recente.</summary>
    public static string? FindMpCmdRun()
    {
        try
        {
            var platform = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows Defender", "Platform");

            if (Directory.Exists(platform))
            {
                var newest = Directory.GetDirectories(platform)
                    .Select(d => new DirectoryInfo(d))
                    .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d.FullName, "MpCmdRun.exe"))
                    .FirstOrDefault(File.Exists);

                if (newest is not null) return newest;
            }

            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Windows Defender", "MpCmdRun.exe");

            return File.Exists(legacy) ? legacy : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<DefenderStatus> GetStatusAsync(CancellationToken ct = default)
    {
        const string script = """
            $out = [ordered]@{ Available = $true }
            try {
                $s = Get-MpComputerStatus
                $out.Available                   = $true
                $out.AntivirusEnabled            = [bool]$s.AntivirusEnabled
                $out.RealTimeProtectionEnabled    = [bool]$s.RealTimeProtectionEnabled
                $out.AntispywareEnabled           = [bool]$s.AntispywareEnabled
                $out.BehaviorMonitorEnabled       = [bool]$s.BehaviorMonitorEnabled
                $out.IoavProtectionEnabled        = [bool]$s.IoavProtectionEnabled
                $out.IsTamperProtected            = [bool]$s.IsTamperProtected
                $out.AntivirusSignatureVersion    = [string]$s.AntivirusSignatureVersion
                $out.AMRunningMode                = [string]$s.AMRunningMode
                $out.AntivirusSignatureAge        = [int64]$s.AntivirusSignatureAge
                $out.QuickScanAge                 = [int64]$s.QuickScanAge
                $out.FullScanAge                  = [int64]$s.FullScanAge
                $out.AntivirusSignatureLastUpdated = if ($s.AntivirusSignatureLastUpdated) { $s.AntivirusSignatureLastUpdated.ToString('yyyy-MM-dd HH:mm') } else { '' }
                $out.QuickScanEndTime             = if ($s.QuickScanEndTime) { $s.QuickScanEndTime.ToString('yyyy-MM-dd HH:mm') } else { '' }
                $out.FullScanEndTime              = if ($s.FullScanEndTime) { $s.FullScanEndTime.ToString('yyyy-MM-dd HH:mm') } else { '' }
            } catch {
                $out.Available = $false
                $out.Error = $_.Exception.Message
            }
            $out | ConvertTo-Json -Depth 3 -Compress
            """;

        var status = await Shell.PowerShellJsonAsync<DefenderStatus>(script, ct).ConfigureAwait(false);

        if (status is null)
        {
            return new DefenderStatus
            {
                Available = false,
                Error = "Não foi possível consultar o Microsoft Defender neste sistema."
            };
        }

        status.TamperProtected = status.IsTamperProtected;
        return status;
    }

    public static async Task<List<ThreatRecord>> GetThreatsAsync(CancellationToken ct = default)
    {
        const string script = """
            $items = @()
            try {
                $threats = @(Get-MpThreatDetection | Sort-Object InitialDetectionTime -Descending | Select-Object -First 60)
                $names = @{}
                $sev = @{}
                try {
                    foreach ($t in @(Get-MpThreat)) {
                        $names[[string]$t.ThreatID] = [string]$t.ThreatName
                        $sev[[string]$t.ThreatID] = [int]$t.SeverityID
                    }
                } catch {}

                foreach ($d in $threats) {
                    $key = [string]$d.ThreatID
                    $name = $names[$key]
                    if (-not $name) { $name = "ThreatID $key" }
                    $items += [ordered]@{
                        ThreatName           = [string]$name
                        Resources            = (@($d.Resources) -join '; ')
                        SeverityID           = if ($sev.ContainsKey($key)) { [int]$sev[$key] } else { 0 }
                        ThreatStatusID       = [int]$d.ThreatStatusID
                        InitialDetectionTime = if ($d.InitialDetectionTime) { $d.InitialDetectionTime.ToString('yyyy-MM-dd HH:mm') } else { '' }
                    }
                }
            } catch {}
            ConvertTo-Json -InputObject @($items) -Depth 3 -Compress
            """;

        return await Shell.PowerShellJsonAsync<List<ThreatRecord>>(script, ct).ConfigureAwait(false) ?? new List<ThreatRecord>();
    }

    public static async Task<List<FirewallProfile>> GetFirewallAsync(CancellationToken ct = default)
    {
        const string script = """
            $items = @()
            try {
                foreach ($p in @(Get-NetFirewallProfile -ErrorAction Stop)) {
                    $items += [ordered]@{ Name = [string]$p.Name; Enabled = [bool]$p.Enabled }
                }
            } catch {}
            ConvertTo-Json -InputObject @($items) -Depth 3 -Compress
            """;

        return await Shell.PowerShellJsonAsync<List<FirewallProfile>>(script, ct).ConfigureAwait(false) ?? new List<FirewallProfile>();
    }

    /// <summary>Atualiza as definições de virus.</summary>
    public static async Task<bool> UpdateSignaturesAsync(Action<string>? onLine = null, CancellationToken ct = default)
    {
        Logger.Info("Segurança", "Atualizando as definições de virus...");

        var mp = FindMpCmdRun();
        if (mp is not null)
        {
            var r = await Shell.RunAsync(mp, "-SignatureUpdate", onLine, ct).ConfigureAwait(false);
            if (r.Success)
            {
                Logger.Ok("Segurança", "Configurações de virus atualizadas.");
                return true;
            }
            onLine?.Invoke($"MpCmdRun devolveu o código {r.ExitCode}; a tentar via PowerShell...");
        }

        var ps = await Shell.PowerShellAsync("Update-MpSignature -UpdateSource MicrosoftUpdateServer; 'ok'", onLine, ct)
            .ConfigureAwait(false);

        if (ps.Success) Logger.Ok("Segurança", "Configurações de virus atualizadas.");
        else Logger.Warn("Segurança", "Não foi possível atualizar as definições de virus.");

        return ps.Success;
    }

    /// <summary>Corre uma análise do Defender transmitindo a saída linha a linha.</summary>
    public static async Task<(bool ok, int threatsFound)> ScanAsync(ScanKind kind, string? customPath = null,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        var mp = FindMpCmdRun();
        if (mp is null)
        {
            onLine?.Invoke("MpCmdRun.exe não encontrado — o Microsoft Defender parece indisponível neste sistema.");
            Logger.Warn("Segurança", "MpCmdRun.exe não encontrado.");
            return (false, 0);
        }

        var args = kind == ScanKind.Custom
            ? $"-Scan -ScanType 3 -File \"{customPath}\""
            : $"-Scan -ScanType {(int)kind}";

        var label = kind switch
        {
            ScanKind.Quick => "análise rapida",
            ScanKind.Full => "análise completa",
            ScanKind.Custom => $"análise de '{customPath}'",
            _ => "análise do setor de arranque"
        };

        Logger.Info("Segurança", $"A iniciar {label}...");
        onLine?.Invoke($"=== A iniciar {label} ===");

        var found = 0;
        var r = await Shell.RunAsync(mp, args, line =>
        {
            if (line.Contains("found", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Threat", StringComparison.OrdinalIgnoreCase))
                found++;
            onLine?.Invoke(line);
        }, ct).ConfigureAwait(false);

        // MpCmdRun: 0 = sem ameaças, 2 = ameaças encontradas e tratadas.
        var clean = r.ExitCode == 0;
        var handled = r.ExitCode == 2;

        if (clean)
        {
            onLine?.Invoke("=== Concluído: nenhuma ameaça encontrada ===");
            Logger.Ok("Segurança", $"{label} concluída sem ameaças.");
        }
        else if (handled)
        {
            onLine?.Invoke("=== Concluído: ameaças encontradas e tratadas pelo Defender ===");
            Logger.Warn("Segurança", $"{label} encontrou ameaças; o Defender tratou-as.");
        }
        else
        {
            onLine?.Invoke($"=== MpCmdRun terminou com o código {r.ExitCode} ===");
            Logger.Warn("Segurança", $"{label} terminou com o código {r.ExitCode}.");
        }

        return (clean || handled, handled ? Math.Max(found, 1) : 0);
    }

    /// <summary>Remove ou coloca em quarentena tudo o que o Defender tenha detetado.</summary>
    public static async Task<bool> RemoveThreatsAsync(Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (!Fmt.IsAdmin)
        {
            onLine?.Invoke("Remover ameaças requer privilégios de administrador.");
            return false;
        }

        onLine?.Invoke("Removendo ameaças detetadas...");
        var r = await Shell.PowerShellAsync("Remove-MpThreat -ErrorAction Stop; 'ok'", onLine, ct).ConfigureAwait(false);

        if (r.Success) Logger.Ok("Segurança", "Pedido de remoção de ameaças enviado ao Defender.");
        else Logger.Warn("Segurança", "Nenhuma ameaça ativa para remover (ou o pedido falhou).");

        return r.Success;
    }

    /// <summary>Verificador de arquivos do sistema.</summary>
    public static async Task<bool> RunSfcAsync(Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (!Fmt.IsAdmin)
        {
            onLine?.Invoke("O sfc /scannow requer privilégios de administrador.");
            return false;
        }

        var exe = Shell.Which("sfc.exe") ?? "sfc.exe";
        onLine?.Invoke("=== sfc /scannow (pode levar 10-20 minutos) ===");
        Logger.Info("Segurança", "A correr sfc /scannow...");

        // O sfc escreve em UTF-16; a saída sai com caracteres nulos entre letras, que removemos.
        var r = await Shell.RunAsync(exe, "/scannow", line =>
        {
            var clean = line.Replace("\0", string.Empty).Trim();
            if (clean.Length > 0) onLine?.Invoke(clean);
        }, ct).ConfigureAwait(false);

        if (r.Success) Logger.Ok("Segurança", "sfc /scannow concluído.");
        else Logger.Warn("Segurança", $"sfc terminou com o código {r.ExitCode}.");

        return r.Success;
    }

    /// <summary>Repara a imagem do Windows (DISM /RestoreHealth).</summary>
    public static async Task<bool> RunDismRestoreAsync(Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (!Fmt.IsAdmin)
        {
            onLine?.Invoke("O DISM /RestoreHealth requer privilégios de administrador.");
            return false;
        }

        var exe = Shell.Which("Dism.exe") ?? "Dism.exe";
        onLine?.Invoke("=== DISM /Online /Cleanup-Image /RestoreHealth ===");
        Logger.Info("Segurança", "A correr DISM /RestoreHealth...");

        var r = await Shell.RunAsync(exe, "/Online /Cleanup-Image /RestoreHealth", onLine, ct).ConfigureAwait(false);

        if (r.Success) Logger.Ok("Segurança", "DISM /RestoreHealth concluído.");
        else Logger.Warn("Segurança", $"DISM terminou com o código {r.ExitCode}.");

        return r.Success;
    }
}
