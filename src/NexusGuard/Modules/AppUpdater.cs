using System.Text;
using System.Text.RegularExpressions;
using NexusGuard.Core;

namespace NexusGuard.Modules;

public sealed class AppUpgrade : Observable
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public required string CurrentVersion { get; init; }
    public required string AvailableVersion { get; init; }
    public string Source { get; init; } = "winget";

    private bool _selected = true;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private string _status = "Atualização disponível";
    public string Status { get => _status; set => Set(ref _status, value); }

    private bool _done;
    public bool Done { get => _done; set => Set(ref _done, value); }

    public string VersionText => $"{CurrentText}  →  {AvailableVersion}";

    /// <summary>O winget escreve "Unknown" quando não consegue ler a versão instalada.</summary>
    private string CurrentText =>
        CurrentVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? "desconhecida" : CurrentVersion;

    /// <summary>
    /// Verdadeiro quando a atualização falhou por falta de permissões. A vista usa isto para
    /// oferecer elevação em vez de mostrar um código de erro.
    /// </summary>
    public bool NeedsElevation { get; set; }

    /// <summary>Um ID truncado pelo winget não pode ser usado para atualizar com segurança.</summary>
    public bool IdIsReliable => !Id.Contains('…') && !Id.EndsWith("...", StringComparison.Ordinal);
}

/// <summary>
/// Atualização de programas via winget. A tabela do winget é localizada, por isso cada linha e lida
/// da direita para a esquerda: só o nome pode conter espaços.
/// </summary>
public static class AppUpdater
{
    private static readonly Regex Whitespace = new(@"\s{1,}", RegexOptions.Compiled);

    /// <summary>O winget escreve em UTF-8 quando a saída é redirecionada.</summary>
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>
    /// Traduz os códigos de saída do winget. Mostrar "-2147024891" a quem está a usar não é
    /// informação — é um número que obriga a ir procurar à internet o que correu mal.
    /// </summary>
    public static string Describe(int exitCode) => exitCode switch
    {
        0 => "concluído",
        1641 or 3010 => "concluído, mas pede reinício do Windows",

        unchecked((int)0x80070005) => "acesso negado — é preciso administrador",
        5 or 1260 => "acesso negado — é preciso administrador",

        unchecked((int)0x8A150061) => "já estava instalado",
        unchecked((int)0x8A15002B) => "não há atualização aplicável",
        unchecked((int)0x80073D06) => "já existe uma versão igual ou mais recente",
        unchecked((int)0x80070652) or 1618 => "outra instalação está a decorrer — espere que termine",
        1602 => "cancelado",

        _ => $"o winget devolveu o código {exitCode}"
    };

    /// <summary>Se este código significa que faltou elevação.</summary>
    public static bool IsAccessDenied(int exitCode) =>
        exitCode == unchecked((int)0x80070005) || exitCode == 5 || exitCode == 1260;

    public static string? WingetPath => Shell.Which("winget.exe");

    public static bool IsAvailable => WingetPath is not null;

    public static async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        var exe = WingetPath;
        if (exe is null) return null;

