using System.IO;
using Microsoft.Win32;
using NexusGuard.Core;

namespace NexusGuard.Modules;

/// <summary>
/// Regras de limpeza por aplicação, para além das categorias de sistema do <see cref="DiskCleaner"/>.
///
/// Escritas de raiz a partir da documentação de cada aplicação e das pastas que ela cria. Não
/// derivam do BleachBit: esse projecto é GPL-3.0, e tanto o código como as definições de limpeza
/// dele obrigariam o NexusGuard inteiro a ser distribuído sob a mesma licença. Ver TERCEIROS.md.
///
/// Duas regras que nenhuma destas entradas quebra:
///
/// 1. Só entra o que a aplicação <b>regenera sozinha</b> — caches de imagens, de código compilado,
///    registos de diagnóstico. Nunca perfis, definições, sessões iniciadas, histórico ou qualquer
///    pasta onde o utilizador possa ter trabalho.
/// 2. Uma aplicação que não está instalada não aparece na lista. Mostrar dez entradas a zero obriga
///    quem está a usar a ler tudo para perceber que não há nada ali.
/// </summary>
public static class AppCleaners
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Uma regra antes de se saber se a aplicação existe nesta máquina.</summary>
    private sealed record Rule(
        string Name,
        string Description,
        string[] Folders,
        CleanRisk Risk = CleanRisk.Safe,
        TimeSpan? MinimumAge = null);

    /// <summary>
    /// Alvos para as aplicações encontradas. A ordem é a de instalação mais provável, para as mais
    /// comuns ficarem no cimo da lista.
    /// </summary>
    public static List<CleanTarget> BuildTargets()
    {
        var targets = new List<CleanTarget>();

        foreach (var rule in Rules())
        {
            var present = rule.Folders.Where(DirectoryHasContent).ToList();
            if (present.Count == 0) continue;

            targets.Add(new CleanTarget
            {
                Name = rule.Name,
                Description = rule.Description,
                Risk = rule.Risk,
                MinimumAge = rule.MinimumAge,
                Folders = present
            });
        }

        if (targets.Count > 0)
            Logger.Info("Limpeza", $"{targets.Count} aplicativos com cache própria encontrados.");

        return targets;
    }

    private static IEnumerable<Rule> Rules()
    {
        // ---- Aplicações Electron: a cache é sempre a mesma árvore, com nomes fixos. ----
        yield return Electron("Discord", Path.Combine(Roaming, "discord"));
        yield return Electron("Slack", Path.Combine(Roaming, "Slack"));
        yield return Electron("Microsoft Teams", Path.Combine(Roaming, @"Microsoft\Teams"));
        yield return Electron("WhatsApp", Path.Combine(Roaming, "WhatsApp"));

        yield return new Rule(
            "Cache do Visual Studio Code",
            "Código compilado e registos que o editor volta a gerar. Extensões e definições ficam.",
            new[]
            {
                Path.Combine(Roaming, @"Code\Cache"),
                Path.Combine(Roaming, @"Code\CachedData"),
                Path.Combine(Roaming, @"Code\Code Cache"),
                Path.Combine(Roaming, @"Code\GPUCache"),
                Path.Combine(Roaming, @"Code\logs")
            });

        yield return new Rule(
            "Cache do Spotify",
            "Músicas guardadas para tocar sem rede. São voltadas a transferir quando precisar delas.",
            new[]
            {
                Path.Combine(Local, @"Spotify\Data"),
                Path.Combine(Local, @"Spotify\Storage")
            },
            CleanRisk.Moderate);

        yield return new Rule(
            "Registos do Zoom",
            "Registos de diagnóstico das reuniões. Não afeta contas nem gravações.",
            new[] { Path.Combine(Roaming, @"Zoom\logs") });

        yield return new Rule(
            "Cache do Epic Games",
            "Cache web e registos do lançador. Os jogos instalados não são tocados.",
            new[]
            {
                Path.Combine(Local, @"EpicGamesLauncher\Saved\webcache"),
                Path.Combine(Local, @"EpicGamesLauncher\Saved\webcache_4147"),
                Path.Combine(Local, @"EpicGamesLauncher\Saved\Logs")
            });

        yield return new Rule(
            "Cache de mídia do Adobe",
            "Pré-visualizações que o Premiere e o After Effects regeneram ao abrir o projeto. "
          + "A primeira abertura depois disto é mais lenta.",
            new[]
            {
                Path.Combine(Local, @"Adobe\Common\Media Cache Files"),
                Path.Combine(Local, @"Adobe\Common\Media Cache"),
                Path.Combine(Local, @"Adobe\Common\Peak Files")
            },
            CleanRisk.Moderate);

        yield return new Rule(
            "Instaladores do NVIDIA",
            "Pacotes de drivers já instalados, guardados para uma reinstalação que raramente acontece.",
            new[] { Path.Combine(Local, @"NVIDIA Corporation\Downloader") },
            CleanRisk.Safe,
            TimeSpan.FromDays(7));

        foreach (var steam in SteamRules()) yield return steam;
    }

    /// <summary>
    /// As aplicações feitas em Electron guardam tudo nos mesmos quatro sítios. Nenhum deles contém
    /// sessão iniciada nem definições — essas vivem em <c>Local Storage</c>, que fica de fora.
    /// </summary>
    private static Rule Electron(string app, string root) => new(
        $"Cache do {app}",
        $"Imagens e código em cache do {app}. A sessão iniciada e as definições não são tocadas.",
        new[]
        {
            Path.Combine(root, "Cache"),
            Path.Combine(root, "Code Cache"),
            Path.Combine(root, "GPUCache"),
            Path.Combine(root, "blob_storage")
        });

    /// <summary>
    /// O Steam pode estar em qualquer disco, por isso o caminho vem do registo. A cache de
    /// sombreadores dele chega facilmente a dezenas de GB em bibliotecas grandes.
    /// </summary>
    private static IEnumerable<Rule> SteamRules()
    {
        var root = SteamPath();
        if (root is null) yield break;

        yield return new Rule(
            "Cache de sombreadores do Steam",
            "Sombreadores pré-compilados dos jogos. São refeitos no primeiro arranque de cada jogo, "
          + "que fica mais lento uma vez.",
            new[] { Path.Combine(root, "steamapps", "shadercache") },
            CleanRisk.Moderate);

        yield return new Rule(
            "Cache e registos do Steam",
            "Cache da loja e registos do cliente. Jogos, saves e sessão iniciada ficam intactos.",
            new[]
            {
                Path.Combine(root, "appcache", "httpcache"),
                Path.Combine(root, "logs")
            });
    }

    private static string? SteamPath()
    {
        try
        {
            // A chave de 32 bits existe nos dois tipos de instalação; a de 64 só nas mais recentes.
            foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
            {
                using var reg = Registry.LocalMachine.OpenSubKey(key);
                if (reg?.GetValue("InstallPath") is string path && Directory.Exists(path)) return path;
            }

            using var user = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (user?.GetValue("SteamPath") is string userPath && Directory.Exists(userPath)) return userPath;
        }
        catch (Exception ex)
        {
            Logger.Warn("Limpeza", $"não foi possível localizar o Steam — {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Existir não chega: uma pasta de cache vazia não vale uma linha na lista. Só olha ao primeiro
    /// nível, porque percorrer árvores inteiras aqui atrasaria a montagem da página.
    /// </summary>
    private static bool DirectoryHasContent(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            return Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }
}
