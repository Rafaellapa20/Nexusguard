using System.IO;
using Microsoft.Win32;
using NexusGuard.Core;

namespace NexusGuard.Modules;

public sealed class RegistryIssue
{
    public required string Category { get; init; }
    public required string Hive { get; init; }
    public required string KeyPath { get; init; }
    public string? ValueName { get; init; }
    public string Detail { get; init; } = "";

    public string Display => ValueName is null ? KeyPath : $"{KeyPath}\\{ValueName}";
}

public sealed class RegistryCategory : Observable
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    private int _count;
    public int Count { get => _count; set { if (Set(ref _count, value)) Raise(nameof(CountText)); } }

    private bool _selected = true;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    public List<RegistryIssue> Issues { get; } = new();

    public string CountText => Count == 0 ? "—" : Fmt.Count(Count);
}

/// <summary>
/// Procura entradas do registro que apontam para coisas que já não existem. Antes de remover
/// seja o que for, exporta um .reg para a quarentena — reverter é só importar esse arquivo.
/// </summary>
public static class RegistryCleaner
{
    public static List<RegistryCategory> BuildCategories() => new()
    {
        new RegistryCategory
        {
            Name = "Extensões de arquivo órfãs",
            Description = "Tipos de arquivo em HKCR que apontam para programas que já não existem."
        },
        new RegistryCategory
        {
            Name = "App Paths inválidos",
            Description = "Atalhos do «Executar» que apontam para executáveis apagados."
        },
        new RegistryCategory
        {
            Name = "Desinstaladores inexistentes",
            Description = "Entradas em «Programas instalados» cujo desinstalador desapareceu."
        },
        new RegistryCategory
        {
            Name = "Bibliotecas partilhadas ausentes",
            Description = "SharedDLLs registadas para arquivos que já não estão em disco."
        },
        new RegistryCategory
        {
            Name = "Itens de inicialização quebrados",
            Description = "Entradas Run que apontam para programas apagados."
        },
        new RegistryCategory
        {
            Name = "Históricos e listas recentes",
            Description = "MRU de diálogos de abrir/salvar e listas de execução recente."
        }
    };

    public static void Scan(List<RegistryCategory> categories, CancellationToken ct = default)
    {
        foreach (var c in categories)
        {
            c.Issues.Clear();
            c.Count = 0;
            c.Status = "Verificando…";
        }

        ScanFileExtensions(categories[0], ct);
        ScanAppPaths(categories[1], ct);
        ScanUninstallers(categories[2], ct);
        ScanSharedDlls(categories[3], ct);
        ScanRunEntries(categories[4], ct);
        ScanMru(categories[5], ct);

        foreach (var c in categories)
        {
            c.Count = c.Issues.Count;
            c.Status = c.Count == 0 ? "Nada encontrado" : "Pronto para corrigir";
        }

        Logger.Ok("Registro", $"{categories.Sum(c => c.Count)} entradas inválidas encontradas.");
    }

