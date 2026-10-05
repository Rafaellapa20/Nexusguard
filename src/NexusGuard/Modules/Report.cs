using System.IO;
using NexusGuard.Core;
using static NexusGuard.Core.PdfDocument;

namespace NexusGuard.Modules;

/// <summary>
/// Relatório A4 de manutenção, com antes/depois, as ações realizadas e as recomendações.
/// Pensado para o técnico entregar ao cliente.
/// </summary>
public static class Report
{
    // Paleta clara do relatório (o PDF é para impressão, não segue o tema escuro da app).
    private const string Ink = "#13101F";
    private const string Accent = "#7C5CF6";
    private const string Green = "#059669";
    private const string Amber = "#D97706";
    private const string Grey = "#6A7488";
    private const string CardBg = "#F4F3FB";
    private const string CardBorder = "#E6E3F5";
    private const string Divider = "#EEECF5";

    private const double MarginX = 52;
    private const double MarginTop = 56;
    private const double ContentWidth = PageWidth - MarginX * 2;

    public static Task<string?> GenerateAsync(SystemSnapshot? snapshot, DateTime? since = null) =>
        Task.Run(() => Generate(snapshot, since));

    public static string? Generate(SystemSnapshot? snapshot, DateTime? since = null)
    {
        try
        {
            History.EnsureLoaded();

            var from = since ?? DateTime.Now.AddDays(-30);
            var actions = History.Entries.Where(e => e.When >= from).OrderByDescending(e => e.When).ToList();

            var pdf = new PdfDocument();
            var y = MarginTop;

            y = DrawHeader(pdf, y);
            y = DrawSummaryCards(pdf, y, snapshot, actions, ScoreLog.FirstSince(from));
            y = DrawActions(pdf, y, actions);
            DrawRecommendations(pdf, y, snapshot);
            DrawFooter(pdf);

            var file = Path.Combine(Paths.Reports, $"relatorio-{DateTime.Now:yyyyMMdd-HHmm}.pdf");
            pdf.Save(file);

            Logger.Ok("Relatório", $"Relatório gerado em {file}.");
            History.Add("Relatório", "Relatório de manutenção gerado", Path.GetFileName(file));

            return file;
        }
        catch (Exception ex)
        {
            Logger.Error("Relatório", "Não foi possível gerar o relatório", ex);
            return null;
        }
    }

    private static double DrawHeader(PdfDocument pdf, double y)
    {
        // Marca desenhada em vetor: quadrado arredondado escuro com um escudo em roxo.
        pdf.Rect(MarginX, y, 34, 34, "#13101F");
        pdf.Rect(MarginX + 9, y + 8, 16, 11, Accent);
        pdf.Rect(MarginX + 12, y + 19, 10, 6, Accent);

        pdf.Text("NexusGuard", MarginX + 46, y + 4, 17, Ink, PdfFont.Bold);
        pdf.Text("Relatório de manutenção", MarginX + 46, y + 23, 9.5, Grey);

        var right = PageWidth - MarginX;
        pdf.TextRight(DateTime.Now.ToString("dd/MM/yyyy 'às' HH:mm", Fmt.Pt), right, y + 2, 9, Grey);
        pdf.TextRight(Environment.MachineName, right, y + 14, 9, Grey);
        pdf.TextRight(Fmt.WindowsName, right, y + 26, 9, Grey);

        y += 52;
        pdf.Line(MarginX, y, PageWidth - MarginX, y, CardBorder, 1);

        return y + 22;
    }

    private static double DrawSummaryCards(PdfDocument pdf, double y, SystemSnapshot? snapshot,
        List<HistoryEntry> actions, ScoreSample? earliest)
    {
        const double gap = 12;
        var cardWidth = (ContentWidth - gap * 2) / 3;
        const double cardHeight = 86;

        var freed = EstimateFreed(actions);
        var score = snapshot?.Score ?? 0;

        // O "antes" so aparece se existir mesmo uma medicao anterior. Antes, era deduzido do numero
        // de acoes e impresso riscado ao lado do real, o que fazia o relatorio inventar o proprio
        // ponto de partida — e um relatorio que faz isso nao serve de prova de coisa nenhuma.
        var before = earliest?.Score;

        void Card(double x, string label, string before2, string after, string afterColor)
        {
            pdf.Rect(x, y, cardWidth, cardHeight, CardBg);
            pdf.Rect(x, y, cardWidth, cardHeight, CardBorder, fill: false);

            pdf.Text(label.ToUpperInvariant(), x + 14, y + 14, 8, Grey, PdfFont.Bold);

            if (before2.Length > 0)
            {
                pdf.Text(before2, x + 14, y + 36, 11, Grey, PdfFont.Mono);
                var strike = x + 14 + Width(before2, 11, PdfFont.Mono);
                pdf.Line(x + 14, y + 42, strike, y + 42, Grey, 0.8);
                pdf.Text("→", x + 16 + (strike - x - 14) + 6, y + 36, 11, Grey);
            }

            pdf.Text(after, x + 14, y + 52, 22, afterColor, PdfFont.Mono);
        }

        Card(MarginX, "Saúde do sistema",
            before is { } earlier && earlier != score ? earlier.ToString(Fmt.Pt) : "",
            snapshot?.LastScan is null ? "—" : score.ToString(Fmt.Pt),
            score >= 80 ? Green : score >= 60 ? Amber : "#B91C1C");

        Card(MarginX + cardWidth + gap, "Espaço liberado", "", Fmt.Bytes(freed), Green);

        Card(MarginX + (cardWidth + gap) * 2, "Ações realizadas", "",
            actions.Count.ToString(Fmt.Pt), Accent);

        return y + cardHeight + 26;
    }

