using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace NexusGuard.Core;

/// <summary>Preferências do usuário, gravadas em %LocalAppData%\NexusGuard\settings.json.</summary>
public sealed class Settings : Observable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static Settings? _current;

    public static Settings Current => _current ??= Load();

    /// <summary>Forma gravada em disco. Separada da classe observável para a leitura não disparar escritas.</summary>
    private sealed class Dto
    {
        public bool FirstRunCompleted { get; set; }
        public bool CreateRestorePoint { get; set; } = true;
        public bool UseQuarantine { get; set; } = true;
        public bool ConfirmEachChange { get; set; } = true;
        public bool WhqlDriversOnly { get; set; } = true;
        public bool Telemetry { get; set; }
        public bool StartWithWindows { get; set; }
        public bool MinimizeToTray { get; set; } = true;
        public bool Notifications { get; set; } = true;
        public string Language { get; set; } = "pt-BR";
        public int QuarantineDays { get; set; } = 7;
        public int BackupRetentionDays { get; set; } = 30;
        public bool BetaChannel { get; set; }
        public bool DryRun { get; set; }
        public string BackupDestination { get; set; } = string.Empty;
        public string[] BackupSources { get; set; } = Array.Empty<string>();
        public bool BackupSkipCloudOnly { get; set; } = true;
    }

    private readonly Dto _data = new();
    private bool _loading;

    // ---------------- Primeira execução ----------------
    public bool FirstRunCompleted
    {
        get => _data.FirstRunCompleted;
        set => Apply(v => _data.FirstRunCompleted = v, _data.FirstRunCompleted, value);
    }

    // ---------------- Proteções (passo 3 do onboarding) ----------------
    public bool CreateRestorePoint
    {
        get => _data.CreateRestorePoint;
        set => Apply(v => _data.CreateRestorePoint = v, _data.CreateRestorePoint, value);
    }

    public bool UseQuarantine
    {
        get => _data.UseQuarantine;
        set => Apply(v => _data.UseQuarantine = v, _data.UseQuarantine, value);
    }

    public bool ConfirmEachChange
    {
        get => _data.ConfirmEachChange;
        set => Apply(v => _data.ConfirmEachChange = v, _data.ConfirmEachChange, value);
    }

    public bool WhqlDriversOnly
    {
        get => _data.WhqlDriversOnly;
        set => Apply(v => _data.WhqlDriversOnly = v, _data.WhqlDriversOnly, value);
    }

    // ---------------- Telemetria (opt-in, desligada por padrão) ----------------
    public bool Telemetry
    {
        get => _data.Telemetry;
        set => Apply(v => _data.Telemetry = v, _data.Telemetry, value);
    }

    // ---------------- Geral ----------------
    public bool StartWithWindows
    {
        get => _data.StartWithWindows;
        set => Apply(v => _data.StartWithWindows = v, _data.StartWithWindows, value);
    }

    public bool MinimizeToTray
    {
        get => _data.MinimizeToTray;
        set => Apply(v => _data.MinimizeToTray = v, _data.MinimizeToTray, value);
    }

    public bool Notifications
    {
        get => _data.Notifications;
        set => Apply(v => _data.Notifications = v, _data.Notifications, value);
    }

    public string Language
    {
        get => _data.Language;
        set => Apply(v => _data.Language = v, _data.Language, value ?? "pt-BR");
    }

    // ---------------- Segurança e retenção ----------------
    public int QuarantineDays
    {
        get => _data.QuarantineDays;
        set => Apply(v => _data.QuarantineDays = v, _data.QuarantineDays, Math.Clamp(value, 1, 90));
    }

    public int BackupRetentionDays
    {
        get => _data.BackupRetentionDays;
        set => Apply(v => _data.BackupRetentionDays = v, _data.BackupRetentionDays, Math.Clamp(value, 1, 365));
    }

    // ---------------- Avançado ----------------
    public bool BetaChannel
    {
        get => _data.BetaChannel;
        set => Apply(v => _data.BetaChannel = v, _data.BetaChannel, value);
    }

    /// <summary>Modo simular: tudo é calculado e registado, nada é alterado no PC.</summary>
    public bool DryRun
    {
        get => _data.DryRun;
        set => Apply(v => _data.DryRun = v, _data.DryRun, value);
    }

    // ---------------- Backup (usado pela tarefa agendada) ----------------

    /// <summary>Último destino escolhido. Sem isto a tarefa «--backup» não sabe para onde copiar.</summary>
    public string BackupDestination
    {
        get => _data.BackupDestination;
        set => Apply(v => _data.BackupDestination = v, _data.BackupDestination, value ?? string.Empty);
    }

    public string[] BackupSources
    {
        get => _data.BackupSources;
        set => Apply(v => _data.BackupSources = v, _data.BackupSources, value ?? Array.Empty<string>());
    }

    public bool BackupSkipCloudOnly
    {
        get => _data.BackupSkipCloudOnly;
        set => Apply(v => _data.BackupSkipCloudOnly = v, _data.BackupSkipCloudOnly, value);
    }

    private void Apply<T>(Action<T> assign, T current, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;

        assign(value);
        Raise(name);

        if (!_loading) Save();
    }

    private static Settings Load()
    {
        var settings = new Settings { _loading = true };

        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var stored = JsonSerializer.Deserialize<Dto>(File.ReadAllText(Paths.SettingsFile), JsonOptions);

                if (stored is not null)
                {
                    settings._data.FirstRunCompleted = stored.FirstRunCompleted;
                    settings._data.CreateRestorePoint = stored.CreateRestorePoint;
                    settings._data.UseQuarantine = stored.UseQuarantine;
                    settings._data.ConfirmEachChange = stored.ConfirmEachChange;
                    settings._data.WhqlDriversOnly = stored.WhqlDriversOnly;
                    settings._data.Telemetry = stored.Telemetry;
                    settings._data.StartWithWindows = stored.StartWithWindows;
                    settings._data.MinimizeToTray = stored.MinimizeToTray;
                    settings._data.Notifications = stored.Notifications;
                    settings._data.Language = string.IsNullOrWhiteSpace(stored.Language) ? "pt-BR" : stored.Language;
                    settings._data.QuarantineDays = Math.Clamp(stored.QuarantineDays, 1, 90);
                    settings._data.BackupRetentionDays = Math.Clamp(stored.BackupRetentionDays, 1, 365);
                    settings._data.BetaChannel = stored.BetaChannel;
                    settings._data.DryRun = stored.DryRun;
                    settings._data.BackupDestination = stored.BackupDestination ?? string.Empty;
                    settings._data.BackupSources = stored.BackupSources ?? Array.Empty<string>();
                    settings._data.BackupSkipCloudOnly = stored.BackupSkipCloudOnly;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Configurações", $"Não foi possível ler as configurações: {ex.Message}");
        }

        settings._loading = false;
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.UserRoot);

            // Grava num temporário e troca: uma falha a meio não deixa o arquivo corrompido.
            var temp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_data, JsonOptions));
            File.Move(temp, Paths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Warn("Configurações", $"Não foi possível gravar as configurações: {ex.Message}");
        }
    }
}
