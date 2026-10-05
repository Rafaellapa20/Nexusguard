using System.IO;
using System.Text.Json;
using NexusGuard.Core;

namespace NexusGuard.Modules;

public sealed class QuarantineItem
{
    public string Original { get; set; } = "";
    public string Stored { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }
}

public sealed class QuarantineBatch : Observable
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime When { get; set; }
    public long Bytes { get; set; }
    public int Files { get; set; }
    public List<QuarantineItem> Items { get; set; } = new();

    /// <summary>Pasta do lote em disco — preenchida na leitura, não vai para o manifesto.</summary>
    public string Folder { get; set; } = "";

    public string WhenText => When.ToString("dd/MM/yyyy HH:mm", Fmt.Pt);

    public string SizeText => Fmt.Bytes(Bytes);

    public int DaysLeft => Math.Max(0, Settings.Current.QuarantineDays - (int)(DateTime.Now - When).TotalDays);

    public bool Expiring => DaysLeft <= 3;

    public string ExpiryText => DaysLeft switch
    {
        0 => "expira hoje",
        1 => "expira em 1 dia",
        _ => $"expira em {DaysLeft} dias"
    };

    public string Summary => $"{Fmt.Count(Files)} ficheiros · {SizeText}";
}

public sealed record QuarantineResult(int Moved, int Failed, long Bytes, string? BatchId);

/// <summary>
/// Em vez de apagar, o NexusGuard move para C:\ProgramData\NexusGuard\Quarantine. Cada lote guarda
/// um manifesto com o caminho de origem de cada ficheiro, por isso restaurar é sempre possível
/// enquanto o lote não expirar.
/// </summary>
public static class Quarantine
{
    private const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // ---------------- Lista branca de proteção ----------------

    private static readonly string[] ProtectedRoots = BuildProtectedRoots();