    private static void ScanFileExtensions(RegistryCategory category, CancellationToken ct)
    {
        try
        {
            using var classes = Registry.ClassesRoot;

            foreach (var name in classes.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                if (!name.StartsWith('.')) continue;

                try
                {
                    using var ext = classes.OpenSubKey(name);
                    if (ext?.GetValue(null) is not string progId || progId.Length == 0) continue;

                    using var target = classes.OpenSubKey(progId);
                    if (target is not null) continue;

                    category.Issues.Add(new RegistryIssue
                    {
                        Category = category.Name,
                        Hive = "HKEY_CLASSES_ROOT",
                        KeyPath = name,
                        Detail = $"aponta para «{progId}», que não existe"
                    });
                }
                catch
                {
                    // Chave sem permissão: ignorada.
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Registro", $"Extensões: {ex.Message}");
        }
    }

    private static void ScanAppPaths(RegistryCategory category, CancellationToken ct)
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key is null) continue;

                foreach (var name in key.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        using var sub = key.OpenSubKey(name);
                        if (sub?.GetValue(null) is not string exe || exe.Length == 0) continue;

                        var clean = exe.Trim('"');
                        if (File.Exists(clean)) continue;

                        category.Issues.Add(new RegistryIssue
                        {
                            Category = category.Name,
                            Hive = root.Name,
                            KeyPath = $"{path}\\{name}",
                            Detail = $"«{clean}» não existe"
                        });
                    }
                    catch
                    {
                        // Ignorado.
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Registro", $"App Paths: {ex.Message}");
            }
        }
    }

    private static void ScanUninstallers(RegistryCategory category, CancellationToken ct)
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is null) return;

            foreach (var name in key.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub?.GetValue("UninstallString") is not string cmd || cmd.Length == 0) continue;
                    if (cmd.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) continue;

                    var exe = cmd.StartsWith('"')
                        ? cmd[1..Math.Max(1, cmd.IndexOf('"', 1))]
                        : cmd.Split(' ')[0];

                    if (string.IsNullOrWhiteSpace(exe) || File.Exists(exe)) continue;

                    category.Issues.Add(new RegistryIssue
                    {
                        Category = category.Name,
                        Hive = Registry.LocalMachine.Name,
                        KeyPath = $"{path}\\{name}",
                        Detail = $"desinstalador «{exe}» não existe"
                    });
                }
                catch
                {
                    // Ignorado.
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Registro", $"Desinstaladores: {ex.Message}");
        }
    }

    private static void ScanSharedDlls(RegistryCategory category, CancellationToken ct)
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs";

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is null) return;

            foreach (var name in key.GetValueNames())
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(name) || File.Exists(name)) continue;

                category.Issues.Add(new RegistryIssue
                {
                    Category = category.Name,
                    Hive = Registry.LocalMachine.Name,
                    KeyPath = path,
                    ValueName = name,
                    Detail = "arquivo ausente"
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Registro", $"SharedDLLs: {ex.Message}");
        }
    }

    private static void ScanRunEntries(RegistryCategory category, CancellationToken ct)
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key is null) continue;

                foreach (var name in key.GetValueNames())
                {
                    ct.ThrowIfCancellationRequested();
                    if (key.GetValue(name) is not string cmd || cmd.Length == 0) continue;

                    var exe = cmd.StartsWith('"')
                        ? cmd[1..Math.Max(1, cmd.IndexOf('"', 1))]
                        : cmd.Split(' ')[0];

                    if (string.IsNullOrWhiteSpace(exe) || File.Exists(exe)) continue;
                    if (!Path.IsPathRooted(exe)) continue;

                    category.Issues.Add(new RegistryIssue
                    {
                        Category = category.Name,
                        Hive = root.Name,
                        KeyPath = path,
                        ValueName = name,
                        Detail = $"«{exe}» não existe"
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Registro", $"Run: {ex.Message}");
            }
        }
    }

    private static void ScanMru(RegistryCategory category, CancellationToken ct)
    {
        var paths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\RunMRU",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedPidlMRU",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs"
        };

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key is null) continue;

                var count = key.GetValueNames().Count(v => !string.IsNullOrEmpty(v) && v != "MRUList" && v != "MRUListEx");
                if (count == 0) continue;

                category.Issues.Add(new RegistryIssue
                {
                    Category = category.Name,
                    Hive = Registry.CurrentUser.Name,
                    KeyPath = path,
                    Detail = $"{count} entradas de histórico"
                });
            }
            catch
            {
                // Ignorado.
            }
        }
    }

    /// <summary>
    /// Exporta as chaves afetadas para um .reg salvo na quarentena e só depois remove.
    /// Reverter é importar esse arquivo.
    /// </summary>
    public static async Task<(int fixedCount, string? backupFile)> FixAsync(
        IEnumerable<RegistryCategory> categories, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var issues = categories.Where(c => c.Selected).SelectMany(c => c.Issues).ToList();
        if (issues.Count == 0) return (0, null);

        if (!Fmt.IsAdmin && issues.Any(i => i.Hive.Contains("LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)))
            onLine?.Invoke("Algumas entradas são do sistema e vão ser ignoradas sem privilégios de administrador.");

        var backupFile = Path.Combine(Paths.RegistryBackups, $"registro-{DateTime.Now:yyyyMMdd-HHmm}.reg");

        if (Settings.Current.DryRun)
        {
            onLine?.Invoke($"[simular] {issues.Count} entradas seriam corrigidas.");
            return (issues.Count, null);
        }

        onLine?.Invoke($"Exportando cópia de segurança para {backupFile}…");

        var exported = await ExportAsync(issues, backupFile, onLine, ct).ConfigureAwait(true);
        if (!exported)
        {
            onLine?.Invoke("A exportação falhou — nada foi alterado.");
            return (0, null);
        }

        var removed = 0;

        foreach (var issue in issues)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var root = issue.Hive.StartsWith("HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase)
                    ? Registry.ClassesRoot
                    : issue.Hive.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)
                        ? Registry.CurrentUser
                        : Registry.LocalMachine;

                if (issue.ValueName is null)
                {
                    root.DeleteSubKeyTree(issue.KeyPath, throwOnMissingSubKey: false);
                }
                else
                {
                    using var key = root.OpenSubKey(issue.KeyPath, writable: true);
                    key?.DeleteValue(issue.ValueName, throwOnMissingValue: false);
                }

                removed++;
            }
            catch (Exception ex)
            {
                onLine?.Invoke($"Ignorado: {issue.Display} ({ex.Message})");
            }
        }

        History.Add("Registro", $"{removed} entradas inválidas corrigidas", Fmt.Count(removed),
            UndoKind.RegistryExport, new Dictionary<string, string> { ["file"] = backupFile });

        Logger.Ok("Registro", $"{removed} entradas corrigidas. Cópia em {backupFile}.");
        onLine?.Invoke($"=== {removed} entradas corrigidas ===");

        return (removed, backupFile);
    }

    private static async Task<bool> ExportAsync(List<RegistryIssue> issues, string file,
        Action<string>? onLine, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            var reg = Shell.Which("reg.exe") ?? "reg.exe";
            var keys = issues.Select(i => $"{i.Hive}\\{i.KeyPath}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var temp = Path.Combine(Path.GetTempPath(), $"ng-reg-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temp);

            var parts = new List<string>();

            foreach (var key in keys)
            {
                ct.ThrowIfCancellationRequested();

                var part = Path.Combine(temp, $"{parts.Count}.reg");
                var r = await Shell.RunAsync(reg, $"export \"{key}\" \"{part}\" /y", null, ct).ConfigureAwait(true);

                if (r.Success && File.Exists(part)) parts.Add(part);
            }

            if (parts.Count == 0)
            {
                try { Directory.Delete(temp, true); } catch { }
                return false;
            }

            // Junta os exports num único .reg (só o primeiro cabeçalho é mantido).
            using (var writer = new StreamWriter(file, false, System.Text.Encoding.Unicode))
            {
                writer.WriteLine("Windows Registry Editor Version 5.00");
                writer.WriteLine();

                foreach (var part in parts)
                {
                    foreach (var line in File.ReadLines(part, System.Text.Encoding.Unicode))
                    {
                        if (line.StartsWith("Windows Registry Editor", StringComparison.OrdinalIgnoreCase)) continue;
                        writer.WriteLine(line);
                    }
                }
            }

            try { Directory.Delete(temp, true); } catch { }

            onLine?.Invoke($"Cópia de segurança com {keys.Count} chaves gravada.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Registro", "Falha ao exportar a cópia de segurança", ex);
            return false;
        }
    }

    /// <summary>Reimporta um .reg exportado antes de uma correção.</summary>
    public static async Task<bool> RestoreAsync(string regFile, Action<string>? onLine = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(regFile))
        {
            onLine?.Invoke($"O arquivo {regFile} já não existe.");
            return false;
        }

        var reg = Shell.Which("reg.exe") ?? "reg.exe";
        var r = await Shell.RunAsync(reg, $"import \"{regFile}\"", onLine, ct).ConfigureAwait(true);

        if (r.Success) Logger.Ok("Registro", $"Entradas repostas a partir de {regFile}.");
        else Logger.Warn("Registro", $"A importação de {regFile} devolveu {r.ExitCode}.");

        return r.Success;
    }
}
