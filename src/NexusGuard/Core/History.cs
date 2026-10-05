using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusGuard.Core;

/// <summary>O que é preciso guardar para conseguir reverter uma ação.</summary>
public enum UndoKind
{
    None,
    QuarantineBatch,
    RegistryExport,
    StartupItem,
    RegistryValue,
    PowerPlan
}

public sealed class HistoryEntry : Observable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime When { get; set; } = DateTime.Now;
    public string Module { get; set; } = "";
    public string Description { get; set; } = "";
    public string Value { get; set; } = "";
    public UndoKind Undo { get; set; } = UndoKind.None;

    /// <summary>Dados próprios de cada tipo de desfazer (caminho do lote, chave do registo, etc.).</summary>
    public Dictionary<string, string> Data { get; set; } = new();

    private bool _undone;
    public bool Undone { get => _undone; set => Set(ref _undone, value); }

    [JsonIgnore]
    public bool CanUndo => Undo != UndoKind.None && !Undone;

    [JsonIgnore]
    public string WhenText => When.ToString("dd/MM/yyyy HH:mm", Fmt.Pt);

    [JsonIgnore]
    public string StatusText => Undone ? "Desfeito" : string.Empty;
}

/// <summary>
/// Histórico estruturado das ações, em JSON Lines. É a fonte do módulo Histórico, do relatório
/// e do botão «Desfazer».
/// </summary>
public static class History
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Acentos ficam legiveis no ficheiro em vez de escapados.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>UTF-8 sem BOM: um JSON Lines com BOM parte as ferramentas que o leiam linha a linha.</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static ObservableCollection<HistoryEntry> Entries { get; } = new();

    private static bool _loaded;

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!File.Exists(Paths.HistoryFile)) return;

            foreach (var line in File.ReadLines(Paths.HistoryFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var entry = JsonSerializer.Deserialize<HistoryEntry>(line, JsonOptions);
                    if (entry is not null) Entries.Add(entry);
                }
                catch
                {
                    // Linha corrompida: o resto do histórico continua a ser útil.
                }
            }

            Sort();
        }
        catch (Exception ex)
        {
            Logger.Warn("Histórico", $"Não foi possível ler o histórico: {ex.Message}");
        }
    }

    private static void Sort()
    {
        var ordered = Entries.OrderByDescending(e => e.When).ToList();
        Entries.Clear();
        foreach (var e in ordered) Entries.Add(e);
    }

    public static HistoryEntry Add(string module, string description, string value = "",
        UndoKind undo = UndoKind.None, Dictionary<string, string>? data = null)
    {
        EnsureLoaded();

        var entry = new HistoryEntry
        {
            Module = module,
            Description = description,
            Value = value,
            Undo = undo,
            Data = data ?? new Dictionary<string, string>()
        };

        Append(entry);

        var app = System.Windows.Application.Current;
        if (app?.Dispatcher is { } d && !d.CheckAccess()) d.BeginInvoke(() => Entries.Insert(0, entry));
        else Entries.Insert(0, entry);

        return entry;
    }

    private static void Append(HistoryEntry entry)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Logs);
                File.AppendAllText(Paths.HistoryFile,
                    JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine, Utf8NoBom);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Histórico", $"Não foi possível gravar no histórico: {ex.Message}");
        }
    }

    /// <summary>Reescreve o ficheiro inteiro — usado depois de marcar uma entrada como desfeita.</summary>
    public static void Persist()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Logs);
                var temp = Paths.HistoryFile + ".tmp";

                using (var writer = new StreamWriter(temp, false, Utf8NoBom))
                {
                    foreach (var entry in Entries.OrderBy(e => e.When))
                        writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
                }

                File.Move(temp, Paths.HistoryFile, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Histórico", $"Não foi possível atualizar o histórico: {ex.Message}");
        }
    }

    public static void MarkUndone(HistoryEntry entry)
    {
        entry.Undone = true;
        Persist();
    }

    public static IEnumerable<HistoryEntry> Since(DateTime from) => Entries.Where(e => e.When >= from);
}
