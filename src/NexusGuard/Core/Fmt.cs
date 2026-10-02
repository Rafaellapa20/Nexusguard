using System.Globalization;
using System.Security.Principal;

namespace NexusGuard.Core;

/// <summary>Formatacao de valores e verificações de contexto (administrador, versão, cultura).</summary>
public static class Fmt
{
    public static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>Formata bytes com a unidade adequada (ex.: "1,4 GB").</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes == 0) return "0 B";

        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }

        var decimals = i == 0 ? 0 : (v < 10 ? 2 : (v < 100 ? 1 : 0));
        return string.Create(Pt, $"{Math.Round(v, decimals)} {Units[i]}");
    }

    public static string Bytes(ulong bytes) => Bytes((long)Math.Min(bytes, long.MaxValue));

    public static string Percent(double value, int decimals = 0) =>
        Math.Round(value, decimals).ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), Pt) + "%";

    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return string.Create(Pt, $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}min");
        if (t.TotalHours >= 1) return string.Create(Pt, $"{(int)t.TotalHours}h {t.Minutes}min");
        if (t.TotalMinutes >= 1) return string.Create(Pt, $"{(int)t.TotalMinutes}min {t.Seconds}s");
        return string.Create(Pt, $"{t.Seconds}s");
    }

    public static string Count(long n) => n.ToString("N0", Pt);

    /// <summary>True quando o processo corre com privilégios de administrador.</summary>
    public static bool IsAdmin
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public static string WindowsName
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var product = key?.GetValue("ProductName") as string ?? "Windows";
                var display = key?.GetValue("DisplayVersion") as string;
                var build = key?.GetValue("CurrentBuild") as string;

                // O Windows 11 continua a reportar-se como "Windows 10" na chave ProductName.
                if (int.TryParse(build, out var b) && b >= 22000 && product.Contains("Windows 10"))
                    product = product.Replace("Windows 10", "Windows 11");

                var parts = new List<string> { product };
                if (!string.IsNullOrWhiteSpace(display)) parts.Add(display);
                if (!string.IsNullOrWhiteSpace(build)) parts.Add($"(build {build})");
                return string.Join(' ', parts);
            }
            catch
            {
                return Environment.OSVersion.VersionString;
            }
        }
    }
}
