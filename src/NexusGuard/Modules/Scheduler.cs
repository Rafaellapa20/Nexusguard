using NexusGuard.Core;

namespace NexusGuard.Modules;

public sealed class ScheduledJob : Observable
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Scope { get; init; }
    public required string WhenText { get; init; }

    /// <summary>Argumentos do schtasks que definem a periodicidade.</summary>
    public required string ScheduleArgs { get; init; }

    /// <summary>Argumento passado ao NexusGuard quando a tarefa dispara.</summary>
    public required string Action { get; init; }

    public string TaskName => $"NexusGuard\\{Key}";

    private bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    private string _next = "—";
    public string Next { get => _next; set => Set(ref _next, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }
}

/// <summary>
/// Tarefas periódicas registadas no Agendador de Tarefas do Windows, para correrem mesmo
/// sem a app aberta.
/// </summary>
public static class Scheduler
{
    private static string SchTasks => Shell.Which("schtasks.exe") ?? "schtasks.exe";

    public static List<ScheduledJob> Build() => new()
    {
        new ScheduledJob
        {
            Key = "AnaliseCompleta",
            Name = "Análise completa do PC",
            Scope = "Todos os módulos",
            WhenText = "Todos os dias às 03:00",
            ScheduleArgs = "/SC DAILY /ST 03:00",
            Action = "--scan"
        },
        new ScheduledJob
        {
            Key = "AtualizarApps",
            Name = "Atualizar aplicativos",
            Scope = "winget",
            WhenText = "Terças e sextas às 03:30",
            ScheduleArgs = "/SC WEEKLY /D TUE,FRI /ST 03:30",
            Action = "--update-apps"
        },
        new ScheduledJob
        {
            Key = "Limpeza",
            Name = "Limpeza de arquivos temporários",
            Scope = "Categorias seguras",
            WhenText = "Domingos às 04:00",
            ScheduleArgs = "/SC WEEKLY /D SUN /ST 04:00",
            Action = "--clean"
        },
        new ScheduledJob
        {
            Key = "Backup",
            Name = "Backup incremental",
            Scope = "Pastas configuradas",
            WhenText = "Todos os dias às 23:00",
            ScheduleArgs = "/SC DAILY /ST 23:00",
            Action = "--backup"
        },
        new ScheduledJob
        {
            Key = "Drivers",
            Name = "Verificar drivers",
            Scope = "Windows Update",
            WhenText = "Todo dia 1 às 05:00",
            ScheduleArgs = "/SC MONTHLY /D 1 /ST 05:00",
            Action = "--check-drivers"
        },
        new ScheduledJob
        {
            Key = "Seguranca",
            Name = "Verificação de segurança",
            Scope = "Microsoft Defender",
            WhenText = "Sábados às 02:00",
            ScheduleArgs = "/SC WEEKLY /D SAT /ST 02:00",
            Action = "--quick-scan"
        }
    };

    private sealed class TaskInfo
    {
        public string? TaskName { get; set; }
        public string? NextRunTime { get; set; }
        public string? Status { get; set; }
    }

    public static async Task RefreshAsync(IEnumerable<ScheduledJob> jobs, CancellationToken ct = default)
    {
        // O Get-ScheduledTask devolve objetos em Unicode; o schtasks /query escreve na página OEM.
        const string script = """
            $items = @()
            try {
                foreach ($t in @(Get-ScheduledTask -TaskPath '\NexusGuard\' -ErrorAction Stop)) {
                    $info = $null
                    try { $info = Get-ScheduledTaskInfo -InputObject $t -ErrorAction Stop } catch {}
                    $items += [ordered]@{
                        TaskName    = [string]$t.TaskName
                        Status      = [string]$t.State
                        NextRunTime = if ($info -and $info.NextRunTime) { $info.NextRunTime.ToString('dd/MM HH:mm') } else { '' }
                    }
                }
            } catch {}
            ConvertTo-Json -InputObject @($items) -Depth 3 -Compress
            """;

        var tasks = await Shell.PowerShellJsonAsync<List<TaskInfo>>(script, ct).ConfigureAwait(true)
                    ?? new List<TaskInfo>();

        foreach (var job in jobs)
        {
            var match = tasks.FirstOrDefault(t =>
                string.Equals(t.TaskName, job.Key, StringComparison.OrdinalIgnoreCase));

            job.Enabled = match is not null && !string.Equals(match.Status, "Disabled", StringComparison.OrdinalIgnoreCase);
            job.Next = string.IsNullOrWhiteSpace(match?.NextRunTime) ? "—" : match!.NextRunTime!;
            job.Status = match is null ? "" : match.Status ?? "";
        }
    }

    public static async Task<(bool ok, string message)> SetEnabledAsync(ScheduledJob job, bool enable,
        CancellationToken ct = default)
    {
        if (!Fmt.IsAdmin)
            return (false, "Criar ou alterar tarefas agendadas exige privilégios de administrador.");

        if (Settings.Current.DryRun)
        {
            job.Enabled = enable;
            return (true, $"[simular] «{job.Name}» ficaria {(enable ? "ativa" : "desativada")}.");
        }

        var exe = Environment.ProcessPath;

        if (enable && string.IsNullOrWhiteSpace(exe))
            return (false, "Não foi possível determinar o caminho do NexusGuard.");

        try
        {
            if (enable)
            {
                var args = $"/Create /F /TN \"{job.TaskName}\" {job.ScheduleArgs} " +
                           $"/TR \"\\\"{exe}\\\" {job.Action}\" /RL HIGHEST";

                var r = await Shell.RunAsync(SchTasks, args, null, ct).ConfigureAwait(true);

                if (!r.Success)
                    return (false, $"O Agendador de Tarefas devolveu {r.ExitCode}.");

                job.Enabled = true;
                History.Add("Agendamento", $"Tarefa «{job.Name}» criada", job.WhenText);
                Logger.Ok("Agendamento", $"Tarefa «{job.Name}» criada ({job.WhenText}).");
                await RefreshAsync(new[] { job }, ct).ConfigureAwait(true);
                return (true, $"«{job.Name}» agendada: {job.WhenText.ToLower(Fmt.Pt)}.");
            }

            var del = await Shell.RunAsync(SchTasks, $"/Delete /F /TN \"{job.TaskName}\"", null, ct)
                .ConfigureAwait(true);

            job.Enabled = false;
            job.Next = "—";

            if (del.Success)
            {
                History.Add("Agendamento", $"Tarefa «{job.Name}» removida", "");
                Logger.Ok("Agendamento", $"Tarefa «{job.Name}» removida.");
            }

            return (true, $"«{job.Name}» já não está agendada.");
        }
        catch (Exception ex)
        {
            Logger.Error("Agendamento", $"Falha ao alterar «{job.Name}»", ex);
            return (false, ex.Message);
        }
    }

    public static async Task<bool> RunNowAsync(ScheduledJob job, CancellationToken ct = default)
    {
        var r = await Shell.RunAsync(SchTasks, $"/Run /TN \"{job.TaskName}\"", null, ct).ConfigureAwait(true);
        return r.Success;
    }

    public static void OpenTaskScheduler() => Shell.OpenExternal("taskschd.msc");
}
