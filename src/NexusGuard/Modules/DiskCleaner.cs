using System.IO;
using System.Runtime.InteropServices;
using NexusGuard.Core;

namespace NexusGuard.Modules;

public enum CleanRisk { Safe, Moderate, Advanced }

/// <summary>Um alvo de limpeza: um conjunto de pastas/padrões com a respetiva descricao e nível de risco.</summary>
public sealed class CleanTarget : Observable
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public CleanRisk Risk { get; init; } = CleanRisk.Safe;
    public bool NeedsAdmin { get; init; }

    /// <summary>Pastas a limpar. O conteúdo e removido, a pasta em si fica.</summary>
    public List<string> Folders { get; init; } = new();

    /// <summary>Arquivos individuais ou padrões glob (ex.: thumbcache_*.db).</summary>
    public List<string> FilePatterns { get; init; } = new();

    /// <summary>Limpeza especial (lixeira, cache DNS, etc.).</summary>
    public Func<CancellationToken, Task<CleanOutcome>>? CustomClean { get; init; }

    public Func<CancellationToken, Task<long>>? CustomSize { get; init; }

    /// <summary>Arquivos mais recentes do que isto são preservados (evita apagar temporários em uso).</summary>
    public TimeSpan? MinimumAge { get; init; }

    private bool _selected = true;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private long _size = -1;
    public long Size { get => _size; set { if (Set(ref _size, value)) { Raise(nameof(SizeText)); Raise(nameof(HasContent)); } } }

    private int _fileCount;
    public int FileCount { get => _fileCount; set { if (Set(ref _fileCount, value)) Raise(nameof(DetailText)); } }

    private string _status = "Não analisado";
    public string Status { get => _status; set => Set(ref _status, value); }

    private bool _scanning;
    public bool Scanning { get => _scanning; set => Set(ref _scanning, value); }

    public bool HasContent => Size > 0;

    public string SizeText => Size < 0 ? "—" : Fmt.Bytes(Size);

    public string DetailText => FileCount > 0 ? $"{Fmt.Count(FileCount)} arquivos" : string.Empty;

    public string RiskText => Risk switch
    {
        CleanRisk.Moderate => "Moderado",
        CleanRisk.Advanced => "Avançado",
        _ => "Seguro"
    };
}

public sealed record CleanOutcome(
    long BytesFreed,
    int FilesDeleted,
    int Skipped,
    int Locked = 0,
    string? BatchId = null,
    int ProtectedSkipped = 0,

    /// <summary>
    /// Arquivos que o Windows vai apagar no próximo arranque. É uma remoção definitiva e sem volta,
    /// por isso conta-se à parte dos que ficaram simplesmente por mover.
    /// </summary>
    int ScheduledForReboot = 0,

    /// <summary>Razão por que a limpeza não chegou a acontecer. Nula quando correu.</summary>
    string? Problem = null);

/// <summary>Um arquivo candidato a limpeza, já com a indicação de estar protegido.</summary>
public sealed record CleanFile(string Path, long Size, bool Protected)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string SizeText => Fmt.Bytes(Size);
}

