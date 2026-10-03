using System.IO;
using NexusGuard.Core;

namespace NexusGuard.Modules;

public sealed record JobResult(bool Success, string Title, string Message, NotifyLevel Level = NotifyLevel.Info);

/// <summary>
/// Os trabalhos que as tarefas agendadas executam. Correm sem janela, registam tudo no histórico
/// e devolvem o texto para a notificação.
/// </summary>
public static class Jobs
{
    public static async Task<JobResult> RunAsync(JobKind job, CancellationToken ct = default)
    {
        Logger.Info("Agendamento", $"A executar «{Cli.Describe(job)}» sem interface.");

        try
        {
            var result = job switch
            {
                JobKind.Scan => await ScanAsync(ct),
                JobKind.Clean => await CleanAsync(ct),
                JobKind.UpdateApps => await UpdateAppsAsync(ct),
                JobKind.Backup => await BackupAsync(ct),
                JobKind.CheckDrivers => await CheckDriversAsync(ct),
                JobKind.QuickScan => await QuickScanAsync(ct),
                _ => new JobResult(false, "NexusGuard", "Nada a fazer.")
            };

            if (result.Success) Logger.Ok("Agendamento", $"{result.Title}: {result.Message}");
            else Logger.Warn("Agendamento", $"{result.Title}: {result.Message}");

            return result;
        }
        catch (OperationCanceledException)
        {
            return new JobResult(false, Cli.Describe(job), "Interrompido.", NotifyLevel.Warning);
        }
        catch (Exception ex)
        {
            Logger.Error("Agendamento", $"«{Cli.Describe(job)}» falhou", ex);
            return new JobResult(false, Cli.Describe(job), ex.Message, NotifyLevel.Error);
        }
    }

    // ---------------- Análise ----------------

    private static async Task<JobResult> ScanAsync(CancellationToken ct)
    {
        var snapshot = new SystemSnapshot();

        var threats = await SecurityManager.GetThreatsAsync(ct);
        snapshot.Threats = threats.Count(t => t.ThreatStatusID is 1 or 6);

        var apps = AppUpdater.IsAvailable ? await AppUpdater.ListUpgradesAsync(null, ct) : new List<AppUpgrade>();
        snapshot.OutdatedApps = apps.Count;

        long recoverable = 0;

        foreach (var target in SafeTargets())
        {
            await DiskCleaner.ScanAsync(target, ct);
            if (target.Size > 0) recoverable += target.Size;
        }

        recoverable += DiskCleaner.RecycleBinSize();
        snapshot.RecoverableBytes = recoverable;
        snapshot.StartupItems = StartupManager.Load().Count(i => i.Enabled);

        var (drivers, _) = await UpdateAgent.SearchAsync(drivers: true, ct);
        snapshot.PendingDrivers = drivers.Count;
        snapshot.LastScan = DateTime.Now;

        // Uma analise concluida e o unico momento em que a pontuacao significa alguma
        // coisa. E guardada para o relatorio poder comparar com medicoes reais.
        ScoreLog.Record(snapshot.Score);

        History.Add("Agendamento", "Análise completa do PC", $"saúde {snapshot.Score}");

        var message = snapshot.AttentionCount == 0
            ? $"Saúde {snapshot.Score}/100 — está tudo em ordem."
            : $"Saúde {snapshot.Score}/100 · {snapshot.AttentionCount} ponto(s) a precisar de atenção.";

        return new JobResult(true, "Análise concluída", message,
            snapshot.AttentionCount == 0 ? NotifyLevel.Info : NotifyLevel.Warning);
    }

    // ---------------- Limpeza ----------------

    private static async Task<JobResult> CleanAsync(CancellationToken ct)
    {
        long freed = 0;
        var files = 0;

        foreach (var target in SafeTargets())
        {
            ct.ThrowIfCancellationRequested();

            await DiskCleaner.ScanAsync(target, ct);
            if (target.Size <= 0) continue;

            var outcome = await DiskCleaner.CleanAsync(target, null, ct);
            freed += outcome.BytesFreed;
            files += outcome.FilesDeleted;
        }

        // Em modo simular nada e removido, por isso a contagem de arquivos fica a zero:
        // o que interessa reportar e o espaco que seria libertado.
        if (Settings.Current.DryRun)
            return new JobResult(true, "Simulação concluída",
                freed > 0
                    ? $"{Fmt.Bytes(freed)} seriam liberados. Nada foi alterado."
                    : "Não havia nada para limpar.");

        if (files == 0)
            return new JobResult(true, "Limpeza concluída", "Não havia nada para limpar.");

        var where = Settings.Current.UseQuarantine
            ? $" Ficaram em quarentena {Settings.Current.QuarantineDays} dias."
            : "";

        return new JobResult(true, "Limpeza concluída",
            $"{Fmt.Bytes(freed)} em {Fmt.Count(files)} arquivos.{where}");
    }

