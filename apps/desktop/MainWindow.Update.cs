using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace PCBoost;

// Atualizações: ao abrir e a cada 6 h o app consulta a última release; havendo versão nova,
// mostra "ATUALIZAR · x.y.z" no topo. O clique baixa o instalador, fecha o app e o instalador reabre a versão nova.
public partial class MainWindow
{
    private UpdateInfo? update;
    private bool updating, updateAnnounced;
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };

    private void InitUpdates()
    {
        if (uiTestMode) return;
        updateTimer.Tick += async (_, _) => await CheckUpdates();
        Loaded += async (_, _) => { await Task.Delay(TimeSpan.FromSeconds(4)); await CheckUpdates(); updateTimer.Start(); };
    }

    private async Task CheckUpdates()
    {
        if (updating || closed) return;
        try { update = await Updater.Check(); }
        catch (Exception) { return; } // sem internet ou GitHub fora: tenta de novo no próximo ciclo
        if (update is null) { UpdateButton.Visibility = Visibility.Collapsed; return; }
        UpdateLabel.Text = $"ATUALIZAR · {update.Version}";
        UpdateButton.ToolTip = Updater.IsInstalled
            ? $"Versão {update.Version} disponível (você tem {Updater.Current}). Clique para baixar e reiniciar."
            : $"Versão {update.Version} disponível (você tem {Updater.Current}). Clique para abrir a página de download.";
        UpdateButton.Visibility = Visibility.Visible;
        if (!updateAnnounced) { updateAnnounced = true; Status($"Nova versão {update.Version} disponível. Clique em ATUALIZAR no topo."); }
    }

    private async void UpdateClicked(object sender, RoutedEventArgs e)
    {
        if (update is null || updating) return;
        if (!Updater.IsInstalled)
        {
            Process.Start(new ProcessStartInfo(update.PageUrl) { UseShellExecute = true })?.Dispose();
            return;
        }
        if (BusyWithSession) { Status("Termine a sessão iniciada pelo app (Desenvolvimento/Jogos) antes de atualizar."); return; }
        updating = true; UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => UpdateLabel.Text = $"BAIXANDO {p:P0}");
            string setup = await Updater.Download(update, progress);
            UpdateLabel.Text = "REINICIANDO…";
            Updater.Launch(setup);
            Close();
        }
        catch (Exception ex)
        {
            Status("Não foi possível atualizar: " + ex.Message);
            UpdateLabel.Text = $"ATUALIZAR · {update.Version}"; UpdateButton.IsEnabled = true; updating = false;
        }
    }
}
