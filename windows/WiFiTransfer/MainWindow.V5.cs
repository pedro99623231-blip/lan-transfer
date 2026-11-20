using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WiFiTransfer;

/// <summary>Recursos da versão 5: texto, arrastar para o aparelho, pausar/cancelar, histórico, sincronização, tema.</summary>
public partial class MainWindow
{
    private readonly ObservableCollection<SyncView> _syncViews = new();
    private readonly ObservableCollection<HistoryView> _historyViews = new();
    private readonly HashSet<SyncJob> _syncing = new();
    private bool _loadingSettings;
    private PeerView? _textPeer;

    private void Snack(string title, string text, ControlAppearance look, SymbolRegular icon, double seconds = 3) =>
        _snack.Show(Translator.T(title), Translator.T(text), look, new SymbolIcon(icon), TimeSpan.FromSeconds(seconds));

    // ------------------------------------------------------------ tema
    private void ApplyTheme()
    {
        try { SystemThemeWatcher.UnWatch(this); } catch { }
        switch (_settings.Theme)
        {
            case "light": ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica); break;
            case "dark": ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica); break;
            default:
                SystemThemeWatcher.Watch(this);
                ApplicationThemeManager.ApplySystemTheme();
                break;
        }
    }

    private void OnThemeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingSettings || (ThemeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag is not string t) return;
        _settings.Theme = t; _settings.Save();
        ApplyTheme();
    }

    private void OnLanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingSettings || (LanguageBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag is not string l) return;
        _settings.Language = l; _settings.Save();
        LanguageHint.Text = l == "en" ? "Applies after reopening the app." : "Vale depois de abrir o app de novo.";
    }

    private void OnAutoAcceptSwitch(object sender, RoutedEventArgs e) { _settings.AutoAcceptTrusted = AutoAcceptSwitch.IsChecked == true; _settings.Save(); }

    private void OnEncryptSwitch(object sender, RoutedEventArgs e)
    {
        _settings.Encrypt = EncryptSwitch.IsChecked == true; _settings.Save();
        PeerNetwork.Encrypt = _settings.Encrypt;
    }

    // ------------------------------------------------------------ menu "Enviar" do aparelho
    private void OnSendMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement btn || btn.Tag is not PeerView v) return;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = btn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string text, SymbolRegular icon, Action a)
        {
            var mi = new System.Windows.Controls.MenuItem { Header = Translator.T(text), Icon = new SymbolIcon(icon) };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Add("Enviar arquivos…", SymbolRegular.Document24, () => SendFilesDialog(v));
        Add("Enviar pasta…", SymbolRegular.Folder24, () => SendFolderDialog(v));
        Add("Enviar texto…", SymbolRegular.ClipboardPaste24, () => ShowTextDialog(v));
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("Sincronizar uma pasta com este aparelho…", SymbolRegular.ArrowSync24, () => AddSyncJob(v));
        menu.IsOpen = true;
    }

    private void OnDropOnPeer(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PeerView v || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        _ = SendPaths(v.Peer, paths);
    }

    // ------------------------------------------------------------ texto / área de transferência
    private void ShowTextDialog(PeerView v)
    {
        _textPeer = v;
        TextTitle.Text = Translator.T($"Enviar texto para {v.Name}");
        string clip = "";
        try { if (System.Windows.Clipboard.ContainsText()) clip = System.Windows.Clipboard.GetText(); } catch { }
        TextBoxSend.Text = clip;
        TextOverlay.Visibility = Visibility.Visible;
        TextBoxSend.Focus();
        TextBoxSend.SelectAll();
    }

    private void OnHideText(object sender, RoutedEventArgs e) => TextOverlay.Visibility = Visibility.Collapsed;

    private async void OnSendText(object sender, RoutedEventArgs e)
    {
        var v = _textPeer; string text = TextBoxSend.Text;
        if (v == null || string.IsNullOrEmpty(text)) return;
        TextOverlay.Visibility = Visibility.Collapsed;
        try
        {
            await PeerNetwork.SendTextAsync(v.Peer, text);
            History.Add(new HistoryEntry { Kind = "text-out", Name = Preview(text), Peer = v.Name });
            Snack("Enviar", $"Texto enviado para {v.Name}", ControlAppearance.Success, SymbolRegular.Checkmark24);
        }
        catch (Exception ex) { Snack("Falhou:", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24, 4); }
    }

    private static string Preview(string text)
    {
        var t = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length > 80 ? t[..80] + "…" : t;
    }

    private void OnTextReceived(string from, string text, bool trusted)
    {
        bool copy = trusted || _settings.AutoAcceptTrusted && trusted;
        if (copy) { try { System.Windows.Clipboard.SetText(text); } catch { copy = false; } }
        var item = new DownloadItem(Preview(text), 0, false, copy ? $"Texto de {from}  ·  copiado" : $"Texto de {from}  ·  clique para copiar")
            { IsText = true, Text = text, From = from };
        _downloads.Insert(0, item);
        History.Add(new HistoryEntry { Kind = "text-in", Name = Preview(text), Peer = from, Path = null, Size = 0 });
        _lastTexts[item.Name] = text;
        Notify("Texto recebido", copy ? $"{from}: {Preview(text)}  ·  copiado" : $"{from}: {Preview(text)}");
    }

    private readonly Dictionary<string, string> _lastTexts = new();

    // ------------------------------------------------------------ ver sem baixar
    private void OnPreview(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RemoteFileView f) return;
        var peer = _peers.FirstOrDefault(p => p.Files.Contains(f));
        if (peer == null) return;
        try { Process.Start(new ProcessStartInfo(PeerNetwork.PreviewUrl(peer.Peer, f.Id)) { UseShellExecute = true }); } catch { }
    }

    // ------------------------------------------------------------ pausar / continuar / cancelar
    private void OnPauseTransfer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DownloadItem d) return;
        d.PauseRequested = true;
        d.CanPause = false;
        if (d.Cts == null || d.Cts.IsCancellationRequested || !d.Busy) { d.Busy = false; d.CanResume = true; d.Status = Translator.T("Pausado em 0%"); }
        d.Cts?.Cancel();
        if (!_downloading || _queue.Any(q => q.item == d)) { d.Busy = false; d.CanResume = true; d.Status = Translator.T($"Pausado em {d.Progress:0}%  ·  toque em ▶ para continuar"); }
    }

    private void OnResumeTransfer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadItem { ResumeAction: { } a } d) { d.CanResume = false; a(); }
    }

    private void OnCancelTransfer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DownloadItem d) return;
        d.CancelRequested = true;
        d.CanPause = false; d.CanResume = false; d.CanCancel = false;
        d.Cts?.Cancel();
        if (!d.Busy || _queue.Any(q => q.item == d)) { d.Busy = false; d.Status = Translator.T("Cancelado"); }
    }

    // ------------------------------------------------------------ histórico
    private void OnShowHistory(object sender, RoutedEventArgs e)
    {
        _historyViews.Clear();
        foreach (var h in History.Items) _historyViews.Add(new HistoryView { E = h });
        HistoryEmpty.Visibility = _historyViews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryOverlay.Visibility = Visibility.Visible;
    }

    private void OnHideHistory(object sender, RoutedEventArgs e) => HistoryOverlay.Visibility = Visibility.Collapsed;

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        History.Clear();
        _historyViews.Clear();
        HistoryEmpty.Visibility = Visibility.Visible;
    }

    private void OnOpenHistory(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HistoryView h) return;
        if (h.E.Path is { } p && File.Exists(p)) { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); return; }
        if (h.E.Kind.StartsWith("text")) { try { System.Windows.Clipboard.SetText(_lastTexts.GetValueOrDefault(h.E.Name, h.E.Name)); } catch { } }
    }

    private void OnShowHistory2(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryView { E.Path: { } p } && File.Exists(p))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
    }

    // ------------------------------------------------------------ pastas sincronizadas
    private void AddSyncJob(PeerView v)
    {
        var dlg = new OpenFolderDialog { Title = Translator.T($"Escolha a pasta para manter sincronizada com {v.Name}") };
        if (dlg.ShowDialog(this) != true) return;
        string folder = dlg.FolderName.TrimEnd('\\');
        if (_settings.SyncJobs.Any(j => j.PeerId == v.Peer.Id && string.Equals(j.Folder, folder, StringComparison.OrdinalIgnoreCase))) return;
        var job = new SyncJob { Folder = folder, PeerId = v.Peer.Id, PeerName = v.Name };
        _settings.SyncJobs.Add(job);
        _settings.Save();
        Snack("Pasta sincronizada", $"Arquivos novos de \"{Path.GetFileName(folder)}\" vão sozinhos para {v.Name} quando ele estiver na rede.",
            ControlAppearance.Primary, SymbolRegular.ArrowSync24, 5);
        _ = RunSync(job);
    }

    private void RefreshSyncViews()
    {
        _syncViews.Clear();
        foreach (var j in _settings.SyncJobs) _syncViews.Add(new SyncView { Job = j });
        SyncEmpty.Visibility = _syncViews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRemoveSync(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SyncView v) return;
        _settings.SyncJobs.Remove(v.Job);
        _settings.Save();
        RefreshSyncViews();
    }

    private async Task SyncLoop()
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        while (!_closing)
        {
            foreach (var job in _settings.SyncJobs.ToList()) await RunSync(job);
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Envia o que é novo ou mudou desde a última vez (se o aparelho estiver na rede).</summary>
    private async Task RunSync(SyncJob job)
    {
        if (_syncing.Contains(job) || !Directory.Exists(job.Folder)) return;
        var peer = _peers.FirstOrDefault(p => p.Peer.Id == job.PeerId);
        if (peer == null || peer.Blocked || peer.NeedsPin) return;
        _syncing.Add(job);
        try
        {
            string parent = Path.GetDirectoryName(job.Folder) ?? job.Folder;
            var pending = await Task.Run(() => Directory.EnumerateFiles(job.Folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Select(f => new FileInfo(f))
                .Where(fi => !fi.Name.StartsWith('.') && !fi.Name.EndsWith(".part"))
                .Select(fi => (fi, rel: Path.GetRelativePath(parent, fi.FullName), stamp: $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}"))
                .Where(x => job.Sent.GetValueOrDefault(x.rel) != x.stamp)
                .Take(500).ToList());
            if (pending.Count == 0) { job.LastSync = DateTime.Now; return; }
            var files = pending.Select(x => new PeerNetwork.OutFile(x.fi.FullName, Path.GetRelativePath(parent, x.fi.DirectoryName!), x.fi.Length)).ToList();
            int ok = await SendTo(peer.Peer, files, sync: true);
            if (ok == files.Count)
            {
                foreach (var x in pending) job.Sent[x.rel] = x.stamp;
                job.LastSync = DateTime.Now;
                _settings.Save();
                Notify("Pasta sincronizada", $"{ok} arquivo(s) de \"{Path.GetFileName(job.Folder)}\" enviados para {peer.Name}");
            }
        }
        catch { }
        finally { _syncing.Remove(job); }
    }
}
