using System.IO;
using Microsoft.Win32;
using NexusGuard.Core;

namespace NexusGuard.Modules;

public enum AppClass { InUse, Bloatware, Unsafe, Redundant, Removed, Unknown }

public sealed class InstalledApp : Observable
{
    public required string Name { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string UninstallCommand { get; init; } = "";
    public string QuietUninstallCommand { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string RegistryKey { get; init; } = "";
    public DateTime? InstallDate { get; init; }
    public long EstimatedSize { get; init; }
    public bool SystemComponent { get; init; }

    private AppClass _classification = AppClass.Unknown;
    public AppClass Classification { get => _classification; set { if (Set(ref _classification, value)) { Raise(nameof(ClassText)); Raise(nameof(ClassIsWarn)); Raise(nameof(ClassIsBad)); Raise(nameof(ClassIsOk)); } } }

    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    private DateTime? _lastUsed;
    public DateTime? LastUsed { get => _lastUsed; set { if (Set(ref _lastUsed, value)) Raise(nameof(LastUsedText)); } }

    public string SizeText => EstimatedSize > 0 ? Fmt.Bytes(EstimatedSize) : "—";

    public string InstalledText => InstallDate?.ToString("dd/MM/yyyy", Fmt.Pt) ?? "—";

    public string LastUsedText
    {
        get
        {
            if (LastUsed is null) return "—";

            var days = (int)(DateTime.Now - LastUsed.Value).TotalDays;

            if (days <= 0) return "hoje";
            if (days == 1) return "ontem";
            if (days < 30) return $"há {days} dias";

            if (days < 365)
            {
                var months = days / 30;
                return months == 1 ? "há 1 mês" : $"há {months} meses";
            }

            var years = days / 365;
            return years == 1 ? "há 1 ano" : $"há {years} anos";
        }
    }

    public string ClassText => Classification switch
    {
        AppClass.Bloatware => "Bloatware",
        AppClass.Unsafe => "Inseguro",
        AppClass.Redundant => "Redundante",
        AppClass.InUse => "Em uso",
        AppClass.Removed => "Removido",
        _ => "—"
    };

    public bool ClassIsWarn => Classification is AppClass.Bloatware or AppClass.Redundant;
    public bool ClassIsBad => Classification == AppClass.Unsafe;
    public bool ClassIsOk => Classification == AppClass.InUse;

    public bool CanUninstall => !string.IsNullOrWhiteSpace(UninstallCommand) || !string.IsNullOrWhiteSpace(QuietUninstallCommand);

    public string Subtitle => string.IsNullOrWhiteSpace(Publisher)
        ? Version
        : string.IsNullOrWhiteSpace(Version) ? Publisher : $"{Publisher} · {Version}";
}

/// <summary>
/// Lê os programas instalados do registro, classifica-os e desinstala em modo silencioso,
/// varrendo os resíduos que ficam para trás (pastas e chaves órfãs) para a quarentena.
/// </summary>
public static class Uninstaller
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Programas que vêm pré-instalados e quase ninguém usa.</summary>
    private static readonly string[] BloatwareHints =
    {
        "candy crush", "bubble witch", "march of empires", "disney magic kingdoms",
        "supportassist", "dell digital delivery", "dell customer connect", "hp jumpstarts",
        "hp support assistant", "lenovo vantage", "mcafee", "norton security scan",
        "wildtangent", "booking.com", "farm heroes", "spotify stub", "onedrive setup",
        "amazon prime video for windows", "netflix", "disney+", "hidden city", "royal revolt",
        "asphalt", "dolby access", "my hp", "hp documentation", "acer jumpstart",
        "cyberlink power", "nero", "wondershare", "driver booster", "advanced systemcare",
        "pc accelerate", "pc optimizer", "systweak", "reimage"
    };

    /// <summary>Programas desatualizados com histórico conhecido de vulnerabilidades.</summary>
    private static readonly string[] UnsafeHints =
    {
        "adobe flash player", "adobe shockwave", "java 6", "java 7", "java(tm) 6", "java(tm) 7",
        "microsoft silverlight", "quicktime", "winrar 4", "winrar 5.0", "utorrent web",
        "ask toolbar", "babylon", "coupon", "搜狗", "baidu"
    };

    /// <summary>Programas que costumam duplicar funções já existentes no Windows.</summary>
    private static readonly string[] RedundantHints =
    {
        "winzip", "7-zip", "winrar", "ccleaner", "avast", "avg antivirus", "iobit",
        "glary utilities", "advanced uninstaller", "quicktime player"
    };

    public static List<InstalledApp> Load()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        Read(apps, Registry.LocalMachine, UninstallPath);
        Read(apps, Registry.LocalMachine, UninstallPath32);
        Read(apps, Registry.CurrentUser, UninstallPath);

        var list = apps.Values.ToList();

        foreach (var app in list) app.Classification = Classify(app);
        foreach (var app in list) app.LastUsed = GuessLastUsed(app);

        return list
            .OrderBy(a => a.Classification switch
            {
                AppClass.Unsafe => 0,
                AppClass.Bloatware => 1,
                AppClass.Redundant => 2,
                _ => 3
            })
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void Read(Dictionary<string, InstalledApp> into, RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null) return;

            foreach (var subName in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(subName);
                    if (sub is null) continue;

                    var name = sub.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Atualizações e componentes do sistema não são programas para o usuário desinstalar.
                    if (sub.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (sub.GetValue("ParentKeyName") is string p && p.Length > 0) continue;
                    if (sub.GetValue("ReleaseType") is string rt &&
                        rt.Contains("Update", StringComparison.OrdinalIgnoreCase)) continue;

                    var app = new InstalledApp
                    {
                        Name = name.Trim(),
                        Publisher = (sub.GetValue("Publisher") as string ?? "").Trim(),
                        Version = (sub.GetValue("DisplayVersion") as string ?? "").Trim(),
                        UninstallCommand = (sub.GetValue("UninstallString") as string ?? "").Trim(),
                        QuietUninstallCommand = (sub.GetValue("QuietUninstallString") as string ?? "").Trim(),
                        InstallLocation = (sub.GetValue("InstallLocation") as string ?? "").Trim(),
                        RegistryKey = $"{root.Name}\\{path}\\{subName}",
                        InstallDate = ParseInstallDate(sub.GetValue("InstallDate") as string),
                        EstimatedSize = sub.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0
                    };

                    if (!into.ContainsKey(app.Name)) into[app.Name] = app;
                }
                catch
                {
                    // Entrada malformada: ignorada.
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Desinstalar", $"Não foi possível ler {path}: {ex.Message}");
        }
    }

    private static DateTime? ParseInstallDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length != 8) return null;

