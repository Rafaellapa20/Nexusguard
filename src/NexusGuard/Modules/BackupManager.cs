using System.IO;
using System.Text;
using System.Text.Json;
using NexusGuard.Core;

namespace NexusGuard.Modules;

public enum BackupMode
{
    /// <summary>Acumula: copia o que é novo ou mais recente e nunca apaga nada no destino.</summary>
    Incremental,

    /// <summary>Espelho exato: o destino fica igual à origem, incluindo remocoes.</summary>
    Mirror,

    /// <summary>Cópia completa numa pasta com a data, preservando as anteriores.</summary>
    Snapshot
}

public sealed class BackupSource : Observable
{
    public required string Name { get; init; }
    public required string Path { get; init; }

    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private long _size = -1;
    public long Size { get => _size; set { if (Set(ref _size, value)) Raise(nameof(SizeText)); } }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    public bool Exists => Directory.Exists(Path);

    public string SizeText => Size < 0 ? "—" : Fmt.Bytes(Size);
}

public sealed record BackupEntry(string Name, string Mode, DateTime When, long Bytes, int Files, string[] Sources)
{
    public string WhenText => When.ToString("dd/MM/yyyy HH:mm", Fmt.Pt);
    public string SizeText => Fmt.Bytes(Bytes);
    public string Display => $"{WhenText} · {Mode} · {Fmt.Count(Files)} arquivos · {SizeText}";
}

public sealed record BackupResult(bool Success, long Bytes, int Files, int Failures, string Message, string Destination);

/// <summary>
/// Cópias de segurança para um disco externo usando o robocopy do Windows (resistente a falhas e retomavel),
/// mais pontos de restauro e imagem completa do sistema.
/// </summary>
public static class BackupManager
{
    public const string RootFolderName = "NexusGuard-Backup";

    public static string RobocopyPath => Shell.Which("robocopy.exe") ?? "robocopy.exe";

