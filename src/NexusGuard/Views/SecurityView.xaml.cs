using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

/// <summary>Linha de estado (proteção ligada/desligada) mostrada no cartão do Defender.</summary>
public sealed class StateRow
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public bool? Good { get; init; }

    public Brush Brush => Good switch
    {
        true => new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)),
        false => new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
        _ => new SolidColorBrush(Color.FromRgb(0x4A, 0x53, 0x66))
    };
}

public partial class SecurityView : UserControl
{
    private readonly ObservableCollection<StateRow> _states = new();
    private ConsoleSink? _sink;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _loadedOnce;

    public SecurityView()
    {
        InitializeComponent();
        StateList.ItemsSource = _states;

        Loaded += async (_, _) =>
        {
            if (_sink is null)
            {
                _sink = new ConsoleSink(Output);
                _sink.Write("A saída das análises, do sfc e do DISM aparece aqui, linha a linha.");
            }

            if (_loadedOnce) return;
            _loadedOnce = true;
            await ReloadStatusAsync();
        };
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        OpStatus.Text = status;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        foreach (var b in new[]
                 {
                     UpdateSigButton, QuickScanButton, FullScanButton, CustomScanButton,
                     RemoveThreatsButton, SfcButton, DismButton, ReloadStatusButton
                 })
            b.IsEnabled = !busy;
    }

    // ---------------- Estado ----------------

    private async void OnReloadStatus(object sender, RoutedEventArgs e) => await ReloadStatusAsync();

    private async Task ReloadStatusAsync()
    {
        ReloadStatusButton.IsEnabled = false;
        ShieldTitle.Text = "Verificando…";
        ShieldSubtitle.Text = "";

        try
        {
            var status = await SecurityManager.GetStatusAsync();
            var firewall = await SecurityManager.GetFirewallAsync();
            var threats = await SecurityManager.GetThreatsAsync();

            _states.Clear();

            if (!status.Available)
            {
                Paint(false, "Estado desconhecido",
                    status.Error ?? "Não foi possível consultar o Microsoft Defender neste sistema.");
                _states.Add(new StateRow
                {
                    Title = "Microsoft Defender",
                    Detail = "indisponível",
                    Good = null
                });
            }
            else
            {
                var protectedOk = status.AntivirusEnabled && status.RealTimeProtectionEnabled;

                Paint(protectedOk,
                    protectedOk ? "O computador está protegido" : "A proteção não está ativa",
                    protectedOk
                        ? $"{status.ModeText} · definições {status.SignatureAgeText}"
                        : "Ligue a proteção em tempo real na Segurança do Windows.");

                _states.Add(new StateRow
                {
                    Title = "Antivírus",
                    Detail = status.AntivirusEnabled ? "ativo" : "desligado",
                    Good = status.AntivirusEnabled
                });
                _states.Add(new StateRow
                {
                    Title = "Proteção em tempo real",
                    Detail = status.RealTimeProtectionEnabled ? "ativa" : "desligada",
                    Good = status.RealTimeProtectionEnabled
                });
                _states.Add(new StateRow
                {
                    Title = "Monitorização de comportamento",
                    Detail = status.BehaviorMonitorEnabled ? "ativa" : "desligada",
                    Good = status.BehaviorMonitorEnabled
                });
                _states.Add(new StateRow
                {
                    Title = "Proteção contra adulteração",
                    Detail = status.TamperProtected ? "ativa" : "desligada",
                    Good = status.TamperProtected
                });
                _states.Add(new StateRow
                {
                    Title = "Configurações de vírus",
                    Detail = $"{status.SignatureText} ({status.SignatureAgeText})",
                    Good = status.AntivirusSignatureAge <= 7
                });
                _states.Add(new StateRow
                {
                    Title = "Última análise rápida",
                    Detail = status.QuickScanText,
                    Good = status.QuickScanAge <= 14
                });
                _states.Add(new StateRow
                {
                    Title = "Última análise completa",
                    Detail = status.FullScanText,
                    Good = status.FullScanAge <= 60
                });
            }

            foreach (var profile in firewall)
            {
                _states.Add(new StateRow
                {
                    Title = $"Firewall — rede {profile.NameText.ToLower(Fmt.Pt)}",
                    Detail = profile.StateText.ToLower(Fmt.Pt),
                    Good = profile.Enabled
                });
            }

            ThreatList.ItemsSource = threats;
            ThreatsEmpty.Visibility = threats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ThreatsEmpty.Text = threats.Count == 0
                ? "Sem deteções registadas."
                : $"{threats.Count} deteções registadas pelo Defender.";
        }
        catch (Exception ex)
        {
            Logger.Error("Segurança", "Falha ao ler o estado de segurança", ex);
            Paint(false, "Erro na verificação", "Consulte o registro para o detalhe.");
        }
        finally
        {
            ReloadStatusButton.IsEnabled = !_busy;
        }
    }