        return DateTime.TryParseExact(raw, "yyyyMMdd", Fmt.Pt,
            System.Globalization.DateTimeStyles.None, out var dt) ? dt : null;
    }

    private static AppClass Classify(InstalledApp app)
    {
        var haystack = $"{app.Name} {app.Publisher}".ToLowerInvariant();

        if (UnsafeHints.Any(h => haystack.Contains(h))) return AppClass.Unsafe;
        if (BloatwareHints.Any(h => haystack.Contains(h))) return AppClass.Bloatware;
        if (RedundantHints.Any(h => haystack.Contains(h))) return AppClass.Redundant;

        return AppClass.InUse;
    }

    /// <summary>Aproxima o último uso pela data de acesso do executável principal.</summary>
    private static DateTime? GuessLastUsed(InstalledApp app)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(app.InstallLocation) || !Directory.Exists(app.InstallLocation))
                return null;

            var newest = Directory.EnumerateFiles(app.InstallLocation, "*.exe", SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists)
                .Select(f => f.LastAccessTime)
                .DefaultIfEmpty()
                .Max();

            return newest == default ? null : newest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Corre o desinstalador em modo silencioso quando o programa o suporta. Sem versão silenciosa,
    /// abre o desinstalador normal para o usuário seguir os passos.
    /// </summary>
    public static async Task<bool> UninstallAsync(InstalledApp app, Action<string>? onLine = null,
        CancellationToken ct = default)
    {
        if (!app.CanUninstall)
        {
            onLine?.Invoke($"{app.Name}: sem comando de desinstalação registado.");
            return false;
        }

        if (Settings.Current.DryRun)
        {
            onLine?.Invoke($"[simular] {app.Name} seria desinstalado.");
            app.Status = "Simulado";
            return true;
        }

        app.Status = "Desinstalando…";
        onLine?.Invoke($"=== {app.Name} ===");

        var command = string.IsNullOrWhiteSpace(app.QuietUninstallCommand)
            ? MakeSilent(app.UninstallCommand)
            : app.QuietUninstallCommand;

        var (exe, args) = SplitCommand(command);
        onLine?.Invoke($"{exe} {args}");

        var result = await Shell.RunAsync(exe, args, onLine, ct).ConfigureAwait(true);

        // Muitos desinstaladores devolvem 0, 1605 (já não instalado) ou 3010 (pede reinício).
        var ok = result.ExitCode is 0 or 1605 or 3010;

        if (ok)
        {
            app.Status = "Removido";
            app.Classification = AppClass.Removed;
            History.Add("Desinstalar", $"{app.Name} removido", app.Version);
            Logger.Ok("Desinstalar", $"{app.Name} removido.");
        }
        else
        {
            app.Status = $"Falhou ({result.ExitCode})";
            Logger.Warn("Desinstalar", $"{app.Name}: desinstalador devolveu {result.ExitCode}.");
        }

        return ok;
    }

    /// <summary>Acrescenta as opções silenciosas conhecidas ao comando de desinstalação.</summary>
    private static string MakeSilent(string command)
    {
        var lower = command.ToLowerInvariant();

        if (lower.Contains("msiexec"))
        {
            var silent = command.Replace("/I", "/X", StringComparison.OrdinalIgnoreCase);
            if (!lower.Contains("/qn")) silent += " /qn /norestart";
            return silent;
        }

        if (lower.Contains("unins000.exe") || lower.Contains("unins001.exe"))
            return command + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

        if (lower.Contains("uninstall.exe") && lower.Contains("nsis"))
            return command + " /S";

        return command;
    }

    private static (string exe, string args) SplitCommand(string command)
    {
        command = command.Trim();

        if (command.StartsWith('"'))
        {
            var close = command.IndexOf('"', 1);
            if (close > 0)
                return (command[1..close], command[(close + 1)..].Trim());
        }

        var space = command.IndexOf(' ');
        return space < 0 ? (command, string.Empty) : (command[..space], command[(space + 1)..].Trim());
    }

    /// <summary>
    /// Procura o que ficou para trás: a pasta de instalação, a pasta em AppData e a chave
    /// Uninstall. O que encontrar vai para quarentena, nunca é apagado directamente.
    /// </summary>
    public static List<string> FindResidues(InstalledApp app)
    {
        var residues = new List<string>();
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        void Consider(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            if (Quarantine.IsProtected(folder)) return;
            residues.Add(folder);
        }

        Consider(app.InstallLocation);

        var token = app.Name.Split(' ').FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(token) && token.Length >= 4)
        {
            foreach (var root in new[] { roaming, local })
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        if (Path.GetFileName(dir).StartsWith(token, StringComparison.OrdinalIgnoreCase))
                            Consider(dir);
                    }
                }
                catch
                {
                    // Sem acesso: ignorado.
                }
            }
        }

        return residues.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static QuarantineResult QuarantineResidues(InstalledApp app, IEnumerable<string> folders,
        CancellationToken ct = default)
    {
        var files = new List<string>();

        foreach (var folder in folders)
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                files.AddRange(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories));
            }
            catch
            {
                // Pasta parcialmente inacessível: leva o que der.
            }
        }

        if (files.Count == 0) return new QuarantineResult(0, 0, 0, null);

        var result = Quarantine.Move(files, $"Resíduos de {app.Name}", null, ct);

        if (result.BatchId is not null)
        {
            History.Add("Desinstalar", $"Resíduos de {app.Name} em quarentena",
                Fmt.Bytes(result.Bytes), UndoKind.QuarantineBatch,
                new Dictionary<string, string> { ["batch"] = result.BatchId });
        }

        return result;
    }
}
