namespace NexusGuard.Core;

/// <summary>Trabalho que o NexusGuard sabe executar sem abrir a janela.</summary>
public enum JobKind
{
    None,
    Scan,
    Clean,
    UpdateApps,
    Backup,
    CheckDrivers,
    QuickScan
}

/// <summary>
/// Argumentos aceites pelo executável. São usados pelas tarefas agendadas e pelo atalho de
/// arranque — sem isto, uma tarefa das 03:00 limitava-se a abrir a janela.
/// </summary>
public static class Cli
{
    public const string TrayFlag = "--tray";

    private static readonly Dictionary<string, JobKind> Jobs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--scan"] = JobKind.Scan,
        ["--clean"] = JobKind.Clean,
        ["--update-apps"] = JobKind.UpdateApps,
        ["--backup"] = JobKind.Backup,
        ["--check-drivers"] = JobKind.CheckDrivers,
        ["--quick-scan"] = JobKind.QuickScan
    };

    public static JobKind ParseJob(IEnumerable<string> args)
    {
        foreach (var arg in args)
            if (Jobs.TryGetValue(arg.Trim(), out var job))
                return job;

        return JobKind.None;
    }

    public static bool StartInTray(IEnumerable<string> args) =>
        args.Any(a => a.Trim().Equals(TrayFlag, StringComparison.OrdinalIgnoreCase));

    public static string Describe(JobKind job) => job switch
    {
        JobKind.Scan => "Análise completa do PC",
        JobKind.Clean => "Limpeza de arquivos temporários",
        JobKind.UpdateApps => "Atualização de aplicativos",
        JobKind.Backup => "Backup incremental",
        JobKind.CheckDrivers => "Verificação de drivers",
        JobKind.QuickScan => "Verificação de segurança",
        _ => "NexusGuard"
    };

    /// <summary>Texto mostrado quando alguém corre o executável com --help.</summary>
    public static string Usage =>
        """
        NexusGuard — manutenção do Windows

        Sem argumentos abre a janela normal.

          --tray            arranca minimizado na bandeja do sistema
          --scan            análise completa, sem alterar nada
          --clean           limpeza das categorias seguras
          --update-apps     atualiza os aplicativos via winget
          --backup          backup incremental para o destino guardado
          --check-drivers   procura drivers pendentes no Windows Update
          --quick-scan      análise rápida do Microsoft Defender
          --help            mostra esta ajuda

        Os trabalhos correm sem interface, registam tudo no histórico e notificam no fim.
        São estes os argumentos usados pelas tarefas criadas em Agendamento.
        """;
}
