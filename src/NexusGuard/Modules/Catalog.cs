using System.IO;
using System.Text;
using System.Text.Json;
using NexusGuard.Core;

namespace NexusGuard.Modules;

public enum InstallState { Pending, Running, Done, AlreadyThere, Failed }

/// <summary>Uma aplicação do catálogo de instalação em lote.</summary>
public sealed class CatalogApp : Observable
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public required string Group { get; init; }

    /// <summary>
    /// Origem do pacote. Quase tudo vem do repositório do winget; o que só existe na Microsoft
    /// Store precisa de <c>--source msstore</c> e de um código de produto em vez de um id legível.
    /// </summary>
    public string Source { get; init; } = "winget";

    /// <summary>Primeira letra, para o lugar do ícone enquanto não há ícone.</summary>
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private InstallState _state = InstallState.Pending;
    public InstallState State
    {
        get => _state;
        set { if (Set(ref _state, value)) Raise(nameof(StateText)); }
    }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    public string StateText => State switch
    {
        InstallState.Running => "instalando",
        InstallState.Done => "instalada",
        InstallState.AlreadyThere => "já estava",
        InstallState.Failed => "falhou",
        _ => "na fila"
    };
}

/// <summary>Quantas instalações correram bem, quantas já existiam e quantas falharam.</summary>
public sealed record BatchResult(int Installed, int AlreadyThere, int Failed)
{
    public int Total => Installed + AlreadyThere + Failed;

    public string Summary => Failed == 0
        ? AlreadyThere == 0
            ? $"{Installed} aplicativos instalados."
            : $"{Installed} instalados, {AlreadyThere} já estavam presentes."
        : $"{Installed} instalados, {Failed} falharam.";
}

