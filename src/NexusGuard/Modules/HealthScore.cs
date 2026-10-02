using NexusGuard.Core;

namespace NexusGuard.Modules;

/// <summary>Os números que alimentam a pontuação de saúde e os cartões da visão geral.</summary>
public sealed class SystemSnapshot : Observable
{
    private int _outdatedApps;
    public int OutdatedApps { get => _outdatedApps; set { if (Set(ref _outdatedApps, value)) Recompute(); } }

    private int _pendingDrivers;
    public int PendingDrivers { get => _pendingDrivers; set { if (Set(ref _pendingDrivers, value)) Recompute(); } }

    private long _recoverableBytes;
    public long RecoverableBytes
    {
        get => _recoverableBytes;
        set { if (Set(ref _recoverableBytes, value)) { Raise(nameof(RecoverableText)); Recompute(); } }
    }

    private int _registryIssues;
    public int RegistryIssues { get => _registryIssues; set { if (Set(ref _registryIssues, value)) Recompute(); } }

    private int _threats;
    public int Threats { get => _threats; set { if (Set(ref _threats, value)) Recompute(); } }

    private int _startupItems;
    public int StartupItems { get => _startupItems; set { if (Set(ref _startupItems, value)) Recompute(); } }

    private DateTime? _lastScan;
    public DateTime? LastScan { get => _lastScan; set { if (Set(ref _lastScan, value)) Raise(nameof(LastScanText)); } }

    private int _score = 100;
    public int Score
    {
        get => _score;
        private set
        {
            if (!Set(ref _score, value)) return;
            Raise(nameof(ScoreText));
            Raise(nameof(ScoreLabel));
        }
    }

    public string ScoreText => Score.ToString(Fmt.Pt);

    public string ScoreLabel => Score switch
    {
        >= 80 => "Boa",
        >= 60 => "Razoável",
        _ => "Precisa de atenção"
    };

    public string RecoverableText => RecoverableBytes > 0 ? Fmt.Bytes(RecoverableBytes) : "0 B";

    public string LastScanText
    {
        get
        {
            if (LastScan is null) return "ainda não analisado";

            var minutes = (int)(DateTime.Now - LastScan.Value).TotalMinutes;
            return minutes switch
            {
                < 1 => "agora mesmo",
                < 60 => $"há {minutes} min",
                < 1440 => $"há {minutes / 60} h",
                _ => $"há {minutes / 1440} dia(s)"
            };
        }
    }

    /// <summary>Quantos pontos merecem atenção — mostrado ao lado do título da visão geral.</summary>
    public int AttentionCount =>
        (OutdatedApps > 0 ? 1 : 0) +
        (PendingDrivers > 0 ? 1 : 0) +
        (RecoverableBytes > 1L * 1024 * 1024 * 1024 ? 1 : 0) +
        (RegistryIssues > 0 ? 1 : 0) +
        (Threats > 0 ? 1 : 0) +
        (StartupItems > 6 ? 1 : 0);

    private void Recompute() => Score = HealthScore.Compute(this);
}

/// <summary>Fórmula da pontuação de saúde, com os pesos definidos no handoff.</summary>
public static class HealthScore
{
    public static int Compute(SystemSnapshot s)
    {
        var gb = s.RecoverableBytes / 1024.0 / 1024.0 / 1024.0;

        var score = 100.0
                    - s.OutdatedApps * 2.0
                    - s.PendingDrivers * 4.0
                    - gb / 2.5
                    - s.StartupItems * 2.0
                    - s.Threats * 6.0
                    - s.RegistryIssues / 80.0;

        return (int)Math.Round(Math.Clamp(score, 0, 100));
    }
}
