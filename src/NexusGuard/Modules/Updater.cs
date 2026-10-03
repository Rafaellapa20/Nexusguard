using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using NexusGuard.Core;

namespace NexusGuard.Modules;

public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string Notes,
    string DownloadUrl,
    long Size,
    string? ChecksumUrl)
{
    public string SizeText => Fmt.Bytes(Size);
}

public enum UpdateState { UpToDate, Available, Unavailable }

public sealed record UpdateCheck(UpdateState State, UpdateInfo? Update, string Message);

/// <summary>
/// Procura versões novas nas Releases do GitHub, confirma o SHA-256 do instalador e corre-o.
///
/// O hash protege contra um download corrompido ou truncado — não contra um release malicioso:
/// quem controlar o repositório publica o ficheiro e o hash. Só a assinatura de código resolve
/// isso, e por isso a verificação é apresentada ao usuário pelo que é.
/// </summary>
public static class Updater
{
    /// <summary>Dono e nome do repositório que publica as versões.</summary>
    public const string Owner = "Rafaellapa20";

    public const string Repo = "Nexusguard";

    /// <summary>Falso enquanto o repositório for o marcador, para não dar erros de rede sem razão.</summary>
    public static bool IsConfigured => !Owner.StartsWith("SEU-", StringComparison.OrdinalIgnoreCase);

    public static string ReleasesUrl => $"https://github.com/{Owner}/{Repo}/releases";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? new Version(v.Major, v.Minor, v.Build)
            : new Version(1, 0, 0);

    private static string UpdateFolder => Path.Combine(Paths.UserRoot, "updates");

    // ---------------- Resposta da API ----------------

