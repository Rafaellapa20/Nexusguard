using System.Text;
using NexusGuard.Core;

namespace NexusGuard.Modules;

public sealed class WindowsUpdateItem : Observable
{
    public string UpdateId { get; set; } = "";
    public int RevisionNumber { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public long Size { get; set; }
    public bool IsDriver { get; set; }
    public string? DriverClass { get; set; }
    public string? DriverManufacturer { get; set; }
    public string? DriverModel { get; set; }
    public string? DriverVersionDate { get; set; }
    public bool IsMandatory { get; set; }
    public bool RebootRequired { get; set; }

    private bool _selected = true;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private string _status = "Disponível";
    public string Status { get => _status; set => Set(ref _status, value); }

    public string SizeText => Size > 0 ? Fmt.Bytes(Size) : "—";

    public string SubtitleText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(DriverManufacturer)) parts.Add(DriverManufacturer!);
            if (!string.IsNullOrWhiteSpace(DriverClass)) parts.Add(DriverClass!);
            if (!string.IsNullOrWhiteSpace(DriverVersionDate)) parts.Add(DriverVersionDate!);
            if (parts.Count == 0 && !string.IsNullOrWhiteSpace(Description))
                parts.Add(Description!.Length > 160 ? Description[..160] + "…" : Description!);
            return string.Join(" · ", parts);
        }
    }
}

public sealed class DeviceProblem
{
    public string? Name { get; set; }
    public string? Class { get; set; }
    public string? Status { get; set; }
    public string? InstanceId { get; set; }
    public int ProblemCode { get; set; }

    public string StatusText => Status switch
    {
        "Error" => "Com erro",
        "Degraded" => "Degradado",
        "Unknown" => "Desconhecido",
        _ => Status ?? "—"
    };

    public string Display => $"{Name} ({Class})";
}

public sealed record UpdateInstallResult(int Installed, int Failed, bool RebootRequired, string Message);

/// <summary>
/// Acesso ao agente do Windows Update (COM) para atualizações de drivers e do sistema,
/// e ao inventario de dispositivos com problemas.
/// </summary>
public static class UpdateAgent
{
    /// <summary>Script comum que devolve as atualizações pendentes em JSON.</summary>
    private static string SearchScript(bool drivers)
    {
        var criteria = drivers ? "IsInstalled=0 and Type='Driver'" : "IsInstalled=0 and Type='Software'";

        var sb = new StringBuilder();
        sb.AppendLine("$items = @()");
        sb.AppendLine("$err = ''");
        sb.AppendLine("try {");
        sb.AppendLine("  $session = New-Object -ComObject Microsoft.Update.Session");
        sb.AppendLine("  $searcher = $session.CreateUpdateSearcher()");
        sb.AppendLine("  $searcher.Online = $true");
        sb.AppendLine("  $res = $null");

        if (drivers)
        {
            // Os drivers so aparecem no serviço Microsoft Update; caso não esteja registado, cai no serviço por omissão.
            sb.AppendLine("  try {");
            sb.AppendLine("    $searcher.ServerSelection = 3");
            sb.AppendLine("    $searcher.ServiceID = '7971f918-a847-4430-9279-4a52d1efe18d'");
            sb.AppendLine($"    $res = $searcher.Search(\"{criteria}\")");
            sb.AppendLine("  } catch {");
            sb.AppendLine("    $searcher = $session.CreateUpdateSearcher()");
            sb.AppendLine("    $searcher.Online = $true");
            sb.AppendLine($"    $res = $searcher.Search(\"{criteria}\")");
            sb.AppendLine("  }");
        }
        else
        {
            sb.AppendLine($"  $res = $searcher.Search(\"{criteria}\")");
        }

        sb.AppendLine("  foreach ($u in $res.Updates) {");
        sb.AppendLine("    $dc = ''; $dm = ''; $dmo = ''; $dd = ''");
        sb.AppendLine("    try { $dc = [string]$u.DriverClass } catch {}");
        sb.AppendLine("    try { $dm = [string]$u.DriverManufacturer } catch {}");
        sb.AppendLine("    try { $dmo = [string]$u.DriverModel } catch {}");
        sb.AppendLine("    try { if ($u.DriverVerDate) { $dd = $u.DriverVerDate.ToString('yyyy-MM-dd') } } catch {}");
        sb.AppendLine("    $items += [ordered]@{");
        sb.AppendLine("      UpdateId = [string]$u.Identity.UpdateID");
        sb.AppendLine("      RevisionNumber = [int]$u.Identity.RevisionNumber");
        sb.AppendLine("      Title = [string]$u.Title");
        sb.AppendLine("      Description = [string]$u.Description");
        sb.AppendLine("      Size = [int64]$u.MaxDownloadSize");
        sb.AppendLine($"      IsDriver = ${(drivers ? "true" : "false")}");
        sb.AppendLine("      DriverClass = $dc");
        sb.AppendLine("      DriverManufacturer = $dm");
        sb.AppendLine("      DriverModel = $dmo");
        sb.AppendLine("      DriverVersionDate = $dd");
        sb.AppendLine("      IsMandatory = [bool]$u.IsMandatory");
        sb.AppendLine("      RebootRequired = [bool]$u.RebootRequired");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("} catch { $err = $_.Exception.Message }");
        sb.AppendLine("ConvertTo-Json -InputObject ([ordered]@{ Items = @($items); Error = $err }) -Depth 4 -Compress");

        return sb.ToString();
    }

