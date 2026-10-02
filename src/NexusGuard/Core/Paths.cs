using System.IO;

namespace NexusGuard.Core;

/// <summary>
/// Pastas de dados do NexusGuard. Os dados partilhados ficam em ProgramData; se essa pasta não
/// for gravável (política de grupo, disco protegido), tudo cai para LocalAppData do usuário.
/// </summary>
public static class Paths
{
    public const string AppName = "NexusGuard";

    private static readonly Lazy<string> SharedRootLazy = new(ResolveSharedRoot);

    /// <summary>C:\ProgramData\NexusGuard (ou equivalente local se não houver acesso).</summary>
    public static string SharedRoot => SharedRootLazy.Value;

    /// <summary>%LocalAppData%\NexusGuard — configuração por usuário.</summary>
    public static string UserRoot { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));

    public static string Logs => Ensure(Path.Combine(SharedRoot, "Logs"));

    public static string Quarantine => Ensure(Path.Combine(SharedRoot, "Quarantine"));

    public static string Reports => Ensure(Path.Combine(SharedRoot, "Reports"));

    public static string RegistryBackups => Ensure(Path.Combine(SharedRoot, "RegistryBackups"));

    public static string SettingsFile => Path.Combine(UserRoot, "settings.json");

    public static string HistoryFile => Path.Combine(Logs, "history.jsonl");

    private static string ResolveSharedRoot()
    {
        var programData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName);

        try
        {
            Directory.CreateDirectory(programData);

            // Confirma que dá mesmo para escrever — criar a pasta pode passar e gravar falhar.
            var probe = Path.Combine(programData, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            return programData;
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "Shared");

            try { Directory.CreateDirectory(fallback); } catch { }
            return fallback;
        }
    }

    private static string Ensure(string path)
    {
        try { Directory.CreateDirectory(path); } catch { }
        return path;
    }

    /// <summary>Transforma "C:\Users\x\f.tmp" em "C\Users\x\f.tmp", para salvar sob a quarentena.</summary>
    public static string ToRelativeStorePath(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var rest = fullPath[root.Length..].TrimStart('\\', '/');
        var drive = root.TrimEnd('\\', '/', ':');

        if (drive.Length == 0) drive = "UNC";

        return Path.Combine(drive, rest);
    }

    /// <summary>Caminho original a partir de um caminho relativo da quarentena.</summary>
    public static string FromRelativeStorePath(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar, 2);
        if (parts.Length < 2) return relative;

        return parts[0].Length == 1
            ? parts[0] + ":\\" + parts[1]
            : Path.Combine(parts[0], parts[1]);
    }
}