/// <summary>Analisa e liberta espaço em disco em caches e pastas temporarias conhecidas do Windows.</summary>
public static class DiskCleaner
{
    /// <summary>
    /// Só uma análise de disco de cada vez. O painel e a página de limpeza podem pedir uma análise
    /// ao mesmo tempo, e duas varreduras em paralelo demoram mais do que as duas em fila.
    /// </summary>
    private static readonly SemaphoreSlim ScanGate = new(1, 1);

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static List<CleanTarget> BuildTargets()
    {
        var local = LocalAppData;

        var targets = new List<CleanTarget>
        {
            new()
            {
                Name = "Arquivos temporários do usuário",
                Description = "Conteúdo de %TEMP% deixado para trás por instaladores e programas.",
                Folders = { Path.GetTempPath() },
                MinimumAge = TimeSpan.FromHours(1)
            },
            new()
            {
                Name = "Arquivos temporários do Windows",
                Description = @"C:\Windows\Temp — temporários de serviços e componentes do sistema.",
                NeedsAdmin = true,
                Folders = { Path.Combine(Win, "Temp") },
                MinimumAge = TimeSpan.FromHours(1)
            },
            new()
            {
                Name = "Lixeira",
                Description = "Esvazia a lixeira de todas as unidades.",
                Risk = CleanRisk.Moderate,
                CustomSize = _ => Task.FromResult(RecycleBinSize()),
                CustomClean = _ => Task.FromResult(EmptyRecycleBin())
            },
            new()
            {
                Name = "Cache do Windows Update",
                Description = "Pacotes de atualização já instalados em SoftwareDistribution\\Download.",
                NeedsAdmin = true,
                Folders = { Path.Combine(Win, "SoftwareDistribution", "Download") }
            },
            new()
            {
                Name = "Cache de otimização de entrega",
                Description = "Arquivos de partilha de atualizações (Delivery Optimization).",
                NeedsAdmin = true,
                Folders =
                {
                    Path.Combine(Win, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft",
                        "Windows", "DeliveryOptimization", "Cache")
                }
            },
            new()
            {
                Name = "Cache de miniaturas e ícones",
                Description = "Bases thumbcache_*.db e iconcache_*.db — o Windows reconstrói-as.",
                FilePatterns =
                {
                    Path.Combine(local, @"Microsoft\Windows\Explorer\thumbcache_*.db"),
                    Path.Combine(local, @"Microsoft\Windows\Explorer\iconcache_*.db"),
                    Path.Combine(local, "IconCache.db")
                }
            },
            new()
            {
                Name = "Relatórios de erros (WER)",
                Description = "Arquivos e filas de relatórios de falhas de aplicativos.",
                Folders =
                {
                    Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"),
                    Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue"),
                    Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"),
                    Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue")
                }
            },
            new()
            {
                Name = "Despejos de memória (crash dumps)",
                Description = "Minidumps e MEMORY.DMP gerados por telas azuis.",
                NeedsAdmin = true,
                Folders = { Path.Combine(Win, "Minidump") },
                FilePatterns = { Path.Combine(Win, "MEMORY.DMP") }
            },
            new()
            {
                Name = "Cache de sombreadores (shaders)",
                Description = "Caches DirectX, NVIDIA e AMD; são regeneradas ao jogar.",
                Folders =
                {
                    Path.Combine(local, "D3DSCache"),
                    Path.Combine(local, @"NVIDIA\DXCache"),
                    Path.Combine(local, @"NVIDIA\GLCache"),
                    Path.Combine(local, @"NVIDIA Corporation\NV_Cache"),
                    Path.Combine(local, @"AMD\DxCache"),
                    Path.Combine(local, @"AMD\DxcCache")
                }
            },
            new()
            {
                Name = "Cache de navegadores",
                Description = "Cache do Edge, Chrome, Brave, Opera, Vivaldi e Firefox (sessões e senhas intactas).",
                Risk = CleanRisk.Moderate,
                Folders = BrowserCacheFolders()
            },
            new()
            {
                Name = "Registros do sistema",
                Description = "Logs do CBS, DISM e Panther que crescem sem limite.",
                NeedsAdmin = true,
                Risk = CleanRisk.Moderate,
                Folders =
                {
                    Path.Combine(Win, "Logs", "CBS"),
                    Path.Combine(Win, "Logs", "DISM"),
                    Path.Combine(Win, "Panther")
                }
            },
            new()
            {
                Name = "Cache de Internet (INetCache)",
                Description = "Arquivos temporários de Internet do WinINet.",
                Folders = { Path.Combine(local, @"Microsoft\Windows\INetCache") }
            },
            new()
            {
                Name = "Cache de DNS",
                Description = "Limpa o resolvedor de DNS — resolve páginas que não carregam.",
                CustomSize = _ => Task.FromResult(0L),
                CustomClean = async ct =>
                {
                    var exe = Shell.Which("ipconfig.exe") ?? "ipconfig.exe";
                    var r = await Shell.RunAsync(exe, "/flushdns", null, ct).ConfigureAwait(false);
                    return new CleanOutcome(0, r.Success ? 1 : 0, r.Success ? 0 : 1);
                }
            },
            new()
            {
                Name = "Prefetch",
                Description = "Dados de pré-carregamento. Apagar torna os primeiros arranques mais lentos.",
                Risk = CleanRisk.Advanced,
                NeedsAdmin = true,
                Selected = false,
                Folders = { Path.Combine(Win, "Prefetch") }
            },
            new()
            {
                Name = "Instalação anterior do Windows (Windows.old)",
                Description = "Ocupa muito espaço, mas apagar impede o regresso à versão anterior.",
                Risk = CleanRisk.Advanced,
                NeedsAdmin = true,
                Selected = false,
                Folders = { Path.Combine(Path.GetPathRoot(Win) ?? "C:\\", "Windows.old") }
            }
        };

        // Caches das aplicacoes instaladas, a seguir as categorias do sistema. So aparecem as
        // aplicacoes que existem nesta maquina e cuja pasta de cache tem alguma coisa dentro.
        targets.AddRange(AppCleaners.BuildTargets());

        return targets;
    }

