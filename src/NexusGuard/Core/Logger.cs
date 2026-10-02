using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;

namespace NexusGuard.Core;

public enum LogLevel { Info, Ok, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    public string LevelText => Level switch
    {
        LogLevel.Ok => "OK",
        LogLevel.Warn => "AVISO",
        LogLevel.Error => "ERRO",
        _ => "INFO"
    };
}

/// <summary>Registro central: arquivo em %LocalAppData%\NexusGuard\logs e lista observavel para a UI.</summary>
public static class Logger
{
    private static readonly object Gate = new();

    /// <summary>UTF-8 sem BOM, para o registo poder ser lido por qualquer ferramenta.</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>Fica ao lado do historico, em ProgramData\NexusGuard\Logs.</summary>
    public static string LogDirectory => Paths.Logs;

    public static string LogFile => Path.Combine(LogDirectory, $"nexusguard-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string source, string message) => Write(LogLevel.Info, source, message);

    public static void Ok(string source, string message) => Write(LogLevel.Ok, source, message);

    public static void Warn(string source, string message) => Write(LogLevel.Warn, source, message);

    public static void Error(string source, string message) => Write(LogLevel.Error, source, message);

    public static void Error(string source, string message, Exception ex) =>
        Write(LogLevel.Error, source, $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, source, message);

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogFile,
                    $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.LevelText,-5}] {source}: {message}{Environment.NewLine}",
                    Utf8NoBom);
            }
        }
        catch
        {
            // O registro nunca deve derrubar a app.
        }

        var app = Application.Current;
        if (app?.Dispatcher is null) return;

        if (app.Dispatcher.CheckAccess()) Add(entry);
        else app.Dispatcher.BeginInvoke(() => Add(entry));
    }

    private static void Add(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > 2000) Entries.RemoveAt(0);
    }
}
