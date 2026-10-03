using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NexusGuard.Core;

/// <summary>Uma pontuação de saúde medida num instante.</summary>
public sealed record ScoreSample(DateTime When, int Score);

/// <summary>
/// Histórico das pontuações de saúde.
///
/// Existe por causa do relatório. Antes desta classe, o "antes" do relatório era calculado a partir
/// do número de ações — um número inventado, impresso riscado ao lado do real como se fosse uma
/// medição anterior. Um relatório que fabrica o seu próprio ponto de partida não serve para nada,
/// e numa ferramenta que se vende é pior do que não ter relatório.
///
/// Agora cada análise concluída deixa aqui uma marca, e o relatório compara com a primeira do
/// período. Não havendo nenhuma anterior, não há comparação para mostrar.
/// </summary>
public static class ScoreLog
{
    /// <summary>Uma marca por análise concluída chega a milhares ao fim de anos; isto corta a cauda.</summary>
    private const int MaxSamples = 2000;

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string FilePath => Path.Combine(Paths.SharedRoot, "scores.jsonl");

    /// <summary>Regista uma pontuação. Falhar aqui nunca deve estragar a análise que a produziu.</summary>
    public static void Record(int score)
    {
        if (score is < 0 or > 100) return;

        try
        {
            lock (Gate)
            {
                var line = JsonSerializer.Serialize(new ScoreSample(DateTime.Now, score), Json);

                // Sem BOM: o ficheiro é lido linha a linha, e um BOM colado à primeira linha
                // transforma-a em JSON inválido.
                File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Pontuação", $"não foi possível registar a pontuação — {ex.Message}");
        }
    }

    /// <summary>Todas as marcas conhecidas, da mais antiga para a mais recente.</summary>
    public static List<ScoreSample> All()
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath)) return new List<ScoreSample>();

                var samples = new List<ScoreSample>();

                foreach (var line in File.ReadLines(FilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        if (JsonSerializer.Deserialize<ScoreSample>(line, Json) is { } sample)
                            samples.Add(sample);
                    }
                    catch (JsonException)
                    {
                        // Uma linha truncada por um desligar a meio não deve perder o resto.
                    }
                }

                samples.Sort((a, b) => a.When.CompareTo(b.When));

                if (samples.Count > MaxSamples)
                {
                    samples = samples.Skip(samples.Count - MaxSamples).ToList();
                    Rewrite(samples);
                }

                return samples;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Pontuação", $"não foi possível ler o histórico de pontuações — {ex.Message}");
            return new List<ScoreSample>();
        }
    }

    /// <summary>
    /// A primeira pontuação medida a partir de uma data, que é o "antes" honesto de um relatório.
    /// Nula quando não há nenhuma medição desse período — e aí o relatório não mostra comparação.
    /// </summary>
    public static ScoreSample? FirstSince(DateTime since)
    {
        foreach (var sample in All())
            if (sample.When >= since) return sample;

        return null;
    }

    private static void Rewrite(List<ScoreSample> samples)
    {
        try
        {
            var text = new StringBuilder();
            foreach (var sample in samples) text.AppendLine(JsonSerializer.Serialize(sample, Json));

            File.WriteAllText(FilePath, text.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Logger.Warn("Pontuação", $"não foi possível compactar o histórico — {ex.Message}");
        }
    }
}
