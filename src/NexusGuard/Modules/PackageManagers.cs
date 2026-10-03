using System.IO;
using System.Text;
using NexusGuard.Core;

namespace NexusGuard.Modules;

/// <summary>Um gestor de pacotes encontrado na máquina.</summary>
public sealed record PackageManagerInfo(string Key, string Name, string Exe, string? Version)
{
    public string Label => string.IsNullOrWhiteSpace(Version) ? Name : $"{Name} {Version}";
}

/// <summary>O que uma listagem de atualizações devolveu, separando "nada a fazer" de "não li".</summary>
public sealed record UpgradeListing(List<AppUpgrade> Items, string? Problem)
{
    public bool Failed => Problem is not null;

    public static UpgradeListing Ok(List<AppUpgrade> items) => new(items, null);
    public static UpgradeListing Error(string problem) => new(new List<AppUpgrade>(), problem);
}

/// <summary>
/// Gestores de pacotes além do winget: Scoop e Chocolatey.
///
/// O NexusGuard <b>detecta</b> os que já estão instalados e usa-os; não os instala. Instalar um
/// gestor de pacotes muda o PATH do sistema e, no caso do Chocolatey, cria uma árvore em
/// <c>C:\ProgramData\chocolatey</c> e passa a interceptar instalações — é uma decisão de quem
/// administra a máquina, não de uma ferramenta de manutenção a correr uma limpeza.
///
/// Os comandos de cada gestor seguem os que o UniGetUI usa (MIT; ver TERCEIROS.md).
/// </summary>
public static class PackageManagers
{
    /// <summary>Estes gestores escrevem em UTF-8 quando a saída é redirecionada.</summary>
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public const string Scoop = "scoop";
    public const string Chocolatey = "choco";

    /// <summary>
    /// Procura os gestores instalados. O Scoop vive na pasta do utilizador e nem sempre está no
    /// PATH do processo elevado, por isso também é procurado no caminho onde se instala.
    /// </summary>
    public static async Task<List<PackageManagerInfo>> DetectAsync(CancellationToken ct = default)
    {
        var found = new List<PackageManagerInfo>();

        if (FindScoop() is { } scoopExe)
        {
            var version = await ReadVersionAsync(scoopExe, "--version", ct).ConfigureAwait(false);
            found.Add(new PackageManagerInfo(Scoop, "Scoop", scoopExe, version));
        }

        if (FindChocolatey() is { } chocoExe)
        {
            var version = await ReadVersionAsync(chocoExe, "--version", ct).ConfigureAwait(false);
            found.Add(new PackageManagerInfo(Chocolatey, "Chocolatey", chocoExe, version));
        }

        if (found.Count > 0)
            Logger.Info("Aplicativos", $"Gestores adicionais: {string.Join(", ", found.Select(f => f.Label))}.");

        return found;
    }

    private static string? FindScoop()
    {
        foreach (var name in new[] { "scoop.cmd", "scoop.exe", "scoop.ps1" })
            if (Shell.Which(name) is { } path) return path;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var shim = Path.Combine(home, "scoop", "shims", "scoop.cmd");
        return File.Exists(shim) ? shim : null;
    }

    private static string? FindChocolatey()
    {
        if (Shell.Which("choco.exe") is { } path) return path;

        var programData = Environment.GetEnvironmentVariable("ProgramData");
        if (string.IsNullOrWhiteSpace(programData)) return null;

        var fixedPath = Path.Combine(programData, "chocolatey", "bin", "choco.exe");
        return File.Exists(fixedPath) ? fixedPath : null;
    }

    private static async Task<string?> ReadVersionAsync(string exe, string args, CancellationToken ct)
    {
        try
        {
            var r = await Shell.RunAsync(exe, args, null, ct, null, Utf8).ConfigureAwait(false);

            // O Chocolatey escreve só o número; o Scoop escreve várias linhas com o commit das buckets.
            var line = r.StdOut.Split('\n')
                               .Select(l => l.Trim())
                               .FirstOrDefault(l => l.Length > 0 && char.IsDigit(l[0]));

            return line;
        }
        catch (Exception ex)
        {
            Logger.Warn("Aplicativos", $"não foi possível ler a versão de {exe} — {ex.Message}");
            return null;
        }
    }

    // ---------------- listagem de atualizações ----------------

    public static Task<UpgradeListing> ListUpgradesAsync(PackageManagerInfo manager,
        CancellationToken ct = default) => manager.Key switch
    {
        Chocolatey => ListChocolateyAsync(manager, ct),
        Scoop => ListScoopAsync(manager, ct),
        _ => Task.FromResult(UpgradeListing.Error($"Gestor desconhecido: {manager.Key}."))
    };