    private sealed class SearchPayload
    {
        public List<WindowsUpdateItem>? Items { get; set; }
        public string? Error { get; set; }
    }

    public static async Task<(List<WindowsUpdateItem> items, string? error)> SearchAsync(bool drivers, CancellationToken ct = default)
    {
        var label = drivers ? "drivers" : "atualizações do Windows";
        Logger.Info("Atualizações", $"A procurar {label} no Windows Update (pode levar um minuto)...");

        var payload = await Shell.PowerShellJsonAsync<SearchPayload>(SearchScript(drivers), ct).ConfigureAwait(false);

        if (payload is null)
            return (new List<WindowsUpdateItem>(), "Não foi possível contactar o agente do Windows Update.");

        if (!string.IsNullOrWhiteSpace(payload.Error))
        {
            Logger.Warn("Atualizações", $"Windows Update: {payload.Error}");
            return (payload.Items ?? new List<WindowsUpdateItem>(), payload.Error);
        }

        var items = payload.Items ?? new List<WindowsUpdateItem>();
        Logger.Ok("Atualizações", items.Count == 0
            ? $"Nenhuma atualização de {label} pendente."
            : $"{items.Count} {label} disponíveis.");

        return (items, null);
    }

    /// <summary>Descarrega e instala as atualizações indicadas. Requer administrador.</summary>
    public static async Task<UpdateInstallResult> InstallAsync(IEnumerable<WindowsUpdateItem> updates, bool drivers,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        var list = updates.ToList();
        if (list.Count == 0) return new UpdateInstallResult(0, 0, false, "Nada selecionado.");

        if (!Fmt.IsAdmin)
        {
            const string msg = "Instalar atualizações requer privilégios de administrador.";
            onLine?.Invoke(msg);
            return new UpdateInstallResult(0, list.Count, false, msg);
        }

        // Apenas GUIDs passam para o script — nada vindo da pesquisa e interpolado sem validacao.
        var ids = list
            .Select(u => u.UpdateId)
            .Where(id => Guid.TryParse(id, out _))
            .Select(id => $"'{id}'")
            .ToList();

        if (ids.Count == 0)
            return new UpdateInstallResult(0, list.Count, false, "Identificadores de atualização inválidos.");

        var criteria = drivers ? "IsInstalled=0 and Type='Driver'" : "IsInstalled=0 and Type='Software'";

        var sb = new StringBuilder();
        sb.AppendLine($"$wanted = @({string.Join(',', ids)})");
        sb.AppendLine("$installed = 0; $failed = 0; $reboot = $false; $err = ''");
        sb.AppendLine("try {");
        sb.AppendLine("  $session = New-Object -ComObject Microsoft.Update.Session");
        sb.AppendLine("  $searcher = $session.CreateUpdateSearcher()");
        sb.AppendLine("  $searcher.Online = $true");

        if (drivers)
        {
            sb.AppendLine("  try {");
            sb.AppendLine("    $searcher.ServerSelection = 3");
            sb.AppendLine("    $searcher.ServiceID = '7971f918-a847-4430-9279-4a52d1efe18d'");
            sb.AppendLine($"    $res = $searcher.Search(\"{criteria}\")");
            sb.AppendLine("  } catch {");
            sb.AppendLine("    $searcher = $session.CreateUpdateSearcher()");
            sb.AppendLine("    $searcher.Online = $true");
            sb.AppendLine($"    $res = $searcher.Search(\"{criteria}\")");
            sb.AppendLine("  }");
        }
        else
        {
            sb.AppendLine($"  $res = $searcher.Search(\"{criteria}\")");
        }

        sb.AppendLine("  $toInstall = New-Object -ComObject Microsoft.Update.UpdateColl");
        sb.AppendLine("  foreach ($u in $res.Updates) {");
        sb.AppendLine("    if ($wanted -contains [string]$u.Identity.UpdateID) {");
        sb.AppendLine("      if (-not $u.EulaAccepted) { try { $u.AcceptEula() } catch {} }");
        sb.AppendLine("      Write-Output \"Preparando: $($u.Title)\"");
        sb.AppendLine("      $null = $toInstall.Add($u)");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("  if ($toInstall.Count -eq 0) { $err = 'As atualizações selecionadas já não estão disponíveis.' }");
        sb.AppendLine("  else {");
        sb.AppendLine("    Write-Output \"A transferir $($toInstall.Count) atualização(oes)...\"");
        sb.AppendLine("    $downloader = $session.CreateUpdateDownloader()");
        sb.AppendLine("    $downloader.Updates = $toInstall");
        sb.AppendLine("    $null = $downloader.Download()");
        sb.AppendLine("    $ready = New-Object -ComObject Microsoft.Update.UpdateColl");
        sb.AppendLine("    foreach ($u in $toInstall) { if ($u.IsDownloaded) { $null = $ready.Add($u) } }");
        sb.AppendLine("    if ($ready.Count -eq 0) { $err = 'Nenhuma atualização ficou baixada.' }");
        sb.AppendLine("    else {");
        sb.AppendLine("      Write-Output \"A instalar $($ready.Count) atualização(oes)...\"");
        sb.AppendLine("      $installer = $session.CreateUpdateInstaller()");
        sb.AppendLine("      $installer.Updates = $ready");
        sb.AppendLine("      $r = $installer.Install()");
        sb.AppendLine("      $reboot = [bool]$r.RebootRequired");
        sb.AppendLine("      for ($i = 0; $i -lt $ready.Count; $i++) {");
        sb.AppendLine("        $code = $r.GetUpdateResult($i).ResultCode");
        sb.AppendLine("        if ($code -eq 2 -or $code -eq 3) { $installed++; Write-Output \"OK: $($ready.Item($i).Title)\" }");
        sb.AppendLine("        else { $failed++; Write-Output \"FALHOU ($code): $($ready.Item($i).Title)\" }");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("} catch { $err = $_.Exception.Message }");
        sb.AppendLine("ConvertTo-Json -InputObject ([ordered]@{ Installed = $installed; Failed = $failed; Reboot = $reboot; Error = $err }) -Depth 3 -Compress");

        var res = await Shell.PowerShellAsync(sb.ToString(), line =>
        {
            // O JSON final não deve aparecer no registo visivel.
            if (!line.TrimStart().StartsWith('{')) onLine?.Invoke(line);
        }, ct).ConfigureAwait(false);

        var parsed = ParseInstallPayload(res.StdOut);

        var message = parsed.error is { Length: > 0 }
            ? parsed.error
            : $"{parsed.installed} instalada(s), {parsed.failed} falha(s)" + (parsed.reboot ? " · reinício necessário" : "");

        if (parsed.installed > 0) Logger.Ok("Atualizações", message);
        else Logger.Warn("Atualizações", message);

        return new UpdateInstallResult(parsed.installed, parsed.failed, parsed.reboot, message);
    }