    /// <summary>Categorias seguras que a tarefa agendada pode tratar sem perguntar nada.</summary>
    private static List<CleanTarget> SafeTargets() =>
        DiskCleaner.BuildTargets()
            .Where(t => t.Risk == CleanRisk.Safe)
            .Where(t => !t.NeedsAdmin || Fmt.IsAdmin)
            .ToList();

    // ---------------- Aplicativos ----------------

    private static async Task<JobResult> UpdateAppsAsync(CancellationToken ct)
    {
        if (!AppUpdater.IsAvailable)
            return new JobResult(false, "Atualização de aplicativos",
                "O winget não está instalado.", NotifyLevel.Warning);

        var pending = await AppUpdater.ListUpgradesAsync(null, ct);

        if (pending.Count == 0)
            return new JobResult(true, "Aplicativos", "Está tudo atualizado.");

        var updated = await AppUpdater.UpgradeManyAsync(pending, null, ct);

        return new JobResult(true, "Aplicativos atualizados",
            updated == pending.Count
                ? $"{updated} aplicativo(s) atualizados."
                : $"{updated} de {pending.Count} atualizados — veja o histórico.",
            updated == pending.Count ? NotifyLevel.Info : NotifyLevel.Warning);
    }

    // ---------------- Backup ----------------

    private static async Task<JobResult> BackupAsync(CancellationToken ct)
    {
        var settings = Settings.Current;

        if (string.IsNullOrWhiteSpace(settings.BackupDestination))
            return new JobResult(false, "Backup",
                "Nenhum destino guardado. Abra o NexusGuard, escolha o destino em Backup e faça uma cópia manual primeiro.",
                NotifyLevel.Warning);

        if (!Directory.Exists(settings.BackupDestination))
            return new JobResult(false, "Backup",
                $"O destino {settings.BackupDestination} não está acessível. O disco está ligado?",
                NotifyLevel.Warning);

        var sources = BackupManager.DefaultSources();
        var chosen = settings.BackupSources;

        if (chosen.Length > 0)
        {
            foreach (var source in sources)
                source.Selected = chosen.Contains(source.Path, StringComparer.OrdinalIgnoreCase);
        }

        if (sources.All(s => !s.Selected))
            return new JobResult(false, "Backup", "Nenhuma pasta selecionada nas configurações.",
                NotifyLevel.Warning);

        var result = await BackupManager.RunAsync(sources, settings.BackupDestination,
            BackupMode.Incremental, settings.BackupSkipCloudOnly, null, null, ct);

        return new JobResult(result.Success, result.Success ? "Backup concluído" : "Backup com erros",
            result.Success
                ? $"{Fmt.Count(result.Files)} arquivos · {Fmt.Bytes(result.Bytes)}."
                : result.Message,
            result.Success ? NotifyLevel.Info : NotifyLevel.Error);
    }

    // ---------------- Drivers ----------------

    private static async Task<JobResult> CheckDriversAsync(CancellationToken ct)
    {
        var (drivers, error) = await UpdateAgent.SearchAsync(drivers: true, ct);

        if (error is not null)
            return new JobResult(false, "Drivers", error, NotifyLevel.Warning);

        History.Add("Agendamento", "Verificação de drivers", $"{drivers.Count} pendentes");

        return drivers.Count == 0
            ? new JobResult(true, "Drivers", "Nenhum driver pendente.")
            : new JobResult(true, "Drivers pendentes",
                $"{drivers.Count} driver(s) à espera de instalação. Abra o NexusGuard para rever.",
                NotifyLevel.Warning);
    }

    // ---------------- Segurança ----------------

    private static async Task<JobResult> QuickScanAsync(CancellationToken ct)
    {
        await SecurityManager.UpdateSignaturesAsync(null, ct);

        var (ok, found) = await SecurityManager.ScanAsync(ScanKind.Quick, null, null, ct);

        History.Add("Agendamento", "Análise rápida do Defender", found > 0 ? "ameaças encontradas" : "sem ameaças");

        if (!ok)
            return new JobResult(false, "Verificação de segurança",
                "A análise não terminou corretamente.", NotifyLevel.Error);

        return found > 0
            ? new JobResult(true, "Ameaças encontradas",
                "O Defender tratou o que encontrou. Abra o NexusGuard para rever o histórico.",
                NotifyLevel.Warning)
            : new JobResult(true, "Verificação de segurança", "Nenhuma ameaça encontrada.");
    }
}