    private static List<string> BrowserCacheFolders()
    {
        var local = LocalAppData;
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var chromium = new (string root, string[] profiles)[]
        {
            (Path.Combine(local, @"Microsoft\Edge\User Data"), Array.Empty<string>()),
            (Path.Combine(local, @"Google\Chrome\User Data"), Array.Empty<string>()),
            (Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), Array.Empty<string>()),
            (Path.Combine(roaming, @"Opera Software\Opera Stable"), Array.Empty<string>()),
            (Path.Combine(local, @"Vivaldi\User Data"), Array.Empty<string>())
        };

        var folders = new List<string>();

        foreach (var (root, _) in chromium)
        {
            if (!Directory.Exists(root)) continue;

            // Cada perfil (Default, Profile 1, ...) tem a sua própria árvore de cache.
            IEnumerable<string> profileDirs;
            try
            {
                profileDirs = Directory.EnumerateDirectories(root)
                    .Where(d =>
                    {
                        var n = Path.GetFileName(d);
                        return n.Equals("Default", StringComparison.OrdinalIgnoreCase)
                               || n.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
                    })
                    .Append(root);
            }
            catch
            {
                continue;
            }

            foreach (var profile in profileDirs)
            {
                foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache", "Service Worker\\CacheStorage", "DawnGraphiteCache", "DawnWebGPUCache" })
                    folders.Add(Path.Combine(profile, sub));
            }
        }

