using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NexusGuard.Core;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    public string All => string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdOut + Environment.NewLine + StdErr;
}

/// <summary>Execução de processos externos e de scripts PowerShell com saída em JSON.</summary>
public static class Shell
{
    /// <summary>Codificação usada pelas ferramentas de consola nativas deste Windows.</summary>
    public static Encoding ConsoleEncoding { get; } = ResolveConsoleEncoding();

    /// <summary>
    /// As ferramentas de consola do Windows (powercfg, robocopy, DISM, sfc) escrevem na página de
    /// códigos OEM, não em UTF-8. O fornecedor de páginas de código é registado aqui e não num
    /// construtor estático — esse correria depois deste inicializador e chegaria tarde.
    /// </summary>
    private static Encoding ResolveConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Corre um executável, transmitindo cada linha de saída a onLine.
    /// <paramref name="encoding"/> permite forçar UTF-8 para ferramentas que não usam a página OEM.
    /// </summary>
    public static async Task<ProcResult> RunAsync(string exe, string args, Action<string>? onLine = null,
        CancellationToken ct = default, string? workingDirectory = null, Encoding? encoding = null)
    {
        var enc = encoding ?? ConsoleEncoding;

        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = enc,
            StandardErrorEncoding = enc,
            WorkingDirectory = workingDirectory ?? Environment.SystemDirectory
        };

        var outBuf = new StringBuilder();
        var errBuf = new StringBuilder();

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            outBuf.AppendLine(e.Data);
            if (e.Data.Length > 0) onLine?.Invoke(e.Data);
        };

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            errBuf.AppendLine(e.Data);
            if (e.Data.Length > 0) onLine?.Invoke(e.Data);
        };

        try
        {
            if (!proc.Start())
                return new ProcResult(-1, string.Empty, $"Não foi possível iniciar {exe}");
        }
        catch (Exception ex)
        {
            return new ProcResult(-1, string.Empty, $"{exe}: {ex.Message}");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }

        return new ProcResult(proc.ExitCode, outBuf.ToString(), errBuf.ToString());
    }

    private static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ignorado: o processo pode ter terminado entretanto.
        }
    }

    private static string PowerShellPath
    {
        get
        {
            var sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(sys) ? sys : "powershell.exe";
        }
    }

    /// <summary>Escreve o script num arquivo temporario e corre-o (evita problemas de escape na linha de comandos).</summary>
    public static async Task<ProcResult> PowerShellAsync(string script, Action<string>? onLine = null,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusGuard");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"ps-{Guid.NewGuid():N}.ps1");

        var header = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'Stop'")
            .AppendLine("$ProgressPreference = 'SilentlyContinue'")
            .AppendLine("try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch {}")
            .AppendLine("try { $OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch {}")
            .AppendLine()
            .ToString();

        try
        {
            await File.WriteAllTextAsync(file, header + script, new UTF8Encoding(true), ct).ConfigureAwait(false);
            // O cabeçalho do script força UTF-8 na saída do PowerShell.
            return await RunAsync(PowerShellPath,
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"",
                onLine, ct, null, new UTF8Encoding(false)).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    /// <summary>Corre um script que escreve JSON em stdout e desserializa o resultado.</summary>
    public static async Task<T?> PowerShellJsonAsync<T>(string script, CancellationToken ct = default)
    {
        var res = await PowerShellAsync(script, null, ct).ConfigureAwait(false);
        var text = ExtractJson(res.StdOut);

        if (string.IsNullOrWhiteSpace(text))
        {
            if (!res.Success && !string.IsNullOrWhiteSpace(res.StdErr))
                Logger.Warn("PowerShell", res.StdErr.Trim().Split('\n')[0]);
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, Json);
        }
        catch (Exception ex)
        {
            Logger.Warn("PowerShell", $"JSON inválido: {ex.Message}");
            return default;
        }
    }

    /// <summary>Isola o bloco JSON na saída (ignora avisos escritos antes dele).</summary>
    private static string? ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.Trim().TrimStart('﻿');
        var start = s.IndexOfAny(new[] { '{', '[' });
        if (start < 0) return null;

        var end = s.LastIndexOfAny(new[] { '}', ']' });
        if (end <= start) return null;

        return s.Substring(start, end - start + 1);
    }

    /// <summary>Abre um caminho, URL ou painel do Windows com o aplicativo associada.</summary>
    public static void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn("Shell", $"Não foi possível abrir '{target}': {ex.Message}");
        }
    }

    /// <summary>Procura um executavel no PATH e nas pastas do sistema.</summary>
    public static string? Which(string exeName)
    {
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim('"'), exeName)); } catch { }
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exeName));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exeName));

        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }

        return null;
    }
}