    /// <summary>
    /// O <c>--limit-output</c> do Chocolatey produz <c>id|instalada|disponível|fixada</c>, um
    /// formato delimitado e documentado — ao contrário da tabela normal, que é para ler com olhos.
    /// </summary>
    private static async Task<UpgradeListing> ListChocolateyAsync(PackageManagerInfo manager,
        CancellationToken ct)
    {
        var r = await Shell.RunAsync(manager.Exe, "outdated --limit-output --no-progress",
            null, ct, null, Utf8).ConfigureAwait(false);

        // 0 = nada desatualizado, 2 = há pacotes desatualizados. Qualquer outro código é problema.
        if (r.ExitCode is not (0 or 2))
            return UpgradeListing.Error($"O Chocolatey devolveu o código {r.ExitCode}.");

        return UpgradeListing.Ok(ParseChocolateyOutdated(r.StdOut));
    }

    /// <summary>
    /// Interpreta a saída de <c>choco outdated --limit-output</c>, que é
    /// <c>id|instalada|disponível|fixada</c> por linha. Está separada da execução para poder ser
    /// verificada com saídas reais sem ter o Chocolatey instalado.
    /// </summary>
    public static List<AppUpgrade> ParseChocolateyOutdated(string output)
    {
        var items = new List<AppUpgrade>();

        foreach (var line in output.Replace("\r", "").Split('\n'))
        {
            var parts = line.Trim().Split('|');
            if (parts.Length < 3) continue;

            var id = parts[0].Trim();
            var current = parts[1].Trim();
            var available = parts[2].Trim();

            // A propria saida comeca por uma linha de cabecalho que tambem tem barras verticais
            // ("Output is package name | current version | ..."). O que a distingue de um pacote e
            // que um id nunca tem espacos e uma versao comeca sempre por digito.
            if (id.Length == 0 || available.Length == 0) continue;
            if (!char.IsLetterOrDigit(id[0]) || id.Contains(' ')) continue;
            if (!char.IsDigit(available[0])) continue;

            // Pacotes fixados não devem ser atualizados: foi uma decisão deliberada de quem os fixou.
            if (parts.Length >= 4 && parts[3].Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                continue;

            items.Add(new AppUpgrade
            {
                Name = id,
                Id = id,
                CurrentVersion = current,
                AvailableVersion = available,
                Source = "chocolatey"
            });
        }

        return items;
    }

    /// <summary>
    /// O <c>scoop status</c> escreve uma tabela de texto sem formato garantido entre versões. Esta
    /// leitura exige a linha de tracinhos que separa o cabeçalho dos dados: sem ela, devolve um
    /// problema em vez de uma lista vazia — dizer "está tudo atualizado" quando na verdade não se
    /// conseguiu ler é o pior resultado possível.
    /// </summary>
    private static async Task<UpgradeListing> ListScoopAsync(PackageManagerInfo manager,
        CancellationToken ct)
    {
        var r = await Shell.RunAsync(manager.Exe, "status", null, ct, null, Utf8).ConfigureAwait(false);

        if (r.ExitCode != 0)
            return UpgradeListing.Error($"O Scoop devolveu o código {r.ExitCode}.");

        return ParseScoopStatus(r.StdOut);
    }

    /// <summary>
    /// Interpreta a saída de <c>scoop status</c>, que é uma tabela de texto sem formato garantido
    /// entre versões. Exige a linha de tracinhos que separa o cabeçalho dos dados: sem ela devolve
    /// um problema e não uma lista vazia, porque dizer "está tudo atualizado" quando na verdade não
    /// se conseguiu ler é o pior resultado possível.
    /// </summary>
    public static UpgradeListing ParseScoopStatus(string output)
    {
        var lines = output.Replace("\r", "").Split('\n');

        // A linha de tracinhos é a chave da leitura: numa tabela do PowerShell as colunas são
        // separadas por um único espaço, e nomes como "Installed Version" têm espaços dentro. Só a
        // posição e largura de cada grupo de tracinhos diz onde cada coluna começa e acaba.
        var separator = Array.FindIndex(lines, IsSeparator);

        if (separator < 1)
        {
            // Sem desatualizados, o Scoop escreve uma frase em vez da tabela. Essa frase é
            // localizada, por isso o que se confirma é a ausência da tabela, havendo texto.
            var hasText = lines.Any(l => l.Trim().Length > 0);
            return hasText
                ? UpgradeListing.Ok(new List<AppUpgrade>())
                : UpgradeListing.Error("Não foi possível interpretar a saída do Scoop.");
        }

        var spans = Spans(lines[separator]);
        var header = spans.Select(sp => Slice(lines[separator - 1], sp)).ToList();

        var nameAt = header.FindIndex(c => c.Equals("Name", StringComparison.OrdinalIgnoreCase));
        var installedAt = header.FindIndex(c => c.Contains("Installed", StringComparison.OrdinalIgnoreCase));
        var latestAt = header.FindIndex(c => c.Contains("Latest", StringComparison.OrdinalIgnoreCase));

        if (nameAt < 0 || latestAt < 0)
            return UpgradeListing.Error("O cabeçalho do Scoop não tem as colunas esperadas.");

        var items = new List<AppUpgrade>();

        foreach (var line in lines.Skip(separator + 1))
        {
            if (line.Trim().Length == 0) continue;

            var name = Slice(line, spans[nameAt]);
            var latest = Slice(line, spans[latestAt]);
            if (name.Length == 0 || latest.Length == 0) continue;

            var installed = installedAt >= 0 ? Slice(line, spans[installedAt]) : "";

            // Uma linha em que a versão instalada e a mais recente coincidem não é atualização.
            if (installed.Length > 0 && installed == latest) continue;

            items.Add(new AppUpgrade
            {
                Name = name,
                Id = name,
                CurrentVersion = installed,
                AvailableVersion = latest,
                Source = "scoop"
            });
        }

        return UpgradeListing.Ok(items);
    }

    /// <summary>Uma linha só de tracinhos e espaços, com pelo menos um grupo de tracinhos.</summary>
    private static bool IsSeparator(string line) =>
        line.Length > 0 && line.Contains('-') && line.All(c => c is '-' or ' ');

    /// <summary>Posição e largura de cada grupo de tracinhos, ou seja, de cada coluna.</summary>
    private static List<(int Start, int Length)> Spans(string separator)
    {
        var spans = new List<(int, int)>();
        var i = 0;

        while (i < separator.Length)
        {
            if (separator[i] != '-') { i++; continue; }

            var start = i;
            while (i < separator.Length && separator[i] == '-') i++;
            spans.Add((start, i - start));
        }

        return spans;
    }

    /// <summary>
    /// Corta uma célula pela posição da coluna. A última coluna costuma ser mais larga do que os
    /// tracinhos anunciam, e as linhas vêm cortadas à direita, por isso os limites são aparados.
    /// </summary>
    private static string Slice(string line, (int Start, int Length) span)
    {
        if (span.Start >= line.Length) return "";

        var end = Math.Min(line.Length, span.Start + span.Length);
        var cell = line[span.Start..end];

        // Um valor mais comprido do que a coluna continua para além dela até ao próximo espaço.
        if (end == span.Start + span.Length)
        {
            var rest = line.Length > end ? line[end..] : "";
            var extra = rest.TakeWhile(c => c != ' ').Count();
            if (extra > 0) cell += rest[..extra];
        }

        return cell.Trim();
    }

    /// <summary>Divide uma linha de tabela por duas ou mais espaços seguidos.</summary>
    private static List<string> SplitColumns(string line) =>
        line.Split("  ", StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .ToList();

    // ---------------- instalar e atualizar ----------------

    public static Task<bool> UpgradeAsync(PackageManagerInfo manager, string id,
        Action<string>? onLine = null, CancellationToken ct = default) =>
        RunPackageAsync(manager, upgrade: true, id, onLine, ct);

    public static Task<bool> InstallAsync(PackageManagerInfo manager, string id,
        Action<string>? onLine = null, CancellationToken ct = default) =>
        RunPackageAsync(manager, upgrade: false, id, onLine, ct);

    private static async Task<bool> RunPackageAsync(PackageManagerInfo manager, bool upgrade, string id,
        Action<string>? onLine, CancellationToken ct)
    {
        var args = manager.Key switch
        {
            Chocolatey => $"{(upgrade ? "upgrade" : "install")} {id} -y --no-progress",
            Scoop => $"{(upgrade ? "update" : "install")} {id}",
            _ => null
        };

        if (args is null)
        {
            onLine?.Invoke($"Gestor desconhecido: {manager.Key}.");
            return false;
        }

        var verb = upgrade ? "Atualizando" : "Instalando";
        onLine?.Invoke($"{verb} {id} pelo {manager.Name}…");

        var r = await Shell.RunAsync(manager.Exe, args, onLine, ct, null, Utf8).ConfigureAwait(false);

        // O Chocolatey usa 1641 e 3010 para "correu bem, precisa de reiniciar".
        var ok = r.ExitCode is 0 or 1641 or 3010;

        if (ok)
        {
            Logger.Ok("Aplicativos", $"{id} ({manager.Name}) concluído.");
            if (r.ExitCode is 1641 or 3010)
                onLine?.Invoke($"{id}: concluído, mas pede reinício do Windows.");
        }
        else
        {
            Logger.Warn("Aplicativos", $"{id} ({manager.Name}) devolveu {r.ExitCode}.");
            onLine?.Invoke($"{id}: {manager.Name} devolveu o código {r.ExitCode}.");
        }

        return ok;
    }
}
