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

    /// <summary>Um ID truncado pelo winget não pode ser usado para atualizar com segurança.</summary>
    public bool IdIsReliable => !Id.Contains('…') && !Id.EndsWith("...", StringComparison.Ordinal);
}

/// <summary>
/// Atualização de aplicativos via winget. A tabela do winget é localizada, por isso cada linha e lida
/// da direita para a esquerda: só o nome pode conter espaços.
/// </summary>
public static class AppUpdater
{
    private static readonly Regex Whitespace = new(@"\s{1,}", RegexOptions.Compiled);

    /// <summary>O winget escreve em UTF-8 quando a saída é redirecionada.</summary>
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

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
            Logger.Warn("Aplicativos", "O winget (Programa de Instalação de Aplicativos) não esta instalado.");
            return new List<AppUpgrade>();
        }

        Logger.Info("Aplicativos", "Procurando atualizações de aplicativos...");

        var r = await Shell.RunAsync(exe,
            "upgrade --include-unknown --accept-source-agreements --disable-interactivity",
            null, ct, null, Utf8).ConfigureAwait(false);

        var list = Parse(r.All);

        foreach (var item in list.Where(i => !i.IdIsReliable))
            item.Status = "ID truncado — atualize pelo nome";

        Logger.Ok("Aplicativos", list.Count == 0
            ? "Todas as aplicativos estão atualizadas."
            : $"{list.Count} aplicativos com atualização disponível.");

        return list;
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

    /// <summary>Linhas que são apenas barras de progresso não interessam ao registro.</summary>
    private static bool IsNoise(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;

        var trimmed = line.Trim();
        if (trimmed.All(c => c is '█' or '▒' or '-' or '\\' or '|' or '/' or '.' or ' ')) return true;
        return trimmed.Contains('█') || trimmed.Contains('▒');
    }

    public static async Task<bool> UpgradeAsync(AppUpgrade app, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var exe = WingetPath;
        if (exe is null) return false;

        app.Status = "Atualizando...";
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
            Logger.Ok("Aplicativos", $"{app.Name} atualizada para {app.AvailableVersion}.");
            return true;
        }

        app.Status = $"Falhou (código {r.ExitCode})";
        Logger.Warn("Aplicativos", $"{app.Name}: winget devolveu {r.ExitCode}.");
        onLine?.Invoke($"{app.Name}: winget devolveu o código {r.ExitCode}.");
        return false;
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

        onLine?.Invoke($"=== Concluído: {ok} aplicativos atualizadas ===");
        return ok;
    }

    /// <summary>Abre a pagina da Microsoft Store do winget, para quem não o tem instalado.</summary>
    public static void OpenWingetInstall() =>
        Shell.OpenExternal("https://apps.microsoft.com/detail/9nblggh4nns1");
}