    private sealed class ReleasePayload
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<AssetPayload>? Assets { get; set; }
    }

    private sealed class AssetPayload
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string? Url { get; set; }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("NexusGuard", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }

    // ---------------- Procura ----------------

    public static async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new UpdateCheck(UpdateState.Unavailable,
                null,
                "O repositório de atualizações ainda não está configurado nesta compilação.");

        var channel = Settings.Current.BetaChannel ? "pré-lançamentos incluídos" : "versões estáveis";
        Logger.Info("Atualização", $"Procurando versões novas no GitHub ({channel})…");

        try
        {
            using var client = CreateClient();

            // Com o canal beta é preciso a lista toda; só assim aparecem os pré-lançamentos.
            var url = Settings.Current.BetaChannel
                ? $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=20"
                : $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new UpdateCheck(UpdateState.Unavailable, null,
                    "Ainda não há nenhuma versão publicada no repositório.");

            if (!response.IsSuccessStatusCode)
                return new UpdateCheck(UpdateState.Unavailable, null,
                    $"O GitHub respondeu {(int)response.StatusCode}. Tente mais tarde.");

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return Evaluate(json, Settings.Current.BetaChannel, CurrentVersion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            Logger.Warn("Atualização", $"Sem acesso ao GitHub: {ex.Message}");
            return new UpdateCheck(UpdateState.Unavailable, null,
                "Não foi possível contactar o GitHub. Verifique a conexão.");
        }
        catch (Exception ex)
        {
            Logger.Error("Atualização", "Falha ao procurar versões", ex);
            return new UpdateCheck(UpdateState.Unavailable, null, ex.Message);
        }
    }

    /// <summary>
    /// Escolhe a melhor versão a partir da resposta da API. Separado do acesso à rede para a
    /// decisão poder ser verificada sem depender do GitHub.
    /// </summary>
    public static UpdateCheck Evaluate(string json, bool includePrerelease, Version current)
    {
        List<ReleasePayload> releases;

        try
        {
            // O endpoint /latest devolve um objeto; a listagem devolve um array.
            releases = json.TrimStart().StartsWith('[')
                ? System.Text.Json.JsonSerializer.Deserialize<List<ReleasePayload>>(json, Shell.Json) ?? new()
                : new List<ReleasePayload>
                {
                    System.Text.Json.JsonSerializer.Deserialize<ReleasePayload>(json, Shell.Json)!
                };
        }
        catch (Exception ex)
        {
            Logger.Warn("Atualização", $"Resposta do GitHub ilegível: {ex.Message}");
            return new UpdateCheck(UpdateState.Unavailable, null, "A resposta do GitHub não foi compreendida.");
        }

        var best = releases
            .Where(r => r is { Draft: false })
            .Where(r => includePrerelease || !r.Prerelease)
            .Select(Parse)
            .Where(u => u is not null)
            .OrderByDescending(u => u!.Version)
            .FirstOrDefault();

        if (best is null)
            return new UpdateCheck(UpdateState.Unavailable, null,
                "Nenhuma versão publicada traz um instalador utilizável.");

        if (best.Version <= current)
        {
            Logger.Ok("Atualização", $"Já está na versão mais recente ({current}).");
            return new UpdateCheck(UpdateState.UpToDate, null,
                $"O NexusGuard {current} é a versão mais recente.");
        }

        Logger.Ok("Atualização", $"Versão {best.Version} disponível (atual: {current}).");

        return new UpdateCheck(UpdateState.Available, best,
            $"Versão {best.Version} disponível · {best.SizeText}");
    }

    private static UpdateInfo? Parse(ReleasePayload release)
    {
        var tag = release.TagName?.Trim();
        if (string.IsNullOrWhiteSpace(tag)) return null;

        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        var setup = release.Assets?.FirstOrDefault(a =>
            a.Name is not null &&
            a.Name.StartsWith("NexusGuard-Setup-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        if (setup?.Url is null) return null;

        var checksum = release.Assets?.FirstOrDefault(a =>
            a.Name is not null && a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));

        return new UpdateInfo(
            new Version(version.Major, version.Minor, Math.Max(0, version.Build)),
            tag,
            release.Body?.Trim() ?? string.Empty,
            setup.Url,
            setup.Size,
            checksum?.Url);
    }

    // ---------------- Download ----------------

    /// <summary>Descarrega o instalador e confirma o SHA-256 publicado no release.</summary>
    public static async Task<string?> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(UpdateFolder);

            var target = Path.Combine(UpdateFolder, $"NexusGuard-Setup-{update.Version}.exe");

            onLine?.Invoke($"Baixando {update.SizeText}…");

            using var client = CreateClient();

            using (var response = await client.GetAsync(update.DownloadUrl,
                       HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? update.Size;

                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = File.Create(target);

                var buffer = new byte[81920];
                long done = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                    done += read;
                    if (total > 0) progress?.Report(done * 100.0 / total);
                }
            }

            onLine?.Invoke("Verificando a integridade do arquivo…");

            var expected = await FetchChecksumAsync(client, update, ct).ConfigureAwait(false);
            var actual = await ComputeSha256Async(target, ct).ConfigureAwait(false);

            if (expected is null)
            {
                onLine?.Invoke($"O release não publica SHA256SUMS.txt — não foi possível verificar.");
                onLine?.Invoke($"SHA-256 do arquivo baixado: {actual}");
                Logger.Warn("Atualização", "Release sem ficheiro de somas; integridade não verificada.");
            }
            else if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);

                var message = "O arquivo baixado não corresponde ao hash publicado — foi descartado.";
                onLine?.Invoke(message);
                Logger.Error("Atualização", $"{message} Esperado {expected}, obtido {actual}.");

                return null;
            }
            else
            {
                onLine?.Invoke("Integridade confirmada.");
                Logger.Ok("Atualização", $"SHA-256 confere ({actual[..16]}…).");
            }

            return target;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error("Atualização", "Falha ao baixar a atualização", ex);
            onLine?.Invoke($"Falhou: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> FetchChecksumAsync(HttpClient client, UpdateInfo update, CancellationToken ct)
    {
        if (update.ChecksumUrl is null) return null;

        try
        {
            var text = await client.GetStringAsync(update.ChecksumUrl, ct).ConfigureAwait(false);
            var wanted = Path.GetFileName(new Uri(update.DownloadUrl).LocalPath);

            return FindHash(text, wanted);
        }
        catch (Exception ex)
        {
            Logger.Warn("Atualização", $"Não foi possível ler o ficheiro de somas: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Procura o hash de um ficheiro num SHA256SUMS no formato do sha256sum:
    /// "&lt;hash&gt;  &lt;nome&gt;", com o asterisco opcional do modo binário.
    /// </summary>
    public static string? FindHash(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var name = parts[^1].TrimStart('*');

            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return parts[0];
        }

        return null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);

        return Convert.ToHexString(hash);
    }

    // ---------------- Instalação ----------------

    /// <summary>
    /// Corre o instalador baixado e devolve o controlo ao chamador para encerrar a aplicação —
    /// o setup não consegue substituir o executável com ele em uso.
    /// </summary>
    public static bool Install(string setupPath, out string message)
    {
        if (!File.Exists(setupPath))
        {
            message = "O instalador baixado já não está no disco.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(setupPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART"
            });

            History.Add("Atualização", "Instalador de atualização iniciado", Path.GetFileName(setupPath));
            Logger.Ok("Atualização", $"Instalador {Path.GetFileName(setupPath)} iniciado.");

            message = "O instalador vai continuar sozinho. O NexusGuard fecha-se agora.";
            return true;
        }
        catch (Exception ex)
        {
            // O usuário pode ter recusado o UAC.
            message = $"Não foi possível iniciar o instalador: {ex.Message}";
            Logger.Warn("Atualização", message);
            return false;
        }
    }

    /// <summary>Apaga instaladores já usados, para a pasta não crescer sem limite.</summary>
    public static void CleanOldDownloads()
    {
        try
        {
            if (!Directory.Exists(UpdateFolder)) return;

            foreach (var file in Directory.EnumerateFiles(UpdateFolder, "NexusGuard-Setup-*.exe"))
            {
                try
                {
                    var info = new FileInfo(file);
                    if ((DateTime.Now - info.LastWriteTime).TotalDays > 7) info.Delete();
                }
                catch
                {
                    // Ficheiro em uso: fica para a próxima.
                }
            }
        }
        catch
        {
            // Limpeza best-effort.
        }
    }
}