    private void Paint(bool? good, string title, string subtitle)
    {
        ShieldTitle.Text = title;
        ShieldSubtitle.Text = subtitle;

        var (badge, stroke) = good switch
        {
            true => ("B.OkSoft", "B.Ok"),
            false => ("B.DangerSoft", "B.Danger"),
            _ => ("B.WarnSoft", "B.Warn")
        };

        ShieldBadge.Background = (Brush)FindResource(badge);
        ShieldIcon.Stroke = (Brush)FindResource(stroke);
    }

    // ---------------- Operações ----------------

    private async Task RunAsync(string label, Func<Action<string>, CancellationToken, Task> work)
    {
        if (_busy) return;

        SetBusy(true, label);
        _cts = new CancellationTokenSource();

        try
        {
            await work(line => _sink?.Write(line), _cts.Token);
            SetBusy(false, $"{label} — concluído.");
        }
        catch (OperationCanceledException)
        {
            _sink?.Write("=== Operação interrompida pelo usuário ===");
            SetBusy(false, "Operação interrompida.");
        }
        catch (Exception ex)
        {
            Logger.Error("Segurança", $"{label} falhou", ex);
            _sink?.Write($"ERRO: {ex.Message}");
            SetBusy(false, $"{label} — falhou.");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            await ReloadStatusAsync();
        }
    }

    private async void OnUpdateSignatures(object sender, RoutedEventArgs e) =>
        await RunAsync("Atualizando definições de vírus",
            (log, ct) => SecurityManager.UpdateSignaturesAsync(log, ct));

    private async void OnQuickScan(object sender, RoutedEventArgs e) =>
        await RunAsync("Análise rápida", async (log, ct) =>
        {
            var (ok, found) = await SecurityManager.ScanAsync(ScanKind.Quick, null, log, ct);
            Announce(ok, found);
        });

    private async void OnFullScan(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm(this, "Análise completa",
                "A análise completa percorre todos os arquivos do computador.\n\n" +
                "Pode demorar mais de uma hora e torna o PC mais lento durante esse tempo. Começar?"))
            return;

        await RunAsync("Análise completa", async (log, ct) =>
        {
            var (ok, found) = await SecurityManager.ScanAsync(ScanKind.Full, null, log, ct);
            Announce(ok, found);
        });
    }

    private async void OnCustomScan(object sender, RoutedEventArgs e)
    {
        var folder = Ui.PickFolder("Escolha a pasta a analisar");
        if (folder is null) return;

        await RunAsync($"Análise de {folder}", async (log, ct) =>
        {
            var (ok, found) = await SecurityManager.ScanAsync(ScanKind.Custom, folder, log, ct);
            Announce(ok, found);
        });
    }

    private void Announce(bool ok, int found)
    {
        if (!ok)
        {
            Ui.Warn(this, "Análise", "A análise não terminou correctamente. Consulte a saída à direita.");
            return;
        }

        Ui.Inform(this, "Análise concluída", found > 0
            ? "Foram encontradas ameaças. O Defender colocou-as em quarentena ou removeu-as.\n\n" +
              "Use «Remover ameaças» se ainda ficarem itens pendentes."
            : "Nenhuma ameaça encontrada.");
    }

    private async void OnRemoveThreats(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm(this, "Remover ameaças",
                "Pede ao Microsoft Defender para remover tudo o que tem detectado.\n\nContinuar?"))
            return;

        await RunAsync("Removendo ameaças",
            (log, ct) => SecurityManager.RemoveThreatsAsync(log, ct));
    }

    private async void OnSfc(object sender, RoutedEventArgs e)
    {
        if (!Fmt.IsAdmin)
        {
            Ui.Warn(this, "Privilégios necessários",
                "O sfc /scannow exige administrador. Use «Reiniciar como administrador» na barra lateral.");
            return;
        }

        if (!Ui.Confirm(this, "Verificar arquivos do sistema",
                "O sfc /scannow pode demorar 10 a 20 minutos. Continuar?"))
            return;

        await RunAsync("sfc /scannow", (log, ct) => SecurityManager.RunSfcAsync(log, ct));
    }

    private async void OnDism(object sender, RoutedEventArgs e)
    {
        if (!Fmt.IsAdmin)
        {
            Ui.Warn(this, "Privilégios necessários",
                "O DISM exige administrador. Use «Reiniciar como administrador» na barra lateral.");
            return;
        }

        if (!Ui.Confirm(this, "Reparar imagem do Windows",
                "O DISM vai transferir arquivos do Windows Update para reparar o sistema.\n\n" +
                "Pode demorar bastante e precisa de conexão com a internet. Continuar?"))
            return;

        await RunAsync("DISM /RestoreHealth", (log, ct) => SecurityManager.RunDismRestoreAsync(log, ct));
    }

    private void OnOpenDefender(object sender, RoutedEventArgs e) =>
        Shell.OpenExternal("windowsdefender://threat");

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        OpStatus.Text = "interrompendo…";
    }

    private void OnClearConsole(object sender, RoutedEventArgs e) => _sink?.Clear();
}