    public static List<BackupSource> DefaultSources()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new (string name, string path, bool on)[]
        {
            ("Área de trabalho", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), true),
            ("Documentos", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), true),
            ("Imagens", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), true),
            ("Vídeos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), true),
            ("Música", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), true),
            ("Downloads", Path.Combine(profile, "Downloads"), false),
            ("Favoritos", Environment.GetFolderPath(Environment.SpecialFolder.Favorites), true),
            ("Dados de aplicativos (Roaming)", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), false)
        };

        var list = new List<BackupSource>();

        foreach (var (name, path, on) in candidates)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
            list.Add(new BackupSource { Name = name, Path = path, Selected = on });
        }

        return list;
    }

    /// <summary>Unidades adequadas a destino: removiveis e discos locais que não sejam o do sistema.</summary>
    public static List<DriveInfoSnapshot> CandidateDestinations()
    {
        SystemMonitor.Instance.RefreshDrives();
        var systemRoot = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');

        return SystemMonitor.Instance.Drives
            .Where(d => d.Type is DriveType.Removable or DriveType.Fixed or DriveType.Network)
            .Where(d => !d.Letter.Equals(systemRoot, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static async Task MeasureAsync(BackupSource source, CancellationToken ct = default)
    {
        source.Status = "Medindo...";

        try
        {
            var size = await Task.Run(() => DirectorySize(source.Path, ct), ct);
            source.Size = size;
            source.Status = "";
        }
        catch (OperationCanceledException)
        {
            source.Status = "Cancelado";
            throw;
        }
        catch (Exception ex)
        {
            source.Status = "Inacessível";
            Logger.Warn("Backup", $"{source.Name}: {ex.Message}");
        }
    }

    private static long DirectorySize(string root, CancellationToken ct)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }

                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    try
                    {
                        if (File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint)) continue;
                    }
                    catch { continue; }

                    stack.Push(d);
                }
            }
            catch
            {
                // Pasta sem permissão.
            }
        }

        return total;
    }

    /// <summary>Valida o destino antes de comecar: existe, não esta dentro da origem e tem espaço.</summary>
    public static string? ValidateDestination(string destination, IEnumerable<BackupSource> sources, long estimatedBytes)
    {
        if (string.IsNullOrWhiteSpace(destination))
            return "Escolha a unidade ou pasta de destino.";

        string full;
        try { full = Path.GetFullPath(destination); }
        catch { return "O caminho de destino não e válido."; }

        if (!Directory.Exists(full))
            return $"A pasta de destino não existe: {full}";

        foreach (var s in sources)
        {
            var src = Path.GetFullPath(s.Path).TrimEnd('\\');
            if (full.StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase) ||
                full.Equals(src, StringComparison.OrdinalIgnoreCase))
                return $"O destino está dentro de '{s.Name}'. Escolha uma unidade diferente.";
        }

        try
        {
            var root = Path.GetPathRoot(full);
            if (root is not null && Native.GetDiskFreeSpaceEx(root, out var free, out _, out _))
            {
                if (estimatedBytes > 0 && (long)free < estimatedBytes)
                    return $"Espaço insuficiente: a cópia precisa de cerca de {Fmt.Bytes(estimatedBytes)} " +
                           $"e so ha {Fmt.Bytes((long)free)} livres em {root}";
            }
        }
        catch
        {
            // Sem leitura de espaço: deixa prosseguir e o robocopy falha de forma controlada.
        }

        return null;
    }

    public static string BuildBackupRoot(string destination, BackupMode mode)
    {
        var root = Path.Combine(destination, RootFolderName, Environment.MachineName);

        return mode == BackupMode.Snapshot
            ? Path.Combine(root, $"snapshot-{DateTime.Now:yyyy-MM-dd_HHmm}")
            : Path.Combine(root, "atual");
    }

    /// <summary>Executa a cópia de segurança, pasta a pasta, com o robocopy.</summary>
    public static async Task<BackupResult> RunAsync(IReadOnlyList<BackupSource> sources, string destination,
        BackupMode mode, bool skipCloudOnly, Action<string>? onLine = null, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var selected = sources.Where(s => s.Selected && s.Exists).ToList();

        if (selected.Count == 0)
            return new BackupResult(false, 0, 0, 0, "Nenhuma pasta selecionada.", destination);

        var root = BuildBackupRoot(destination, mode);

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex)
        {
            var msg = $"Não foi possível criar a pasta de destino: {ex.Message}";
            Logger.Error("Backup", msg);
            return new BackupResult(false, 0, 0, 0, msg, root);
        }

        var modeText = mode switch
        {
            BackupMode.Mirror => "espelho",
            BackupMode.Snapshot => "instantâneo",
            _ => "incremental"
        };

        Logger.Info("Backup", $"A iniciar cópia {modeText} de {selected.Count} pasta(s) para {root}.");
        onLine?.Invoke($"=== Cópia de segurança ({modeText}) para {root} ===");

        long totalBytes = 0;
        var totalFiles = 0;
        var failures = 0;

        foreach (var source in selected)
        {
            ct.ThrowIfCancellationRequested();

            var target = Path.Combine(root, SafeFolderName(source.Name));
            source.Status = "Copiando...";
            progress?.Report($"Copiando {source.Name}...");
            onLine?.Invoke($"--- {source.Name}: {source.Path}  →  {target}");

            var args = BuildRobocopyArgs(source.Path, target, mode, skipCloudOnly);

            long bytes = 0;
            var files = 0;

            var r = await Shell.RunAsync(RobocopyPath, args, line =>
            {
                var parsed = ParseRobocopyLine(line);
                if (parsed.isFile)
                {
                    files++;
                    bytes += parsed.bytes;
                    if (files % 25 == 0) progress?.Report($"{source.Name}: {Fmt.Count(files)} arquivos ({Fmt.Bytes(bytes)})");
                }

                if (!string.IsNullOrWhiteSpace(line)) onLine?.Invoke(line.TrimEnd());
            }, ct).ConfigureAwait(false);

            // Robocopy: 0-7 são sucesso (0 = nada a copiar); >= 8 indica falhas reais.
            if (r.ExitCode >= 8)
            {
                failures++;
                source.Status = $"Falhou (código {r.ExitCode})";
                Logger.Warn("Backup", $"{source.Name}: robocopy devolveu {r.ExitCode}.");
                onLine?.Invoke($"!!! {source.Name}: robocopy devolveu o código {r.ExitCode}.");
            }
            else
            {
                source.Status = files > 0 ? $"{Fmt.Count(files)} arquivos · {Fmt.Bytes(bytes)}" : "Sem alteracoes";
                totalFiles += files;
                totalBytes += bytes;
            }
        }

        await WriteManifestAsync(root, modeText, totalBytes, totalFiles, selected, ct).ConfigureAwait(false);

        var ok = failures == 0;
        var message = ok
            ? $"Cópia concluída: {Fmt.Count(totalFiles)} arquivos · {Fmt.Bytes(totalBytes)} em {root}"
            : $"Cópia concluída com {failures} pasta(s) com erros. Consulte o registro.";

        if (ok) Logger.Ok("Backup", message);
        else Logger.Warn("Backup", message);

        onLine?.Invoke($"=== {message} ===");
        return new BackupResult(ok, totalBytes, totalFiles, failures, message, root);
    }

    private static string BuildRobocopyArgs(string source, string target, BackupMode mode, bool skipCloudOnly)
    {
        var sb = new StringBuilder();
        sb.Append($"\"{source.TrimEnd('\\')}\" \"{target.TrimEnd('\\')}\" ");

        sb.Append(mode switch
        {
            // /MIR replica remocoes; /E + /XO acumula sem nunca apagar.
            BackupMode.Mirror => "/MIR ",
            BackupMode.Snapshot => "/E ",
            _ => "/E /XO "
        });

        sb.Append("/COPY:DAT /DCOPY:DAT ");   // dados, atributos e datas
        sb.Append("/R:1 /W:2 ");              // não insiste em arquivos bloqueados
        sb.Append("/MT:16 ");                 // cópia multi-thread
        sb.Append("/XJ ");                    // ignora junctions (evita ciclos)
        sb.Append("/BYTES /NP /NDL /NJH ");   // tamanhos em bytes, sem percentagens nem cabecalho
        sb.Append("/XD \"$RECYCLE.BIN\" \"System Volume Information\" ");

        if (skipCloudOnly) sb.Append("/XA:O "); // ignora arquivos apenas na nuvem (OneDrive)

        return sb.ToString().TrimEnd();
    }

    /// <summary>Linhas de arquivo do robocopy tem a forma "  <tag>  <tamanho>\t<caminho>".</summary>
    private static (bool isFile, long bytes) ParseRobocopyLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return (false, 0);

        var tab = line.IndexOf('\t');
        if (tab <= 0) return (false, 0);

        var head = line[..tab].Trim();
        if (head.Length == 0) return (false, 0);

        // O tamanho é o último campo antes do tab; pode vir precedido de um rótulo localizado.
        var lastSpace = head.LastIndexOf(' ');
        var sizeToken = lastSpace >= 0 ? head[(lastSpace + 1)..] : head;

        return long.TryParse(sizeToken, out var bytes) ? (true, bytes) : (false, 0);
    }

    private static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);

        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);

        return sb.ToString().Trim().TrimEnd('.');
    }

    private static async Task WriteManifestAsync(string root, string mode, long bytes, int files,
        IEnumerable<BackupSource> sources, CancellationToken ct)
    {
        try
        {
            var manifest = new
            {
                app = "NexusGuard",
                machine = Environment.MachineName,
                user = Environment.UserName,
                mode,
                when = DateTime.Now,
                bytes,
                files,
                sources = sources.Select(s => new { s.Name, s.Path }).ToArray()
            };

            var file = Path.Combine(root, "turboclean-backup.json");
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(file, json, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn("Backup", $"Não foi possível escrever o manifesto: {ex.Message}");
        }
    }

    /// <summary>Procura cópias anteriores do NexusGuard numa unidade de destino.</summary>
    public static List<BackupEntry> ListBackups(string destination)
    {
        var list = new List<BackupEntry>();

        try
        {
            var root = Path.Combine(destination, RootFolderName, Environment.MachineName);
            if (!Directory.Exists(root)) return list;

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var manifest = Path.Combine(dir, "turboclean-backup.json");
                var name = Path.GetFileName(dir);

                if (!File.Exists(manifest))
                {
                    list.Add(new BackupEntry(name, "desconhecido", Directory.GetLastWriteTime(dir), 0, 0, Array.Empty<string>()));
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    var r = doc.RootElement;

                    list.Add(new BackupEntry(
                        name,
                        r.TryGetProperty("mode", out var m) ? m.GetString() ?? "—" : "—",
                        r.TryGetProperty("when", out var w) && w.TryGetDateTime(out var dt) ? dt : Directory.GetLastWriteTime(dir),
                        r.TryGetProperty("bytes", out var b) ? b.GetInt64() : 0,
                        r.TryGetProperty("files", out var f) ? f.GetInt32() : 0,
                        r.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Array
                            ? s.EnumerateArray()
                                .Select(e => e.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "")
                                .Where(x => x.Length > 0).ToArray()
                            : Array.Empty<string>()));
                }
                catch
                {
                    list.Add(new BackupEntry(name, "ilegível", Directory.GetLastWriteTime(dir), 0, 0, Array.Empty<string>()));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Backup", $"Não foi possível listar cópias em {destination}: {ex.Message}");
        }

        return list.OrderByDescending(e => e.When).ToList();
    }

    /// <summary>Restaura uma pasta da cópia de segurança para o destino indicado (nunca apaga no destino).</summary>
    public static async Task<BackupResult> RestoreAsync(string backupFolder, string targetFolder,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(backupFolder))
            return new BackupResult(false, 0, 0, 1, "A pasta da cópia de segurança não existe.", targetFolder);

        try
        {
            Directory.CreateDirectory(targetFolder);
        }
        catch (Exception ex)
        {
            return new BackupResult(false, 0, 0, 1, $"Destino inacessível: {ex.Message}", targetFolder);
        }

        Logger.Info("Backup", $"Restaurando {backupFolder} para {targetFolder}.");
        onLine?.Invoke($"=== Restauro: {backupFolder}  →  {targetFolder} ===");

        long bytes = 0;
        var files = 0;

        // /XO garante que arquivos mais recentes no destino não são substituidos pela versão da cópia.
        var args = $"\"{backupFolder.TrimEnd('\\')}\" \"{targetFolder.TrimEnd('\\')}\" /E /XO " +
                   "/COPY:DAT /DCOPY:DAT /R:1 /W:2 /MT:16 /XJ /BYTES /NP /NDL /NJH " +
                   "/XF \"turboclean-backup.json\"";

        var r = await Shell.RunAsync(RobocopyPath, args, line =>
        {
            var parsed = ParseRobocopyLine(line);
            if (parsed.isFile) { files++; bytes += parsed.bytes; }
            if (!string.IsNullOrWhiteSpace(line)) onLine?.Invoke(line.TrimEnd());
        }, ct).ConfigureAwait(false);

        var ok = r.ExitCode < 8;
        var message = ok
            ? $"Restauro concluído: {Fmt.Count(files)} arquivos · {Fmt.Bytes(bytes)}"
            : $"Restauro falhou (robocopy devolveu {r.ExitCode}).";

        if (ok) Logger.Ok("Backup", message);
        else Logger.Error("Backup", message);

        onLine?.Invoke($"=== {message} ===");
        return new BackupResult(ok, bytes, files, ok ? 0 : 1, message, targetFolder);
    }

    /// <summary>Cria um ponto de restauro do sistema.</summary>
    public static async Task<(bool ok, string message)> CreateRestorePointAsync(string description,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (!Fmt.IsAdmin)
            return (false, "Criar um ponto de restauro requer privilégios de administrador.");

        var safe = new string(description.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').Take(60).ToArray());
        if (string.IsNullOrWhiteSpace(safe)) safe = "NexusGuard";

        onLine?.Invoke("Criando ponto de restauro do sistema...");

        var sb = new StringBuilder();
        sb.AppendLine("$drive = $env:SystemDrive");
        sb.AppendLine("try { Enable-ComputerRestore -Drive $drive -ErrorAction Stop } catch {}");
        // O Windows limita pontos de restauro a um por 24h; esta chave remove esse limite para este pedido.
        sb.AppendLine(@"$k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'");
        sb.AppendLine("$old = $null");
        sb.AppendLine("try { $old = (Get-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -ErrorAction Stop).SystemRestorePointCreationFrequency } catch {}");
        sb.AppendLine("try { Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord -ErrorAction Stop } catch {}");
        sb.AppendLine("try {");
        sb.AppendLine($"  Checkpoint-Computer -Description '{safe}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop");
        sb.AppendLine("  Write-Output 'RESTOREPOINT_OK'");
        sb.AppendLine("} catch { Write-Output ('RESTOREPOINT_FAIL ' + $_.Exception.Message) }");
        sb.AppendLine("finally {");
        sb.AppendLine("  if ($null -ne $old) { try { Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Value $old -Type DWord } catch {} }");
        sb.AppendLine("}");

        var r = await Shell.PowerShellAsync(sb.ToString(), onLine, ct).ConfigureAwait(false);

        if (r.All.Contains("RESTOREPOINT_OK", StringComparison.Ordinal))
        {
            Logger.Ok("Backup", $"Ponto de restauro '{safe}' criado.");
            return (true, $"Ponto de restauro '{safe}' criado.");
        }

        var reason = r.All.Contains("RESTOREPOINT_FAIL", StringComparison.Ordinal)
            ? r.All[(r.All.IndexOf("RESTOREPOINT_FAIL", StringComparison.Ordinal) + 18)..].Trim().Split('\n')[0]
            : "verifique se a Proteção do Sistema esta ligada para o disco do Windows";

        var msg = $"Não foi possível criar o ponto de restauro: {reason}";
        Logger.Warn("Backup", msg);
        return (false, msg);
    }

    public static void OpenSystemProtection() => Shell.OpenExternal("SystemPropertiesProtection.exe");

    /// <summary>Imagem completa do sistema com o wbadmin. O destino tem de ser uma unidade dedicada em NTFS.</summary>
    public static async Task<(bool ok, string message)> CreateSystemImageAsync(string targetDriveLetter,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (!Fmt.IsAdmin)
            return (false, "A imagem do sistema requer privilégios de administrador.");

        var letter = targetDriveLetter.Trim().TrimEnd('\\', ':');
        if (letter.Length != 1 || !char.IsLetter(letter[0]))
            return (false, "Indique a letra da unidade de destino (ex.: E).");

        var wbadmin = Shell.Which("wbadmin.exe");
        if (wbadmin is null)
            return (false, "O wbadmin não esta disponível nesta edição do Windows.");

        var systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');

        if (string.Equals($"{letter}:", systemDrive, StringComparison.OrdinalIgnoreCase))
            return (false, "O destino não pode ser o próprio disco do Windows.");

        onLine?.Invoke($"=== Imagem completa do sistema para {letter}: (pode levar horas) ===");
        Logger.Info("Backup", $"Criando imagem do sistema em {letter}:.");

        var args = $"start backup -backupTarget:{letter}: -include:{systemDrive} -allCritical -vssFull -quiet";
        var r = await Shell.RunAsync(wbadmin, args, onLine, ct).ConfigureAwait(false);

        if (r.Success)
        {
            Logger.Ok("Backup", "Imagem do sistema concluída.");
            return (true, $"Imagem do sistema criada em {letter}:.");
        }

        var msg = $"O wbadmin terminou com o código {r.ExitCode}. " +
                  "Confirme que a unidade esta formatada em NTFS e tem espaço suficiente.";
        Logger.Warn("Backup", msg);
        return (false, msg);
    }
}