    private static double DrawActions(PdfDocument pdf, double y, List<HistoryEntry> actions)
    {
        pdf.Text("Ações realizadas", MarginX, y, 13, Ink, PdfFont.Bold);
        y += 22;

        if (actions.Count == 0)
        {
            pdf.Text("Nenhuma ação registada no período.", MarginX, y, 9.5, Grey);
            return y + 26;
        }

        pdf.Text("MÓDULO", MarginX, y, 7.5, Grey, PdfFont.Bold);
        pdf.Text("DESCRIÇÃO", MarginX + 92, y, 7.5, Grey, PdfFont.Bold);
        pdf.TextRight("VALOR", PageWidth - MarginX, y, 7.5, Grey, PdfFont.Bold);
        y += 13;

        foreach (var action in actions.Take(70))
        {
            if (y > PageHeight - 110)
            {
                pdf.NewPage();
                y = MarginTop;
            }

            pdf.Text(action.Module, MarginX, y, 9, Accent, PdfFont.Bold);

            var description = action.Description.Length > 74
                ? action.Description[..74] + "…"
                : action.Description;

            pdf.Text(description, MarginX + 92, y, 9, Ink);
            pdf.TextRight(action.Value, PageWidth - MarginX, y, 9, Grey, PdfFont.Mono);

            y += 9;
            pdf.Line(MarginX, y, PageWidth - MarginX, y, Divider, 0.5);
            y += 9;
        }

        if (actions.Count > 70)
        {
            pdf.Text($"… e mais {actions.Count - 70} ações no histórico completo.", MarginX, y, 8.5, Grey);
            y += 16;
        }

        return y + 16;
    }

    private static void DrawRecommendations(PdfDocument pdf, double y, SystemSnapshot? snapshot)
    {
        var items = BuildRecommendations(snapshot);
        if (items.Count == 0) return;

        if (y > PageHeight - 160)
        {
            pdf.NewPage();
            y = MarginTop;
        }

        pdf.Text("Atenção recomendada", MarginX, y, 13, Ink, PdfFont.Bold);
        y += 22;

        foreach (var item in items)
        {
            pdf.Rect(MarginX, y + 3, 4, 4, Amber);
            y = pdf.Paragraph(item, MarginX + 14, y, ContentWidth - 14, 9.5, Ink) + 6;
        }
    }

    private static List<string> BuildRecommendations(SystemSnapshot? snapshot)
    {
        var items = new List<string>();

        if (snapshot is null) return items;

        if (snapshot.OutdatedApps > 0)
            items.Add($"{snapshot.OutdatedApps} programa(s) continuam desatualizados — atualizar reduz a superfície de ataque.");

        if (snapshot.PendingDrivers > 0)
            items.Add($"{snapshot.PendingDrivers} driver(s) com atualização pendente no Windows Update.");

        if (snapshot.Threats > 0)
            items.Add($"{snapshot.Threats} ameaça(s) registadas pelo Microsoft Defender — revisar o histórico de deteções.");

        if (snapshot.StartupItems > 6)
            items.Add($"{snapshot.StartupItems} programas arrancam com o Windows; desligar os desnecessários acelera o arranque.");

        if (snapshot.RecoverableBytes > 5L * 1024 * 1024 * 1024)
            items.Add($"Ainda há cerca de {Fmt.Bytes(snapshot.RecoverableBytes)} recuperáveis em caches e temporários.");

        items.Add("Confirme periodicamente o estado S.M.A.R.T. dos discos e mantenha uma cópia de segurança recente.");

        return items;
    }

    private static void DrawFooter(PdfDocument pdf)
    {
        var y = PageHeight - 46;

        pdf.Line(MarginX, y, PageWidth - MarginX, y, CardBorder, 0.8);

        pdf.Text($"Ponto de restauração: NG-{DateTime.Now:yyyyMMdd-HHmm}", MarginX, y + 10, 8, Grey);
        pdf.Text($"Quarentena: {Settings.Current.QuarantineDays} dias", MarginX, y + 21, 8, Grey);

        pdf.TextRight("NexusGuard 1.0.0", PageWidth - MarginX, y + 10, 8, Grey);
        pdf.TextRight(Paths.Reports, PageWidth - MarginX, y + 21, 7.5, Grey);
    }

    /// <summary>Soma os valores "x MB/GB" registados no histórico.</summary>
    private static long EstimateFreed(IEnumerable<HistoryEntry> actions)
    {
        long total = 0;

        foreach (var action in actions)
        {
            var value = action.Value;
            if (string.IsNullOrWhiteSpace(value)) continue;

            var parts = value.Split(' ');
            if (parts.Length != 2) continue;
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Any, Fmt.Pt, out var number)) continue;

            total += parts[1].ToUpperInvariant() switch
            {
                "B" => (long)number,
                "KB" => (long)(number * 1024),
                "MB" => (long)(number * 1024 * 1024),
                "GB" => (long)(number * 1024 * 1024 * 1024),
                "TB" => (long)(number * 1024L * 1024 * 1024 * 1024),
                _ => 0
            };
        }

        return total;
    }


}