    private static string[] BuildProtectedRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(profile, "Downloads"),
            Path.Combine(profile, "OneDrive"),
            roaming
        };

        // Perfis de navegador: palavras-passe, sessões e favoritos vivem aqui. A cache fica fora destes caminhos.
        foreach (var browser in new[]
                 {
                     @"Microsoft\Edge\User Data\Default",
                     @"Google\Chrome\User Data\Default",
                     @"BraveSoftware\Brave-Browser\User Data\Default",
                     @"Mozilla\Firefox\Profiles"
                 })
        {
            roots.Add(Path.Combine(local, browser));
        }

        return roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Ficheiros sob pastas pessoais ou de perfil de navegador nunca são tocados, mesmo que uma
    /// regra de limpeza os apanhe. Caches ficam fora da lista por não guardarem nada do utilizador.
    /// </summary>
    public static bool IsProtected(string fullPath)
    {
        string path;
        try { path = Path.GetFullPath(fullPath); }
        catch { return true; }

        foreach (var root in ProtectedRoots)
        {
            if (root.Length == 0) continue;

            if (path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ||
                path.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                // A cache dos navegadores vive dentro do perfil e é segura de limpar.
                if (LooksLikeCache(path)) continue;
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeCache(string path) =>
        path.Contains(@"\Cache", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\Code Cache", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\GPUCache", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\cache2", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\INetCache", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\CacheStorage", StringComparison.OrdinalIgnoreCase);

    /// <summary>Nome do próximo lote: aaaaMMdd-HHmm, com sufixo se já existir.</summary>
    private static string NextBatchFolder()
    {
        var baseName = DateTime.Now.ToString("yyyyMMdd-HHmm");
        var folder = Path.Combine(Paths.Quarantine, baseName);
        var n = 2;

        while (Directory.Exists(folder))
        {
            folder = Path.Combine(Paths.Quarantine, $"{baseName}-{n}");
            n++;
        }

        return folder;
    }

    /// <summary>
    /// Move os ficheiros indicados para um lote novo. Devolve o identificador do lote, que fica
    /// guardado no histórico para o «Desfazer».
    /// </summary>
    /// <summary>Resultado da verificação de espaço, com os números para mostrar a quem pergunta.</summary>
    public sealed record SpaceCheck(bool Ok, long Needed, long Free, string Drive);

    /// <summary>Margem a deixar livre no disco da quarentena depois de tudo movido.</summary>
    private const long Margin = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Confirma que a quarentena cabe no disco onde vive, antes de mover o que quer que seja.
    ///
    /// A quarentena está sempre no disco do sistema. Mover um ficheiro dentro do mesmo disco é só
    /// mudar o nome e não gasta espaço nenhum, mas mover de outro disco é copiar — limpar 50 GB de
    /// um disco de dados encheria o disco do Windows e deixaria a máquina sem conseguir arrancar.
    /// Só conta o que vem de fora, que é o que custa espaço.
    /// </summary>
    public static SpaceCheck HasRoomFor(IEnumerable<(string Path, long Size)> files)
    {
        var store = Paths.Quarantine;
        var storeRoot = Path.GetPathRoot(store) ?? "C:\\";

        long needed = 0;

        foreach (var (path, size) in files)
        {
            var root = Path.GetPathRoot(path);
            if (root is null) continue;

            // Mesmo disco: a mudança é instantânea e não ocupa espaço novo.
            if (string.Equals(root, storeRoot, StringComparison.OrdinalIgnoreCase)) continue;

            needed += size;
        }

        long free;

        try
        {
            free = new DriveInfo(storeRoot).AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            // Sem saber o espaço livre, deixar passar é o menor dos males: recusar uma limpeza por
            // causa de uma leitura falhada seria pior do que o risco que se tenta evitar.
            Logger.Warn("Quarentena", $"espaço livre de {storeRoot} indisponível — {ex.Message}");
            return new SpaceCheck(true, needed, 0, storeRoot);
        }

        return new SpaceCheck(needed == 0 || free > needed + Margin, needed, free, storeRoot);
    }

    public static QuarantineResult Move(IEnumerable<string> files, string label,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var batchFolder = NextBatchFolder();
        var items = new List<QuarantineItem>();

        long bytes = 0;
        int moved = 0, failed = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) continue;

                if (IsProtected(file))
                {
                    failed++;
                    continue;
                }

                var original = info.FullName;
                var modified = info.LastWriteTime;
                var size = info.Length;

                var relative = Paths.ToRelativeStorePath(original);
                var target = Path.Combine(batchFolder, "files", relative);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (info.IsReadOnly) info.IsReadOnly = false;
                info.MoveTo(target, overwrite: true);

                items.Add(new QuarantineItem
                {
                    Original = original,
                    Stored = relative,
                    Size = size,
                    Modified = modified
                });

                bytes += size;
                moved++;

                if (moved % 200 == 0) progress?.Report($"{Fmt.Count(moved)} ficheiros movidos…");
            }
            catch
            {
                failed++;
            }
        }

        if (moved == 0)
        {
            try { if (Directory.Exists(batchFolder)) Directory.Delete(batchFolder, true); } catch { }
            return new QuarantineResult(0, failed, 0, null);
        }

        var batch = new QuarantineBatch
        {
            Id = Path.GetFileName(batchFolder),
            Label = label,
            When = DateTime.Now,
            Bytes = bytes,
            Files = moved,
            Items = items
        };

        try
        {
            File.WriteAllText(Path.Combine(batchFolder, ManifestName),
                JsonSerializer.Serialize(batch, JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Error("Quarentena", $"Não foi possível gravar o manifesto de {batch.Id}", ex);
        }

        Logger.Ok("Quarentena", $"{label}: {moved} ficheiros ({Fmt.Bytes(bytes)}) em quarentena no lote {batch.Id}.");
        return new QuarantineResult(moved, failed, bytes, batch.Id);
    }

    public static List<QuarantineBatch> List()
    {
        var batches = new List<QuarantineBatch>();

        try
        {
            if (!Directory.Exists(Paths.Quarantine)) return batches;

            foreach (var dir in Directory.EnumerateDirectories(Paths.Quarantine))
            {
                var manifest = Path.Combine(dir, ManifestName);
                if (!File.Exists(manifest)) continue;

                try
                {
                    var batch = JsonSerializer.Deserialize<QuarantineBatch>(File.ReadAllText(manifest), JsonOptions);
                    if (batch is null) continue;

                    batch.Folder = dir;
                    batches.Add(batch);
                }
                catch
                {
                    // Lote ilegível: ignorado, mas continua em disco para inspeção manual.
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Quarentena", $"Não foi possível listar a quarentena: {ex.Message}");
        }

        return batches.OrderByDescending(b => b.When).ToList();
    }

    public static QuarantineBatch? Find(string batchId) =>
        List().FirstOrDefault(b => b.Id.Equals(batchId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Devolve os ficheiros aos caminhos originais. Não sobrepõe nada que exista agora.</summary>
    public static (int restored, int skipped) Restore(QuarantineBatch batch)
    {
        int restored = 0, skipped = 0;

        foreach (var item in batch.Items)
        {
            try
            {
                var source = Path.Combine(batch.Folder, "files", item.Stored);
                if (!File.Exists(source)) { skipped++; continue; }

                var target = Paths.FromRelativeStorePath(item.Stored);

                if (File.Exists(target)) { skipped++; continue; }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target);
                restored++;
            }
            catch
            {
                skipped++;
            }
        }

        if (restored > 0)
        {
            try { Directory.Delete(batch.Folder, recursive: true); } catch { }
            Logger.Ok("Quarentena", $"Lote {batch.Id}: {restored} ficheiros restaurados ({skipped} ignorados).");
        }
        else
        {
            Logger.Warn("Quarentena", $"Lote {batch.Id}: nada restaurado ({skipped} ignorados).");
        }

        return (restored, skipped);
    }

    public static bool Delete(QuarantineBatch batch)
    {
        try
        {
            Directory.Delete(batch.Folder, recursive: true);
            Logger.Ok("Quarentena", $"Lote {batch.Id} apagado definitivamente ({batch.SizeText}).");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Quarentena", $"Não foi possível apagar o lote {batch.Id}", ex);
            return false;
        }
    }

    public static (int batches, long bytes) EmptyAll()
    {
        var all = List();
        long bytes = 0;
        var count = 0;

        foreach (var batch in all)
        {
            if (!Delete(batch)) continue;
            bytes += batch.Bytes;
            count++;
        }

        return (count, bytes);
    }

    /// <summary>Apaga lotes que passaram do prazo de retenção.</summary>
    public static int PurgeExpired()
    {
        var days = Settings.Current.QuarantineDays;
        var cutoff = DateTime.Now.AddDays(-days);
        var purged = 0;

        foreach (var batch in List().Where(b => b.When < cutoff))
        {
            if (Delete(batch)) purged++;
        }

        if (purged > 0)
            Logger.Info("Quarentena", $"{purged} lote(s) com mais de {days} dias foram removidos automaticamente.");

        return purged;
    }

    public static long TotalBytes() => List().Sum(b => b.Bytes);
}
