using System.Globalization;
using System.IO;
using System.Text;

namespace NexusGuard.Core;

/// <summary>
/// Escritor de PDF mínimo, só com o que o relatório precisa: texto nas fontes base (sem incorporar
/// arquivos), retângulos e linhas. Evita trazer uma biblioteca de PDF para o aplicativo.
/// </summary>
public sealed class PdfDocument
{
    public enum PdfFont { Regular, Bold, Mono }

    private readonly List<string> _pages = new();
    private readonly StringBuilder _content = new();

    /// <summary>A4 em pontos (72 dpi).</summary>
    public const double PageWidth = 595.28;

    public const double PageHeight = 841.89;

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Converte um Y medido a partir do topo para o sistema do PDF (origem em baixo).</summary>
    private static double Y(double fromTop) => PageHeight - fromTop;

    public void Rect(double x, double top, double width, double height, string hex, bool fill = true)
    {
        var (r, g, b) = Rgb(hex);
        _content.AppendLine(fill
            ? $"{N(r)} {N(g)} {N(b)} rg"
            : $"{N(r)} {N(g)} {N(b)} RG");
        _content.AppendLine($"{N(x)} {N(Y(top + height))} {N(width)} {N(height)} re {(fill ? "f" : "S")}");
    }

    public void Line(double x1, double top1, double x2, double top2, string hex, double width = 0.6)
    {
        var (r, g, b) = Rgb(hex);
        _content.AppendLine($"{N(r)} {N(g)} {N(b)} RG {N(width)} w");
        _content.AppendLine($"{N(x1)} {N(Y(top1))} m {N(x2)} {N(Y(top2))} l S");
    }

    public void Text(string text, double x, double top, double size, string hex = "#13101F",
        PdfFont font = PdfFont.Regular)
    {
        if (string.IsNullOrEmpty(text)) return;

        var (r, g, b) = Rgb(hex);
        var resource = font switch
        {
            PdfFont.Bold => "/F2",
            PdfFont.Mono => "/F3",
            _ => "/F1"
        };

        _content.AppendLine("BT");
        _content.AppendLine($"{resource} {N(size)} Tf");
        _content.AppendLine($"{N(r)} {N(g)} {N(b)} rg");
        _content.AppendLine($"{N(x)} {N(Y(top + size))} Td");
        _content.AppendLine($"({Escape(text)}) Tj");
        _content.AppendLine("ET");
    }

    /// <summary>Texto alinhado à direita de <paramref name="right"/>, estimando a largura da fonte.</summary>
    public void TextRight(string text, double right, double top, double size, string hex = "#13101F",
        PdfFont font = PdfFont.Regular)
    {
        Text(text, right - Width(text, size, font), top, size, hex, font);
    }

    /// <summary>Largura aproximada — chega para alinhar à direita sem métricas incorporadas.</summary>
    public static double Width(string text, double size, PdfFont font = PdfFont.Regular)
    {
        var factor = font switch
        {
            PdfFont.Mono => 0.60,
            PdfFont.Bold => 0.55,
            _ => 0.50
        };

        return text.Length * size * factor;
    }

    /// <summary>Escreve texto quebrando por palavras; devolve o Y seguinte.</summary>
    public double Paragraph(string text, double x, double top, double maxWidth, double size,
        string hex = "#13101F", PdfFont font = PdfFont.Regular, double lineHeight = 1.45)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();
        var y = top;

        foreach (var word in words)
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";

            if (Width(candidate, size, font) > maxWidth && line.Length > 0)
            {
                Text(line.ToString(), x, y, size, hex, font);
                y += size * lineHeight;
                line.Clear().Append(word);
            }
            else
            {
                line.Clear().Append(candidate);
            }
        }

        if (line.Length > 0)
        {
            Text(line.ToString(), x, y, size, hex, font);
            y += size * lineHeight;
        }

        return y;
    }

    /// <summary>Fecha a página atual e começa uma nova.</summary>
    public void NewPage()
    {
        _pages.Add(_content.ToString());
        _content.Clear();
    }

    public int PageCount => _pages.Count + (_content.Length > 0 ? 1 : 0);

    public void Save(string path)
    {
        if (_content.Length > 0) NewPage();

        var objects = new List<byte[]>();

        // 1 catálogo · 2 páginas · 3..n páginas e conteúdos · fontes no fim.
        var pageCount = _pages.Count;
        var firstPageObj = 3;
        var fontObj = firstPageObj + pageCount * 2;

        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{firstPageObj + i * 2} 0 R"));

        objects.Add(Latin1("<< /Type /Catalog /Pages 2 0 R >>"));
        objects.Add(Latin1($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>"));

        for (var i = 0; i < pageCount; i++)
        {
            var contentObj = firstPageObj + i * 2 + 1;

            objects.Add(Latin1(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] " +
                $"/Resources << /Font << /F1 {fontObj} 0 R /F2 {fontObj + 1} 0 R /F3 {fontObj + 2} 0 R >> >> " +
                $"/Contents {contentObj} 0 R >>"));

            var stream = Latin1(_pages[i]);
            var header = Latin1($"<< /Length {stream.Length} >>\nstream\n");
            var footer = Latin1("\nendstream");

            objects.Add(header.Concat(stream).Concat(footer).ToArray());
        }

        objects.Add(Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        objects.Add(Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));
        objects.Add(Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>"));

        using var file = File.Create(path);
        var offsets = new List<int>();
        var position = 0;

        void Write(byte[] data)
        {
            file.Write(data, 0, data.Length);
            position += data.Length;
        }

        Write(Latin1("%PDF-1.4\n%âãÏÓ\n"));

        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(position);
            Write(Latin1($"{i + 1} 0 obj\n"));
            Write(objects[i]);
            Write(Latin1("\nendobj\n"));
        }

        var xref = position;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");

        foreach (var offset in offsets)
            sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");

        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF");

        Write(Latin1(sb.ToString()));
    }

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);

        foreach (var c in text)
        {
            switch (c)
            {
                case '(': sb.Append("\\("); break;
                case ')': sb.Append("\\)"); break;
                case '\\': sb.Append("\\\\"); break;
                case '\r':
                case '\n': sb.Append(' '); break;
                default:
                    // As fontes base usam WinAnsi; o que não couber vira "?" em vez de corromper o arquivo.
                    sb.Append(c <= 'ÿ' ? c : '?');
                    break;
            }
        }

        return sb.ToString();
    }

    private static (double r, double g, double b) Rgb(string hex)
    {
        hex = hex.TrimStart('#');

        if (hex.Length != 6) return (0, 0, 0);

        return (
            Convert.ToInt32(hex[..2], 16) / 255.0,
            Convert.ToInt32(hex.Substring(2, 2), 16) / 255.0,
            Convert.ToInt32(hex.Substring(4, 2), 16) / 255.0);
    }
}