/// <summary>
/// Catálogo para instalar aplicativos em lote, pensado para um PC recém-formatado.
///
/// Todos os identificadores foram confirmados um a um contra o repositório do winget. Um catálogo
/// com ids errados é uma funcionalidade que parece pronta e só falha na mão de quem a usa, por isso
/// nenhum entra aqui de memória.
/// </summary>
public static class Catalog
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public const string Browsers = "Navegadores";
    public const string Essentials = "Essenciais";
    public const string Communication = "Comunicação";
    public const string Work = "Trabalho e dev";

    /// <summary>
    /// Instâncias novas a cada chamada: cada uma guarda o seu estado de seleção e de instalação, e
    /// partilhá-las entre visitas à página deixaria caixas marcadas de uma sessão anterior.
    /// </summary>
    public static List<CatalogApp> All() => new()
    {
        new() { Name = "Google Chrome", Id = "Google.Chrome", Group = Browsers },
        new() { Name = "Mozilla Firefox", Id = "Mozilla.Firefox", Group = Browsers },
        new() { Name = "Brave", Id = "Brave.Brave", Group = Browsers },

        new() { Name = "7-Zip", Id = "7zip.7zip", Group = Essentials },
        new() { Name = "VLC", Id = "VideoLAN.VLC", Group = Essentials },
        new() { Name = "Acrobat Reader", Id = "Adobe.Acrobat.Reader.64-bit", Group = Essentials },
        new() { Name = "Notepad++", Id = "Notepad++.Notepad++", Group = Essentials },
        new() { Name = "PowerToys", Id = "Microsoft.PowerToys", Group = Essentials },

        new() { Name = "WhatsApp", Id = "9NKSQGP7F2NH", Group = Communication, Source = "msstore" },
        new() { Name = "Telegram", Id = "Telegram.TelegramDesktop", Group = Communication },
        new() { Name = "Zoom", Id = "Zoom.Zoom", Group = Communication },
        new() { Name = "Microsoft Teams", Id = "Microsoft.Teams", Group = Communication },
        new() { Name = "Discord", Id = "Discord.Discord", Group = Communication },
        new() { Name = "AnyDesk", Id = "AnyDesk.AnyDesk", Group = Communication },

        new() { Name = "LibreOffice", Id = "TheDocumentFoundation.LibreOffice", Group = Work },
        new() { Name = "Visual Studio Code", Id = "Microsoft.VisualStudioCode", Group = Work },
        new() { Name = "Git", Id = "Git.Git", Group = Work },
        new() { Name = "Python 3.13", Id = "Python.Python.3.13", Group = Work },
        new() { Name = "Node.js", Id = "OpenJS.NodeJS", Group = Work },
        new() { Name = "Steam", Id = "Valve.Steam", Group = Work }
    };

    /// <summary>Conjuntos prontos, como no desenho: um clique marca tudo o que o perfil inclui.</summary>
    public static readonly (string Name, string[] Ids)[] Presets =
    {
        ("PC básico", new[]
        {
            "Google.Chrome", "7zip.7zip", "VideoLAN.VLC", "Adobe.Acrobat.Reader.64-bit", "9NKSQGP7F2NH"
        }),
        ("Escritório", new[]
        {
            "Google.Chrome", "7zip.7zip", "VideoLAN.VLC", "Adobe.Acrobat.Reader.64-bit", "9NKSQGP7F2NH",
            "TheDocumentFoundation.LibreOffice", "Zoom.Zoom", "Microsoft.Teams", "AnyDesk.AnyDesk"
        }),
        ("Desenvolvedor", new[]
        {
            "Brave.Brave", "7zip.7zip", "Microsoft.VisualStudioCode", "Git.Git",
            "Notepad++.Notepad++", "Microsoft.PowerToys", "Discord.Discord"
        })
    };

    // ---------------- instalação ----------------

    /// <summary>
    /// Instala a fila, um de cada vez. Em paralelo seria mais rápido, mas dois instaladores do
    /// Windows a correr ao mesmo tempo disputam o Windows Installer e falham um ao outro.
    ///
    /// Cada falha é repetida uma vez: a maioria vem de rede e passa à segunda.
    /// </summary>
    public static async Task<BatchResult> InstallAsync(IReadOnlyList<CatalogApp> apps,
        Action<string>? onLine = null, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var exe = AppUpdater.WingetPath;

        if (exe is null)
        {
            onLine?.Invoke("O winget não está instalado — sem ele não há de onde instalar.");
            return new BatchResult(0, 0, apps.Count);
        }

        int installed = 0, already = 0, failed = 0, done = 0;

        foreach (var app in apps)
        {
            ct.ThrowIfCancellationRequested();

            app.State = InstallState.Running;
            app.Status = "instalando…";
            onLine?.Invoke($"=== {app.Name} ({app.Id}) ===");

            var outcome = await InstallOneAsync(exe, app, onLine, ct).ConfigureAwait(false);

            if (outcome == InstallState.Failed)
            {
                onLine?.Invoke($"{app.Name}: falhou à primeira, tentando outra vez…");
                outcome = await InstallOneAsync(exe, app, onLine, ct).ConfigureAwait(false);
            }

            app.State = outcome;
            app.Selected = false;

            switch (outcome)
            {
                case InstallState.Done:
                    installed++;
                    app.Status = "instalada";
                    Logger.Ok("Aplicativos", $"{app.Name} instalado.");
                    break;

                case InstallState.AlreadyThere:
                    already++;
                    app.Status = "já estava instalada";
                    break;

                default:
                    failed++;
                    app.Status = "falhou";
                    Logger.Warn("Aplicativos", $"{app.Name} não foi instalado.");
                    break;
            }

            progress?.Report((++done, apps.Count));
        }

        var result = new BatchResult(installed, already, failed);
        onLine?.Invoke($"=== {result.Summary} ===");
        return result;
    }

    private static async Task<InstallState> InstallOneAsync(string exe, CatalogApp app,
        Action<string>? onLine, CancellationToken ct)
    {
        var source = app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
            ? " --source msstore"
            : "";

        var args = $"install --id \"{app.Id}\" --exact{source} --silent " +
                   "--accept-package-agreements --accept-source-agreements --disable-interactivity";

        var r = await Shell.RunAsync(exe, args, onLine, ct, null, Utf8).ConfigureAwait(false);

        return r.ExitCode switch
        {
            0 => InstallState.Done,

            // 0x8A150061: já está instalado. Não é erro — é o resultado desejado já conseguido.
            unchecked((int)0x8A150061) => InstallState.AlreadyThere,

            // 0x8A15002B: nada aplicável encontrado, que para uma instalação quer dizer o mesmo.
            unchecked((int)0x8A15002B) => InstallState.AlreadyThere,

            _ => InstallState.Failed
        };
    }

    // ---------------- perfis ----------------

    /// <summary>
    /// Guarda os ids escolhidos para reutilizar noutro PC. Só ids: um perfil não deve carregar
    /// nomes nem estados, para continuar a funcionar quando o catálogo mudar.
    /// </summary>
    public static string ExportProfile(IEnumerable<CatalogApp> apps)
    {
        var ids = apps.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return JsonSerializer.Serialize(new Profile(1, ids), Shell.Json);
    }

    /// <summary>Lê um perfil. Devolve vazio em vez de atirar: o ficheiro vem de fora.</summary>
    public static string[] ParseProfile(string json)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<Profile>(json, Shell.Json);
            return profile?.Ids ?? Array.Empty<string>();
        }
        catch (JsonException ex)
        {
            Logger.Warn("Aplicativos", $"perfil ilegível — {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public static void SaveProfile(string path, IEnumerable<CatalogApp> apps) =>
        File.WriteAllText(path, ExportProfile(apps), Utf8);

    public static string[] LoadProfile(string path) =>
        File.Exists(path) ? ParseProfile(File.ReadAllText(path)) : Array.Empty<string>();

    private sealed record Profile(int Version, string[] Ids);
}