    private static (int installed, int failed, bool reboot, string? error) ParseInstallPayload(string stdout)
    {
        try
        {
            var start = stdout.LastIndexOf('{');
            var end = stdout.LastIndexOf('}');
            if (start < 0 || end <= start) return (0, 0, false, "Resposta inesperada do agente de atualizações.");

            using var doc = System.Text.Json.JsonDocument.Parse(stdout[start..(end + 1)]);
            var root = doc.RootElement;

            var installed = root.TryGetProperty("Installed", out var i) ? i.GetInt32() : 0;
            var failed = root.TryGetProperty("Failed", out var f) ? f.GetInt32() : 0;
            var reboot = root.TryGetProperty("Reboot", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True;
            var error = root.TryGetProperty("Error", out var e) ? e.GetString() : null;

            return (installed, failed, reboot, string.IsNullOrWhiteSpace(error) ? null : error);
        }
        catch (Exception ex)
        {
            return (0, 0, false, $"Resposta inválida: {ex.Message}");
        }
    }

    /// <summary>Dispositivos com erro, degradados ou sem driver.</summary>
    public static async Task<List<DeviceProblem>> GetDeviceProblemsAsync(CancellationToken ct = default)
    {
        const string script = """
            $items = @()
            try {
                $devices = @(Get-PnpDevice -ErrorAction Stop | Where-Object { $_.Status -in @('Error','Degraded','Unknown') })
                foreach ($d in $devices) {
                    $items += [ordered]@{
                        Name        = [string]$d.FriendlyName
                        Class       = [string]$d.Class
                        Status      = [string]$d.Status
                        InstanceId  = [string]$d.InstanceId
                        ProblemCode = 0
                    }
                }
            } catch {}
            ConvertTo-Json -InputObject @($items) -Depth 3 -Compress
            """;

        var list = await Shell.PowerShellJsonAsync<List<DeviceProblem>>(script, ct).ConfigureAwait(false)
                   ?? new List<DeviceProblem>();

        if (list.Count > 0)
            Logger.Warn("Drivers", $"{list.Count} dispositivos com problemas detetados.");

        return list;
    }

    /// <summary>Abre a pagina do Windows Update nas Definições.</summary>
    public static void OpenWindowsUpdate() => Shell.OpenExternal("ms-settings:windowsupdate");

    public static void OpenDeviceManager() => Shell.OpenExternal("devmgmt.msc");
}