        var r = await Shell.RunAsync(exe, "--version", null, ct, null, Utf8).ConfigureAwait(false);
        return r.Success ? r.StdOut.Trim() : null;
    }

    public static async Task<List<AppUpgrade>> ListUpgradesAsync(Action<string>? onLine = null, CancellationToken ct = default)
    {
        var exe = WingetPath;
        if (exe is null)
        {
            Logger.Warn("Programas", "O winget (Programa de Instalação de Aplicações) não esta instalado.");
            return new List<AppUpgrade>();
        }

        Logger.Info("Programas", "A procurar atualizações de programas...");

        var r = await Shell.RunAsync(exe,
            "upgrade --include-unknown --accept-source-agreements --disable-interactivity",
            null, ct, null, Utf8).ConfigureAwait(false);

        var list = Parse(r.All);

        foreach (var item in list.Where(i => !i.IdIsReliable))
            item.Status = "ID truncado — atualize pelo nome";

        await AddOtherManagersAsync(list, onLine, ct).ConfigureAwait(false);

        Logger.Ok("Programas", list.Count == 0
            ? "Todos os programas estão atualizados."
            : $"{list.Count} programas com atualização disponível.");

        return list;
    }

    /// <summary>Gestores detectados nesta sessão, para não os procurar a cada operação.</summary>
    private static List<PackageManagerInfo>? _others;

    private static async Task<List<PackageManagerInfo>> OtherManagersAsync(CancellationToken ct) =>
        _others ??= await PackageManagers.DetectAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Junta as atualizações do Scoop e do Chocolatey, quando estão instalados. Uma listagem que
    /// falha é dita em voz alta: o pior resultado seria o utilizador ver a lista do winget e
    /// concluir que está tudo em ordem quando um dos outros gestores nem foi lido.
    /// </summary>
    private static async Task AddOtherManagersAsync(List<AppUpgrade> list, Action<string>? onLine,
        CancellationToken ct)
    {
        foreach (var manager in await OtherManagersAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var listing = await PackageManagers.ListUpgradesAsync(manager, ct).ConfigureAwait(false);

            if (listing.Failed)
            {
                Logger.Warn("Programas", $"{manager.Name}: {listing.Problem}");
                onLine?.Invoke($"{manager.Name}: {listing.Problem} As atualizações dele não entram nesta lista.");
                continue;
            }

            // O mesmo programa pode estar nos dois gestores; o winget manda, porque é o do sistema.
            foreach (var item in listing.Items)
            {
                if (list.Any(e => e.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(item);
            }

            onLine?.Invoke(listing.Items.Count == 0
                ? $"{manager.Label}: nada a atualizar."
                : $"{manager.Label}: {listing.Items.Count} com atualização.");
        }
    }

    /// <summary>Le a tabela do winget sem depender dos nomes (localizados) das colunas.</summary>
    internal static List<AppUpgrade> Parse(string output)
    {
        var result = new List<AppUpgrade>();
        var inTable = false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Replace("\r", string.Empty).TrimEnd();

            if (line.TrimStart().StartsWith("---", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }

            if (!inTable) continue;

            if (string.IsNullOrWhiteSpace(line))
            {
                inTable = false;
                continue;
            }

            var tokens = Whitespace.Split(line.Trim());
            if (tokens.Length < 4) continue;

            // [Nome...] [Id] [VersaoAtual] [VersaoDisponivel] ([Fonte])
            var hasSource = tokens.Length >= 5 && IsSourceToken(tokens[^1]);

            var source = hasSource ? tokens[^1] : string.Empty;
            var end = hasSource ? tokens.Length - 1 : tokens.Length;

            if (end < 4) continue;

            var available = tokens[end - 1];
            var current = tokens[end - 2];
            var id = tokens[end - 3];
            var name = string.Join(' ', tokens[..(end - 3)]);

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
            if (!LooksLikeVersion(available)) continue;

            result.Add(new AppUpgrade
            {
                Name = name,
                Id = id,
                CurrentVersion = current,
                AvailableVersion = available,
                Source = string.IsNullOrEmpty(source) ? "winget" : source
            });
        }

        return result;
    }

    private static bool IsSourceToken(string token) =>
        token.Equals("winget", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("msstore", StringComparison.OrdinalIgnoreCase);

    /// <summary>Aceita numeros de versão e tambem rótulos como "Unknown"/"Desconhecido".</summary>
    private static bool LooksLikeVersion(string token) =>
        token.Length > 0 && (char.IsDigit(token[0]) || char.IsLetter(token[0]));

    /// <summary>Linhas que são apenas barras de progresso não interessam ao registo.</summary>
    private static bool IsNoise(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;

        var trimmed = line.Trim();
        if (trimmed.All(c => c is '█' or '▒' or '-' or '\\' or '|' or '/' or '.' or ' ')) return true;
        return trimmed.Contains('█') || trimmed.Contains('▒');
    }

    public static async Task<bool> UpgradeAsync(AppUpgrade app, Action<string>? onLine = null, CancellationToken ct = default)
    {
        // Cada entrada sabe de que gestor veio, e tem de voltar pelo mesmo caminho: pedir ao winget
        // para atualizar um pacote do Chocolatey não falha com erro, simplesmente não faz nada.
        if (!app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase))
            return await UpgradeByManagerAsync(app, onLine, ct).ConfigureAwait(false);

        var exe = WingetPath;
        if (exe is null) return false;

        app.Status = "A atualizar...";
        onLine?.Invoke($"=== {app.Name} ({app.CurrentVersion} → {app.AvailableVersion}) ===");

        var selector = app.IdIsReliable
            ? $"--id \"{app.Id}\" --exact"
            : $"--name \"{app.Name}\"";

        var args = $"upgrade {selector} --include-unknown --silent --accept-package-agreements " +
                   "--accept-source-agreements --disable-interactivity";

        var r = await Shell.RunAsync(exe, args, line =>
        {
            if (!IsNoise(line)) onLine?.Invoke(line);
        }, ct, null, Utf8).ConfigureAwait(false);

        // 0 = sucesso. -1978335189 (0x8A15002B) = não e aplicavel / nenhuma atualização encontrada.
        if (r.Success)
        {
            app.Status = "Atualizada";
            app.Done = true;
            app.Selected = false;
            Logger.Ok("Programas", $"{app.Name} atualizada para {app.AvailableVersion}.");
            return true;
        }

        var razao = Describe(r.ExitCode);
        app.NeedsElevation = IsAccessDenied(r.ExitCode);
        app.Status = app.NeedsElevation ? "Precisa de administrador" : $"Falhou — {razao}";

        Logger.Warn("Programas", $"{app.Name}: {razao} (código {r.ExitCode}).");
        onLine?.Invoke($"{app.Name}: {razao}.");
        return false;
    }

    private static async Task<bool> UpgradeByManagerAsync(AppUpgrade app, Action<string>? onLine,
        CancellationToken ct)
    {
        var managers = await OtherManagersAsync(ct).ConfigureAwait(false);
        var manager = managers.FirstOrDefault(m =>
            m.Key.Equals(app.Source, StringComparison.OrdinalIgnoreCase) ||
            m.Name.Equals(app.Source, StringComparison.OrdinalIgnoreCase));

        if (manager is null)
        {
            app.Status = $"{app.Source} não está disponível";
            onLine?.Invoke($"{app.Name}: o gestor {app.Source} já não está disponível nesta máquina.");
            return false;
        }

        app.Status = "A atualizar...";
        onLine?.Invoke($"=== {app.Name} ({app.CurrentVersion} → {app.AvailableVersion}) via {manager.Name} ===");

        var ok = await PackageManagers.UpgradeAsync(manager, app.Id, onLine, ct).ConfigureAwait(false);

        if (ok)
        {
            app.Status = "Atualizada";
            app.Done = true;
            app.Selected = false;
        }
        else
        {
            app.Status = "Falhou";
        }

        return ok;
    }

    public static async Task<int> UpgradeManyAsync(IEnumerable<AppUpgrade> apps, Action<string>? onLine = null,
        CancellationToken ct = default)
    {
        var ok = 0;

        foreach (var app in apps.ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (await UpgradeAsync(app, onLine, ct).ConfigureAwait(false)) ok++;
        }

        onLine?.Invoke($"=== Concluído: {ok} programas atualizadas ===");
        return ok;
    }

    /// <summary>Abre a pagina da Microsoft Store do winget, para quem não o tem instalado.</summary>
    public static void OpenWingetInstall() =>
        Shell.OpenExternal("https://apps.microsoft.com/detail/9nblggh4nns1");
}
