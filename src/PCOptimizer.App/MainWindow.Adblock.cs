using System.Windows;
using PCOptimizer.Core.Network;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

/// <summary>Bloqueio de anúncios pelo ficheiro hosts (aba Rede).</summary>
public partial class MainWindow
{
    void RefreshAdblock()
    {
        var n = HostsBlocker.BlockedCount();
        var old = n > HostsBlocker.TooMany;
        AdblockButton.Content = old ? Loc.T("Atualizar lista") : n > 0 ? Loc.T("Desligar") : Loc.T("Ligar");
        AdblockButton.Style = (Style)FindResource(n > 0 && !old ? typeof(System.Windows.Controls.Button) : "PrimaryButton");
        AdblockStatus.Text = old
            ? Loc.T($"⚠ A lista antiga ({n:N0} domínios) é pesada demais e deixa as apps lentas a ligar-se à internet. Atualiza para a lista leve (~10 mil).")
            : n > 0
            ? Loc.T($"✓ Ligado: {n:N0} domínios de anúncios e rastreadores bloqueados.")
            : Loc.T("Bloqueia anúncios e rastreadores em todo o PC (listas AdAway + Peter Lowe).");
    }

    async void Adblock_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdmin()) return;
        // com a lista antiga, o botão troca-a pela leve em vez de desligar
        var n = HostsBlocker.BlockedCount();
        var on = n > 0 && n <= HostsBlocker.TooMany;
        AdblockButton.IsEnabled = false;
        AdblockStatus.Text = on ? Loc.T("A desligar…") : Loc.T("A descarregar a lista e a bloquear…");
        try
        {
            if (on) await HostsBlocker.DisableAsync();
            else await HostsBlocker.EnableAsync();
            RefreshAdblock();
            if (!on && HostsBlocker.LastWarning is { } warning) AdblockStatus.Text += "\n⚠ " + warning;
        }
        catch (Exception ex) { AdblockStatus.Text = Ui.Error(ex, Loc.T("Não deu: ")); }
        finally { AdblockButton.IsEnabled = true; }
    }
}