        // Firefox: cache fica numa árvore própria por perfil.
        var ffCache = Path.Combine(local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ffCache))
        {
            try
            {
                foreach (var profile in Directory.EnumerateDirectories(ffCache))
                    folders.Add(Path.Combine(profile, "cache2"));
            }
            catch
            {
                // Perfis inacessiveis: ignorados.
            }
        }

        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------------- Análise ----------------

    public static async Task ScanAsync(CleanTarget target, CancellationToken ct = default)
    {
        target.Scanning = true;
        target.Status = "Na fila...";

        await ScanGate.WaitAsync(ct);
        target.Status = "Analisando...";

        try
        {
            if (target.CustomSize is not null)
            {
                target.Size = await target.CustomSize(ct);
                target.FileCount = 0;
                target.Status = target.Size > 0 ? "Pronto para limpar" : "Nada a limpar";
                return;
            }

            var (size, count) = await Task.Run(() => Measure(target, ct), ct);
            target.Size = size;
            target.FileCount = count;
            target.Status = size > 0 ? "Pronto para limpar" : "Nada a limpar";
        }
        catch (OperationCanceledException)
        {
            target.Status = "Análise cancelada";
            throw;
        }
        catch (Exception ex)
        {
            target.Status = "Inacessível";
            Logger.Warn("Limpeza", $"{target.Name}: {ex.Message}");
        }
        finally
        {
            target.Scanning = false;
            ScanGate.Release();
        }
    }

    /// <summary>
    /// Lista os arquivos que este alvo removeria. Os que caem na lista branca de proteção vêm
    /// marcados em vez de omitidos, para a pré-visualização poder explicar porque ficam de fora.
    /// </summary>
    public static List<CleanFile> Enumerate(CleanTarget target, CancellationToken ct)
    {
        var result = new List<CleanFile>();
        var cutoff = target.MinimumAge is { } age ? DateTime.Now - age : (DateTime?)null;

        void Consider(string file, bool applyAge)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var fi = new FileInfo(file);
                if (!fi.Exists) return;
                if (applyAge && cutoff is not null && fi.LastWriteTime > cutoff) return;

                result.Add(new CleanFile(fi.FullName, fi.Length, Quarantine.IsProtected(fi.FullName)));
            }
            catch
            {
                // Arquivo desapareceu ou está sem permissão de leitura.
            }
        }

        foreach (var folder in target.Folders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in SafeFiles(folder, ct)) Consider(file, applyAge: true);
        }

        foreach (var pattern in target.FilePatterns)
            foreach (var file in ExpandPattern(pattern))
                Consider(file, applyAge: false);

        return result;
    }

    private static (long size, int count) Measure(CleanTarget target, CancellationToken ct)
    {
        var files = Enumerate(target, ct).Where(f => !f.Protected).ToList();
        return (files.Sum(f => f.Size), files.Count);
    }

    private static IEnumerable<string> ExpandPattern(string pattern)
    {
        var dir = Path.GetDirectoryName(pattern);
        var name = Path.GetFileName(pattern);

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;

        if (!name.Contains('*') && !name.Contains('?'))
        {
            if (File.Exists(pattern)) yield return pattern;
            yield break;
        }

        IEnumerable<string> matches;
        try { matches = Directory.EnumerateFiles(dir, name, SearchOption.TopDirectoryOnly); }
        catch { yield break; }

        foreach (var m in matches) yield return m;
    }

    /// <summary>Enumera arquivos recursivamente ignorando pastas sem permissão.</summary>
    private static IEnumerable<string> SafeFiles(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var f in files) yield return f;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var s in subs)
            {
                try
                {
                    // Não segue junctions/symlinks para evitar ciclos e sair do alvo.
                    var attrs = File.GetAttributes(s);
                    if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue;
                }
                catch { continue; }

                stack.Push(s);
            }
        }
    }

    // ---------------- Limpeza ----------------

    /// <summary>
    /// Limpa o alvo. Com <paramref name="only"/> trata apenas os arquivos escolhidos na
    /// pré-visualização; sem ele, varre o alvo inteiro.
    /// </summary>
    public static async Task<CleanOutcome> CleanAsync(CleanTarget target, IProgress<string>? progress = null,
        CancellationToken ct = default, IReadOnlyCollection<string>? only = null)
    {
        target.Status = Settings.Current.DryRun ? "Simulando..." : "Limpando...";

        try
        {
            CleanOutcome outcome;

            if (target.CustomClean is not null)
            {
                outcome = Settings.Current.DryRun
                    ? new CleanOutcome(target.Size > 0 ? target.Size : 0, 0, 0)
                    : await target.CustomClean(ct);
            }
            else
            {
                outcome = await Task.Run(() => Purge(target, only, progress, ct), ct);
            }

            target.Size = 0;
            target.FileCount = 0;

            target.Status = Settings.Current.DryRun
                ? "Simulado"
                : outcome.Locked > 0
                    ? $"Limpo ({outcome.Locked} em uso)"
                    : "Limpo";

            var verb = Settings.Current.DryRun
                ? "simulado"
                : Settings.Current.UseQuarantine ? "movido para quarentena" : "removido";

            Logger.Ok("Limpeza", $"{target.Name}: {Fmt.Bytes(outcome.BytesFreed)} {verb} " +
                                 $"({outcome.FilesDeleted} arquivos, {outcome.Locked} em uso, " +
                                 $"{outcome.ProtectedSkipped} protegidos).");

            return outcome;
        }
        catch (OperationCanceledException)
        {
            target.Status = "Cancelado";
            throw;
        }
        catch (Exception ex)
        {
            target.Status = "Falhou";
            Logger.Error("Limpeza", $"{target.Name} falhou", ex);
            return new CleanOutcome(0, 0, 0);
        }
    }

    /// <summary>
    /// Move para quarentena (padrão) ou apaga. Arquivos bloqueados por outro programa ficam
    /// agendados para serem removidos no próximo arranque, antes de o Windows os travar.
    /// </summary>
    private static CleanOutcome Purge(CleanTarget target, IReadOnlyCollection<string>? only,
        IProgress<string>? progress, CancellationToken ct)
    {
        var all = Enumerate(target, ct);
        var protectedCount = all.Count(f => f.Protected);

        var candidates = all.Where(f => !f.Protected);

        if (only is not null)
        {
            var wanted = new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);
            candidates = candidates.Where(f => wanted.Contains(f.Path));
        }

        var files = candidates.ToList();

        if (Settings.Current.DryRun)
        {
            progress?.Report($"{target.Name}: {Fmt.Count(files.Count)} arquivos seriam tratados (modo simular).");
            return new CleanOutcome(files.Sum(f => f.Size), 0, 0, 0, null, protectedCount);
        }

        if (Settings.Current.UseQuarantine)
        {
            // A quarentena vive no disco do sistema. Encher esse disco para guardar o que se estava
            // a limpar seria trocar um problema por outro bem pior.
            var room = Quarantine.HasRoomFor(files.Select(f => (f.Path, f.Size)));

            if (!room.Ok)
            {
                var problem = $"A quarentena em {room.Drive} tem {Fmt.Bytes(room.Free)} livres e " +
                              $"precisaria de {Fmt.Bytes(room.Needed)}.";

                Logger.Warn("Limpeza", $"{target.Name}: {problem}");
                return new CleanOutcome(0, 0, files.Count, 0, null, protectedCount, 0, problem);
            }

            var result = Quarantine.Move(files.Select(f => f.Path), target.Name, progress, ct);
            var locked = StillOnDisk(files, result.Moved);

            CleanupEmptyFolders(target);
            RemoveWindowsOldIfRequested(target);

            if (result.BatchId is not null)
            {
                History.Add("Limpeza", $"{target.Name}: {Fmt.Count(result.Moved)} arquivos em quarentena",
                    Fmt.Bytes(result.Bytes), UndoKind.QuarantineBatch,
                    new Dictionary<string, string> { ["batch"] = result.BatchId });
            }

            return new CleanOutcome(result.Bytes, result.Moved, result.Failed, locked, result.BatchId, protectedCount);
        }

        long freed = 0;
        int deleted = 0, locked2 = 0, scheduled = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var fi = new FileInfo(file.Path);
                if (!fi.Exists) continue;

                if (fi.IsReadOnly) fi.IsReadOnly = false;
                fi.Delete();

                freed += file.Size;
                deleted++;

                if (deleted % 200 == 0)
                    progress?.Report($"{target.Name}: {Fmt.Count(deleted)} arquivos removidos...");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sem quarentena, quem limpa já aceitou a remoção definitiva. Um arquivo preso por
                // outro programa só sai no arranque seguinte, antes de o Windows o voltar a abrir.
                if (ScheduleDeleteOnReboot(file.Path)) scheduled++;
                else locked2++;
            }
        }

        CleanupEmptyFolders(target);
        RemoveWindowsOldIfRequested(target);

        History.Add("Limpeza", $"{target.Name}: {Fmt.Count(deleted)} arquivos removidos", Fmt.Bytes(freed));

        return new CleanOutcome(freed, deleted, locked2 + scheduled, locked2, null, protectedCount, scheduled);
    }

    /// <summary>
    /// Quantos arquivos continuaram em disco depois da tentativa de mover para a quarentena.
    ///
    /// Só conta: não agenda nada. Quem liga a quarentena está a pedir que nada seja apagado sem
    /// volta, e agendar a remoção definitiva de um arquivo que falhou a entrada na quarentena faria
    /// exatamente o contrário do que a definição promete — sem o dizer, e sem forma de recuperar.
    /// Ficam onde estão e a próxima limpeza tenta outra vez.
    /// </summary>
    private static int StillOnDisk(List<CleanFile> files, int moved)
    {
        if (moved >= files.Count) return 0;

        var stillThere = 0;

        foreach (var file in files)
        {
            try { if (File.Exists(file.Path)) stillThere++; }
            catch (IOException) { stillThere++; }
            catch (UnauthorizedAccessException) { stillThere++; }
        }

        return stillThere;
    }

    /// <summary>Agenda a remoção para o próximo arranque. Requer administrador.</summary>
    public static bool ScheduleDeleteOnReboot(string path)
    {
        if (!Fmt.IsAdmin) return false;

        try { return Native.MoveFileEx(path, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT); }
        catch { return false; }
    }

    private static void CleanupEmptyFolders(CleanTarget target)
    {
        foreach (var folder in target.Folders)
        {
            if (!Directory.Exists(folder)) continue;

            try
            {
                foreach (var dir in Directory.GetDirectories(folder, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                {
                    try
                    {
                        if (File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint)) continue;
                        if (Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                        Directory.Delete(dir);
                    }
                    catch
                    {
                        // Pasta em uso.
                    }
                }
            }
            catch
            {
                // Sem acesso à árvore.
            }
        }
    }

    private static void RemoveWindowsOldIfRequested(CleanTarget target)
    {
        foreach (var folder in target.Folders.Where(f =>
                     f.EndsWith("Windows.old", StringComparison.OrdinalIgnoreCase) && Directory.Exists(f)))
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    // ---------------- Lixeira ----------------

    public static long RecycleBinSize()
    {
        try
        {
            var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Native.SHQUERYRBINFO>() };
            // null = todas as unidades
            return Native.SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static CleanOutcome EmptyRecycleBin()
    {
        var before = RecycleBinSize();

        try
        {
            var hr = Native.SHEmptyRecycleBin(IntPtr.Zero, null,
                Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);

            // 0 = OK; -2147418113 (E_UNEXPECTED) surge quando já esta vazia.
            if (hr != 0 && before > 0)
                Logger.Warn("Limpeza", $"Lixeira devolveu o código 0x{hr:X8}.");

            return new CleanOutcome(before, 1, 0);
        }
        catch (Exception ex)
        {
            Logger.Error("Limpeza", "Falha ao esvaziar a lixeira", ex);
            return new CleanOutcome(0, 0, 1);
        }
    }

    /// <summary>Executa a Limpeza de Disco do Windows em modo silencioso (componentes do sistema).</summary>
    public static async Task<bool> RunWindowsComponentCleanupAsync(Action<string>? onLine = null, CancellationToken ct = default)
    {
        var dism = Shell.Which("Dism.exe") ?? "Dism.exe";
        Logger.Info("Limpeza", "Compactando o armazenamento de componentes (DISM /StartComponentCleanup)...");

        var r = await Shell.RunAsync(dism, "/Online /Cleanup-Image /StartComponentCleanup", onLine, ct).ConfigureAwait(false);

        if (r.Success) Logger.Ok("Limpeza", "Armazenamento de componentes compactado.");
        else Logger.Warn("Limpeza", $"DISM terminou com o código {r.ExitCode}.");

        return r.Success;
    }
}
