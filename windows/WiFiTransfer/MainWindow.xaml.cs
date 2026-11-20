using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using QRCoder;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WiFiTransfer;

public partial class MainWindow : FluentWindow
{
    public const string PipeName = "LANTransfer.Open";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly Settings _settings = Settings.Load();
    private readonly ObservableCollection<SharedFile> _shared = new();
    private readonly ObservableCollection<PeerView> _peers = new();
    private readonly ObservableCollection<DownloadItem> _downloads = new();
    private readonly ObservableCollection<KnownDevice> _known = new();
    private readonly Dictionary<string, DateTime> _peerSeen = new();
    private readonly Dictionary<string, DownloadItem> _incoming = new();
    private readonly SnackbarService _snack = new();
    private readonly SemaphoreSlim _offerGate = new(1, 1);
    private TaskCompletionSource<bool>? _offerTcs;
    private Offer? _currentOffer;
    private PeerView? _pinPeer;
    private TransferServer? _server;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _closing, _exiting, _trayHintShown;
    private bool _downloading;
    private readonly bool _startHidden;
    private readonly Queue<(Peer peer, RemoteFileView file, DownloadItem item)> _queue = new();

    public MainWindow()
    {
        InitializeComponent();
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        _startHidden = args.Contains("--minimized");
        if (_startHidden) { WindowState = WindowState.Minimized; ShowInTaskbar = false; }

        SharedList.ItemsSource = _shared;
        PeersList.ItemsSource = _peers;
        DownloadsList.ItemsSource = _downloads;
        KnownList.ItemsSource = _known;
        _shared.CollectionChanged += (_, _) => UpdateEmptyStates();
        _peers.CollectionChanged += (_, _) => UpdateEmptyStates();
        _downloads.CollectionChanged += (_, _) => UpdateEmptyStates();
        _snack.SetSnackbarPresenter(Snackbar);
        SharingSwitch.IsChecked = _settings.Sharing;
        PeerNetwork.MyId = _settings.DeviceId;
        PeerNetwork.Encrypt = _settings.Encrypt;
        SyncList.ItemsSource = _syncViews;
        HistoryList.ItemsSource = _historyViews;
        PeerNetwork.PinFor = id => { lock (_settings) return _settings.PeerPins.GetValueOrDefault(id); };
        SetupTray();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTheme();
        if (_startHidden) Hide();

        foreach (var p in _settings.SharedPaths.Where(File.Exists)) AddShared(new SharedFile(p));
        foreach (var d in _settings.SharedFolders.Where(Directory.Exists).ToList()) AddFolder(d, save: false, notify: false);
        AddPaths(Environment.GetCommandLineArgs().Skip(1).Where(a => !a.StartsWith("--")));
        UpdateEmptyStates();
        UpdatePinBadge();
        SyncStartupRegistry();

        _server = new TransferServer(_settings, () => Dispatcher.Invoke(() => _shared.ToList()), AskOffer);
        _server.Serving += (item, bytes) => Dispatcher.InvokeAsync(() =>
        {
            item.Busy = true;
            item.Progress = item.Size > 0 ? bytes * 100.0 / item.Size : 0;
            item.Status = $"Sendo baixado… {item.Progress:0}%";
        });
        _server.Served += (item, ok, who) => Dispatcher.InvokeAsync(() =>
        {
            item.Busy = false;
            item.Status = ok ? $"{item.SizeText}  ·  ✓ baixado por {who}" : item.SizeText;
            if (ok && _settings.NotifyDownloads) Notify("Arquivo baixado", $"{who} baixou {item.Name}");
        });
        _server.DeviceSeen += (id, name) => Dispatcher.InvokeAsync(() => RememberDevice(id, name));
        _server.TextReceived += (from, text, trusted) => Dispatcher.InvokeAsync(() => OnTextReceived(from, text, trusted));
        _server.IncomingStart += (id, name, size, from) => Dispatcher.InvokeAsync(() =>
        {
            var item = new DownloadItem(name, size, true, $"Recebendo de {from}…") { From = from };
            _incoming[id] = item;
            _downloads.Insert(0, item);
        });
        _server.IncomingProgress += (id, got) => Dispatcher.InvokeAsync(() =>
        {
            if (!_incoming.TryGetValue(id, out var item)) return;
            item.Report(got, "Recebendo");
        });
        _server.IncomingDone += (id, path, error) => Dispatcher.InvokeAsync(() =>
        {
            if (!_incoming.Remove(id, out var item)) return;
            item.Busy = false;
            if (path != null)
            {
                item.Path = path; item.Name = Path.GetFileName(path); item.Progress = 100;
                item.Status = $"{Format.Size(item.Size)}  ·  recebido de {item.From}";
                History.Add(new HistoryEntry { Kind = "in", Name = item.Name, Size = item.Size, Peer = item.From ?? "", Path = path });
                if (_incoming.Count == 0) Notify("Arquivos recebidos", "Salvos em " + _settings.ReceiveFolder);
            }
            else item.Status = "Falhou: " + error;
        });
        try { await _server.StartAsync(); }
        catch (Exception ex) { System.Windows.MessageBox.Show("Não foi possível iniciar o compartilhamento:\n" + ex.Message, "LAN Transfer"); }

        var ips = TransferServer.LocalIPs();
        MeText.Text = Translator.T($"Este PC: {Environment.MachineName}  ·  {ips[0]}");
        FolderText.Text = Translator.T("Recebidos e baixados vão para: " + _settings.ReceiveFolder);

        _ = PipeLoop();
        _ = SyncLoop();
        _ = DiscoveryLoop();
        _ = FilesLoop();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_settings.CloseToTray && !_exiting)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                Notify("O LAN Transfer continua aberto", "Seus arquivos seguem disponíveis. Clique no ícone perto do relógio para abrir ou sair.", force: true);
            }
            return;
        }
        _closing = true;
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        if (_server != null) await _server.DisposeAsync();
    }

    // ------------------------------------------------------------ bandeja / segunda instância
    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon { Text = "LAN Transfer", Visible = true };
        try
        {
            var s = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))?.Stream;
            if (s != null) _tray.Icon = new System.Drawing.Icon(s);
        }
        catch { }
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Translator.T("Abrir LAN Transfer"), null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add(Translator.T("Disponibilizar arquivos…"), null, (_, _) => Dispatcher.Invoke(() => { ShowFromTray(); OnAddFiles(this, new RoutedEventArgs()); }));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Translator.T("Sair"), null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, a) => { if (a.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(ShowFromTray); };
        _tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void Notify(string title, string text, bool force = false)
    {
        title = Translator.T(title); text = Translator.T(text);
        bool visible = IsVisible && WindowState != WindowState.Minimized && IsActive;
        if (visible && !force)
            _snack.Show(title, text, ControlAppearance.Primary, new SymbolIcon(SymbolRegular.Info24), TimeSpan.FromSeconds(4));
        else
            _tray?.ShowBalloonTip(4000, title, text, System.Windows.Forms.ToolTipIcon.Info);
    }

    /// <summary>Recebe caminhos de outra instância (menu do Explorer, "Enviar para").</summary>
    private async Task PipeLoop()
    {
        while (!_closing)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server);
                var text = await reader.ReadToEndAsync();
                var paths = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(a => !a.StartsWith("--")).ToList();
                ShowFromTray();
                AddPaths(paths);
            }
            catch { await Task.Delay(500); }
        }
    }

    // ------------------------------------------------------------ busca de aparelhos
    private async Task DiscoveryLoop()
    {
        int n = 0;
        while (!_closing)
        {
            await Discover(wide: n % 5 == 0);
            n++;
            await Task.Delay(6000);
        }
    }

    private async Task Discover(bool wide)
    {
        SearchRing.Visibility = Visibility.Visible;
        try
        {
            var found = await Task.Run(() => PeerNetwork.DiscoverAsync(_settings.DeviceId, wide));
            var now = DateTime.UtcNow;
            foreach (var p in found)
            {
                _peerSeen[p.Id] = now;
                RememberDevice(p.Id, p.Name);
                var view = _peers.FirstOrDefault(v => v.Peer.Id == p.Id);
                if (view == null)
                {
                    view = new PeerView(p);
                    _peers.Add(view);
                    await RefreshPeer(view);
                }
                else if (view.Peer != p) { view.Peer = p; view.Refresh(); }
            }
            foreach (var stale in _peers.Where(v => (now - _peerSeen.GetValueOrDefault(v.Peer.Id)).TotalSeconds > 40).ToList())
                _peers.Remove(stale);
        }
        catch { }
        finally { SearchRing.Visibility = Visibility.Collapsed; }
    }

    private async Task FilesLoop()
    {
        while (!_closing)
        {
            foreach (var v in _peers.ToList()) await RefreshPeer(v);
            await Task.Delay(3000);
        }
    }

    private async Task RefreshPeer(PeerView view)
    {
        List<RemoteFile> files;
        try
        {
            files = await PeerNetwork.FilesAsync(view.Peer);
            view.NeedsPin = false; view.Blocked = false;
        }
        catch (PeerNetwork.AccessException ax)
        {
            view.NeedsPin = ax.NeedPin; view.Blocked = !ax.NeedPin;
            if (view.Files.Count > 0) view.Files.Clear();
            view.Refresh();
            return;
        }
        catch { return; }
        var current = view.Files.Select(f => f.Id).ToList();
        if (current.SequenceEqual(files.Select(f => f.Id))) { view.Refresh(); return; }
        var selected = view.Files.Where(f => f.Selected).Select(f => f.Id).ToHashSet();
        var thumbs = view.Files.Where(f => f.Thumb != null).ToDictionary(f => f.Id, f => f.Thumb);
        view.Files.Clear();
        foreach (var f in files)
        {
            var rv = new RemoteFileView { Id = f.Id, Name = f.Name, RelDir = f.RelDir, Size = f.Size, CanThumb = f.Thumb, Selected = selected.Contains(f.Id) };
            rv.Visible = Matches(rv.Name, rv.RelDir);
            if (thumbs.TryGetValue(f.Id, out var t)) rv.Thumb = t;
            view.Files.Add(rv);
        }
        view.Refresh();
        UpdateSelectionUi();
        _ = LoadRemoteThumbs(view);
    }

    private async Task LoadRemoteThumbs(PeerView view)
    {
        foreach (var f in view.Files.Where(f => f.CanThumb && f.Thumb == null).Take(200).ToList())
        {
            var bytes = await PeerNetwork.ThumbAsync(view.Peer, f.Id);
            f.Thumb = Thumbs.ToImage(bytes);
        }
    }

    private async void OnSearch(object sender, RoutedEventArgs e) => await Discover(wide: true);

    private void RememberDevice(string id, string name)
    {
        if (string.IsNullOrEmpty(id) || id == _settings.DeviceId) return;
        lock (_settings)
        {
            if (_settings.KnownDevices.TryGetValue(id, out var old) && old == name) return;
            _settings.KnownDevices[id] = name;
        }
        _settings.Save();
    }

    // ------------------------------------------------------------ busca por nome
    private string _query = "";
    private bool Matches(string name, string rel) =>
        _query.Length == 0 || (rel + "/" + name).Contains(_query, StringComparison.CurrentCultureIgnoreCase);

    private void OnSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _query = SearchBox.Text.Trim();
        foreach (var f in _shared) f.Visible = Matches(f.Name, f.RelDir);
        foreach (var p in _peers)
        {
            foreach (var f in p.Files) f.Visible = Matches(f.Name, f.RelDir);
            p.AnyVisible = p.Files.Any(f => f.Visible);
        }
    }

    // ------------------------------------------------------------ baixar
    private void OnSelectionChanged(object sender, RoutedEventArgs e) => UpdateSelectionUi();

    private void OnSelectAllPeer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PeerView v) return;
        var shown = v.Files.Where(f => f.Visible).ToList();
        bool all = shown.All(f => f.Selected);
        foreach (var f in shown) f.Selected = !all;
        UpdateSelectionUi();
    }

    private void UpdateSelectionUi()
    {
        int n = _peers.Sum(p => p.Files.Count(f => f.Selected));
        DownloadSelectedButton.IsEnabled = n > 0;
        DownloadSelectedButton.Content = Translator.T(n > 0 ? $"Baixar {n} selecionado(s)" : "Baixar selecionados");
    }

    private void OnDownloadOne(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RemoteFileView f) return;
        var peer = _peers.FirstOrDefault(p => p.Files.Contains(f));
        if (peer != null) Enqueue(peer.Peer, f);
    }

    private void OnDownloadSelected(object sender, RoutedEventArgs e)
    {
        foreach (var p in _peers)
            foreach (var f in p.Files.Where(f => f.Selected))
            {
                f.Selected = false;
                Enqueue(p.Peer, f);
            }
        UpdateSelectionUi();
    }

    private void Enqueue(Peer peer, RemoteFileView file, DownloadItem? existing = null)
    {
        var item = existing ?? new DownloadItem(file.Name, file.Size, true, $"Na fila  ·  de {peer.Name}") { From = peer.Name };
        if (existing == null) _downloads.Insert(0, item);
        bool alreadyQueued = _queue.Any(q => q.item == item);
        item.Busy = true; item.PauseRequested = false; item.CancelRequested = false;
        item.CanResume = false; item.CanPause = true; item.CanCancel = true;
        item.Status = $"Na fila  ·  de {peer.Name}";
        item.ResumeAction = () => Enqueue(peer, file, item);
        if (alreadyQueued) return;
        _queue.Enqueue((peer, file, item));
        _ = RunQueue();
    }

    private async Task RunQueue()
    {
        if (_downloading) return;
        _downloading = true;
        int ok = 0;
        try
        {
            while (_queue.Count > 0)
            {
                var (peer, file, item) = _queue.Dequeue();
                if (item.CancelRequested || item.PauseRequested) continue;
                var rf = new RemoteFile(file.Id, file.Name, file.Size, file.RelDir);
                item.Cts = new System.Threading.CancellationTokenSource();
                item.ResetSpeed();
                try
                {
                    var path = await PeerNetwork.DownloadAsync(peer, rf, _settings.ReceiveFolder,
                        got => Dispatcher.InvokeAsync(() => item.Report(got, "Baixando")), item.Cts.Token);
                    item.Path = path;
                    item.Name = Path.GetFileName(path);
                    item.Progress = 100;
                    item.Status = $"{Format.Size(item.Size)}  ·  de {peer.Name}";
                    History.Add(new HistoryEntry { Kind = "down", Name = item.Name, Size = item.Size, Peer = peer.Name, Path = path });
                    ok++;
                    item.CanResume = false;
                }
                catch (OperationCanceledException) when (item.PauseRequested)
                {
                    item.Status = $"Pausado em {item.Progress:0}%  ·  toque em ▶ para continuar";
                    item.CanResume = true;
                }
                catch (OperationCanceledException) when (item.CancelRequested)
                {
                    try { File.Delete(PeerNetwork.PartialPath(peer, rf, _settings.ReceiveFolder)); } catch { }
                    item.Status = "Cancelado";
                }
                catch (Exception ex)
                {
                    item.Status = "Falhou: " + ex.Message + "  ·  toque em ▶ para continuar de onde parou";
                    item.CanResume = true;
                }
                finally { item.Busy = false; item.CanPause = false; item.CanCancel = item.CanResume; }
            }
            if (ok > 0) Notify("Download concluído", "Arquivos salvos em " + _settings.ReceiveFolder);
        }
        finally { _downloading = false; }
    }

    private void OnClearDownloads(object sender, RoutedEventArgs e)
    {
        foreach (var d in _downloads.Where(d => !d.Busy).ToList()) _downloads.Remove(d);
    }

    private void OnOpenDownload(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadItem { IsText: true, Text: { } txt })
        {
            try { System.Windows.Clipboard.SetText(txt); } catch { }
            _snack.Show(Translator.T("Copiado"), "O texto está na área de transferência.", ControlAppearance.Success, new SymbolIcon(SymbolRegular.Copy24), TimeSpan.FromSeconds(2));
            return;
        }
        if ((sender as FrameworkElement)?.Tag is DownloadItem { Path: { } p } && File.Exists(p))
            Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
    }

    private void OnShowDownload(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadItem { Path: { } p } && File.Exists(p))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.ReceiveFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.ReceiveFolder}\"") { UseShellExecute = true });
    }

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Onde salvar os arquivos recebidos e baixados", InitialDirectory = _settings.ReceiveFolder };
        if (dlg.ShowDialog(this) != true) return;
        _settings.ReceiveFolder = dlg.FolderName;
        _settings.Save();
        FolderText.Text = Translator.T("Recebidos e baixados vão para: " + _settings.ReceiveFolder);
    }

    // ------------------------------------------------------------ enviar direto para um aparelho
    private void SendFilesDialog(PeerView v)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = $"Escolha os arquivos para enviar a {v.Name}" };
        if (dlg.ShowDialog(this) != true) return;
        _ = SendPaths(v.Peer, dlg.FileNames);
    }

    private void SendFolderDialog(PeerView v)
    {
        var dlg = new OpenFolderDialog { Title = $"Escolha a pasta para enviar a {v.Name}" };
        if (dlg.ShowDialog(this) != true) return;
        _ = SendPaths(v.Peer, new[] { dlg.FolderName });
    }

    /// <summary>Arquivos e pastas (pastas vão com as subpastas).</summary>
    private Task SendPaths(Peer peer, IEnumerable<string> paths)
    {
        var files = new List<PeerNetwork.OutFile>();
        foreach (var p in paths)
        {
            if (File.Exists(p)) files.Add(new PeerNetwork.OutFile(p, "", new FileInfo(p).Length));
            else if (Directory.Exists(p))
            {
                string root = p.TrimEnd('\\'), parent = Path.GetDirectoryName(root) ?? root;
                foreach (var f in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    files.Add(new PeerNetwork.OutFile(f, Path.GetRelativePath(parent, Path.GetDirectoryName(f)!), new FileInfo(f).Length));
            }
        }
        if (files.Count == 0)
        {
            _snack.Show(Translator.T("Nada para enviar"), "Não há arquivos.", ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(3));
            return Task.CompletedTask;
        }
        return SendTo(peer, files);
    }

    /// <summary>Envia direto; devolve quantos foram enviados (-1 = recusado/sem resposta).</summary>
    private async Task<int> SendTo(Peer peer, List<PeerNetwork.OutFile> files, bool sync = false)
    {
        var waiting = new DownloadItem(Translator.T($"{files.Count} arquivo(s) para {peer.Name}"), files.Sum(f => f.Size), true,
            sync ? $"Sincronizando com {peer.Name}…" : $"Aguardando {peer.Name} aceitar…") { Outgoing = true, CanCancel = true };
        waiting.Cts = new System.Threading.CancellationTokenSource();
        _downloads.Insert(0, waiting);
        string? token;
        try { token = await PeerNetwork.OfferAsync(peer, files, waiting.Cts.Token, sync); }
        catch (Exception ex)
        {
            waiting.Busy = false; waiting.CanCancel = false;
            waiting.Status = waiting.CancelRequested ? "Cancelado" : "Sem resposta: " + ex.Message;
            return -1;
        }
        if (token == null) { waiting.Busy = false; waiting.CanCancel = false; waiting.Status = $"{peer.Name} recusou o envio"; return -1; }

        int ok = 0;
        if (PeerNetwork.ShouldBundle(files))
        {
            // muitos arquivos pequenos: vai tudo num pacote só
            var item = waiting;
            item.Name = Translator.T($"{files.Count} arquivo(s) em pacote para {peer.Name}");
            item.Cts = new System.Threading.CancellationTokenSource();
            item.ResetSpeed();
            try
            {
                await PeerNetwork.UploadBundleAsync(peer, token, files, sent => Dispatcher.InvokeAsync(() => item.Report(sent, "Enviando")),
                    item.Cts.Token, overwrite: sync);
                item.Progress = 100; ok = files.Count;
                item.Status = $"{Format.Size(item.Size)}  ·  enviado para {peer.Name}";
                History.Add(new HistoryEntry { Kind = "up", Name = item.Name, Size = item.Size, Peer = peer.Name });
            }
            catch (Exception ex) { item.Status = item.CancelRequested ? "Cancelado" : "Falhou: " + ex.Message; }
            finally { item.Busy = false; item.CanCancel = false; }
        }
        else
        {
            _downloads.Remove(waiting);
            var items = files.Select(f =>
            {
                var it = new DownloadItem(f.Name, f.Size, true, $"Na fila  ·  para {peer.Name}") { Outgoing = true, Path = f.Path, CanCancel = true };
                _downloads.Insert(0, it);
                return it;
            }).ToList();
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i]; var item = items[i];
                if (item.CancelRequested) { item.Busy = false; item.Status = "Cancelado"; continue; }
                item.Cts = new System.Threading.CancellationTokenSource();
                try
                {
                    await PeerNetwork.UploadAsync(peer, token, f, sent => Dispatcher.InvokeAsync(() => item.Report(sent, "Enviando")),
                        item.Cts.Token, overwrite: sync);
                    item.Progress = 100;
                    item.Status = $"{Format.Size(f.Size)}  ·  enviado para {peer.Name}";
                    History.Add(new HistoryEntry { Kind = "up", Name = f.Name, Size = f.Size, Peer = peer.Name, Path = f.Path });
                    ok++;
                }
                catch (Exception ex) { item.Status = item.CancelRequested ? "Cancelado" : "Falhou: " + ex.Message; }
                finally { item.Busy = false; item.CanCancel = false; }
            }
        }
        if (!sync) Notify("Envio concluído", $"{ok} de {files.Count} arquivo(s) enviados para {peer.Name}");
        return ok;
    }

    /// <summary>Chamado pelo servidor quando alguém quer enviar arquivos para este PC.</summary>
    private async Task<bool> AskOffer(Offer offer)
    {
        if (_settings.AutoAcceptTrusted && offer.DeviceId != null && _settings.Trusted.Contains(offer.DeviceId))
        {
            if (!offer.Sync) _ = Dispatcher.InvokeAsync(() => Notify("Recebendo arquivos", $"{offer.From} está enviando {offer.Files.Count} arquivo(s)"));
            return true;
        }
        await _offerGate.WaitAsync();
        try
        {
            return await await Dispatcher.InvokeAsync(async () =>
            {
                _currentOffer = offer;
                _offerTcs = new TaskCompletionSource<bool>();
                OfferTitle.Text = Translator.T(offer.Sync ? $"{offer.From} quer sincronizar {offer.Files.Count} arquivo(s) de uma pasta"
                    : $"{offer.From} quer enviar {offer.Files.Count} arquivo(s)");
                OfferInfo.Text = Translator.T($"{Format.Size(offer.Total)}  ·  serão salvos em {_settings.ReceiveFolder}");
                OfferFiles.Text = string.Join("\n", offer.Files.Take(50).Select(f =>
                    (string.IsNullOrEmpty(f.Path) ? f.Name : f.Path + "\\" + f.Name) + "   (" + Format.Size(f.Size) + ")"))
                    + (offer.Files.Count > 50 ? $"\n… e mais {offer.Files.Count - 50}" : "");
                bool canTrust = offer.DeviceId != null && !_settings.Trusted.Contains(offer.DeviceId);
                OfferTrust.Visibility = canTrust ? Visibility.Visible : Visibility.Collapsed;
                OfferTrust.IsChecked = false;
                OfferOverlay.Visibility = Visibility.Visible;
                bool wasHidden = !IsVisible || WindowState == WindowState.Minimized;
                ShowFromTray();
                if (wasHidden) _tray?.ShowBalloonTip(4000, "Pedido de envio", OfferTitle.Text, System.Windows.Forms.ToolTipIcon.Info);
                var timeout = Task.Delay(TimeSpan.FromSeconds(85));
                var done = await Task.WhenAny(_offerTcs.Task, timeout);
                OfferOverlay.Visibility = Visibility.Collapsed;
                return done == _offerTcs.Task && _offerTcs.Task.Result;
            });
        }
        finally { _offerGate.Release(); }
    }

    private void OnOfferAccept(object sender, RoutedEventArgs e)
    {
        if (OfferTrust.IsChecked == true && _currentOffer?.DeviceId is { } id && !_settings.Trusted.Contains(id))
        {
            _settings.Trusted.Add(id);
            _settings.Save();
        }
        _offerTcs?.TrySetResult(true);
    }

    private void OnOfferDecline(object sender, RoutedEventArgs e) => _offerTcs?.TrySetResult(false);

    // ------------------------------------------------------------ PIN de outro aparelho
    private void OnEnterPin(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PeerView v) return;
        _pinPeer = v;
        PinTitle.Text = Translator.T($"PIN de {v.Name}");
        PinBox.Text = "";
        PinError.Visibility = Visibility.Collapsed;
        PinOverlay.Visibility = Visibility.Visible;
        PinBox.Focus();
    }

    private void OnPinKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) OnPinOk(sender, e); }

    private async void OnPinOk(object sender, RoutedEventArgs e)
    {
        if (_pinPeer == null) return;
        lock (_settings) _settings.PeerPins[_pinPeer.Peer.Id] = PinBox.Text.Trim();
        _settings.Save();
        await RefreshPeer(_pinPeer);
        if (_pinPeer.NeedsPin) { PinError.Visibility = Visibility.Visible; PinBox.SelectAll(); return; }
        PinOverlay.Visibility = Visibility.Collapsed;
        _pinPeer = null;
    }

    private void OnHidePin(object sender, RoutedEventArgs e) => PinOverlay.Visibility = Visibility.Collapsed;

    // ------------------------------------------------------------ configurações
    private void OnShowSettings(object sender, RoutedEventArgs e)
    {
        PinSwitch.IsChecked = _settings.RequirePin;
        PinText.Text = _settings.Pin;
        PinPanel.Visibility = _settings.RequirePin ? Visibility.Visible : Visibility.Collapsed;
        TrustedSwitch.IsChecked = _settings.TrustedOnly;
        StartupSwitch.IsChecked = _settings.StartWithWindows;
        TraySwitch.IsChecked = _settings.CloseToTray;
        NotifySwitch.IsChecked = _settings.NotifyDownloads;
        AutoAcceptSwitch.IsChecked = _settings.AutoAcceptTrusted;
        EncryptSwitch.IsChecked = _settings.Encrypt;
        _loadingSettings = true;
        ThemeBox.SelectedIndex = _settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        LanguageBox.SelectedIndex = _settings.Language == "en" ? 1 : 0;
        _loadingSettings = false;
        RefreshSyncViews();
        _known.Clear();
        lock (_settings)
            foreach (var (id, name) in _settings.KnownDevices.OrderBy(k => k.Value))
                _known.Add(new KnownDevice { Id = id, Name = name, Trusted = _settings.Trusted.Contains(id) });
        KnownEmpty.Visibility = _known.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void OnHideSettings(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

    private void OnPinSwitch(object sender, RoutedEventArgs e)
    {
        _settings.RequirePin = PinSwitch.IsChecked == true;
        _settings.Save();
        PinPanel.Visibility = _settings.RequirePin ? Visibility.Visible : Visibility.Collapsed;
        UpdatePinBadge();
    }

    private void OnNewPin(object sender, RoutedEventArgs e)
    {
        _settings.Pin = Settings.NewPin();
        _settings.Save();
        PinText.Text = _settings.Pin;
        UpdatePinBadge();
    }

    private void OnTrustedSwitch(object sender, RoutedEventArgs e)
    {
        _settings.TrustedOnly = TrustedSwitch.IsChecked == true;
        _settings.Save();
        UpdatePinBadge();
    }

    private void OnTrustChanged(object sender, RoutedEventArgs e)
    {
        _settings.Trusted = _known.Where(k => k.Trusted).Select(k => k.Id).ToList();
        _settings.Save();
    }

    private void OnStartupSwitch(object sender, RoutedEventArgs e)
    {
        _settings.StartWithWindows = StartupSwitch.IsChecked == true;
        _settings.Save();
        SyncStartupRegistry();
    }

    private void SyncStartupRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (_settings.StartWithWindows && Environment.ProcessPath is { } exe)
                key.SetValue("LAN Transfer", $"\"{exe}\" --minimized");
            else if (key.GetValue("LAN Transfer") != null)
                key.DeleteValue("LAN Transfer", false);
        }
        catch { }
    }

    private void OnTraySwitch(object sender, RoutedEventArgs e) { _settings.CloseToTray = TraySwitch.IsChecked == true; _settings.Save(); }
    private void OnNotifySwitch(object sender, RoutedEventArgs e) { _settings.NotifyDownloads = NotifySwitch.IsChecked == true; _settings.Save(); }

    private void UpdatePinBadge()
    {
        var parts = new List<string>();
        if (_settings.RequirePin) parts.Add($"PIN {_settings.Pin}");
        if (_settings.TrustedOnly) parts.Add("só aparelhos de confiança");
        PinBadge.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PinBadgeText.Text = Translator.T("Protegido: " + string.Join("  ·  ", parts));
    }

    // ------------------------------------------------------------ QR code (sem app)
    private void OnShowQr(object sender, RoutedEventArgs e)
    {
        var ips = TransferServer.LocalIPs();
        int port = _server?.Port ?? _settings.Port;
        string url = $"http://{ips[0]}:{port}/";
        using (var gen = new QRCodeGenerator())
        using (var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M))
        {
            var png = new PngByteQRCode(data).GetGraphic(12);
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(png);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            QrImage.Source = bmp;
        }
        QrUrl.Text = url;
        var extra = new List<string>();
        if (ips.Count > 1) extra.Add("Outros endereços deste PC: " + string.Join(", ", ips.Skip(1).Select(i => $"{i}:{port}")));
        if (_settings.RequirePin) extra.Add($"A página vai pedir o PIN {_settings.Pin}.");
        QrOther.Text = Translator.T(string.Join("\n", extra));
        if (!_settings.Sharing)
            _snack.Show(Translator.T("Compartilhamento pausado"), "Ligue \"Visível\" para a página mostrar os arquivos.", ControlAppearance.Caution,
                new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(4));
        QrOverlay.Visibility = Visibility.Visible;
    }

    private void OnHideQr(object sender, RoutedEventArgs e) => QrOverlay.Visibility = Visibility.Collapsed;

    private void OnCardClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // ------------------------------------------------------------ meus arquivos
    private void OnSharingChanged(object sender, RoutedEventArgs e)
    {
        _settings.Sharing = SharingSwitch.IsChecked == true;
        _settings.Save();
        SharingHint.Text = Translator.T(_settings.Sharing
            ? "Qualquer aparelho com o LAN Transfer na rede vê e baixa estes arquivos. Eles são enviados de onde estão, sem cópia."
            : "Compartilhamento pausado: ninguém vê seus arquivos até você ligar de novo.");
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Escolha os arquivos para disponibilizar na rede" };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Escolha a pasta para disponibilizar (com as subpastas)" };
        if (dlg.ShowDialog(this) == true) AddFolder(dlg.FolderName);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
    }

    private bool AddShared(SharedFile f)
    {
        if (_shared.Any(o => string.Equals(o.Path, f.Path, StringComparison.OrdinalIgnoreCase))) return false;
        f.Visible = Matches(f.Name, f.RelDir);
        _shared.Add(f);
        if (f.HasThumb) _ = LoadLocalThumb(f);
        return true;
    }

    private static async Task LoadLocalThumb(SharedFile f)
    {
        var bytes = await Task.Run(() => Thumbs.LocalAsync(f.Path));
        f.Thumb = Thumbs.ToImage(bytes);
    }

    /// <summary>Arquivos soltos e pastas (pastas mantêm as subpastas).</summary>
    private void AddPaths(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) { added += AddFolder(p, notify: false); continue; }
            if (!File.Exists(p)) continue;
            try { if (AddShared(new SharedFile(p))) added++; } catch { }
        }
        if (added == 0) return;
        SaveShared();
        _snack.Show(Translator.T("Disponível na rede"), $"{added} arquivo(s) agora aparecem para os outros aparelhos.",
            ControlAppearance.Primary, new SymbolIcon(SymbolRegular.Share24), TimeSpan.FromSeconds(3));
    }

    private int AddFolder(string root, bool save = true, bool notify = true)
    {
        root = root.TrimEnd('\\');
        string parent = Path.GetDirectoryName(root) ?? root;
        int added = 0;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
        catch { return 0; }
        foreach (var f in files)
        {
            try
            {
                string rel = Path.GetRelativePath(parent, Path.GetDirectoryName(f)!);
                if (AddShared(new SharedFile(f, rel))) added++;
            }
            catch { }
        }
        if (!_settings.SharedFolders.Any(d => string.Equals(d, root, StringComparison.OrdinalIgnoreCase)))
            _settings.SharedFolders.Add(root);
        if (save) SaveShared();
        if (notify && added > 0)
            _snack.Show(Translator.T("Pasta disponível na rede"), $"{added} arquivo(s) de \"{Path.GetFileName(root)}\", com as subpastas.",
                ControlAppearance.Primary, new SymbolIcon(SymbolRegular.Share24), TimeSpan.FromSeconds(3));
        return added;
    }

    private void OnRemoveShared(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SharedFile item) return;
        _shared.Remove(item);
        // se era a última de uma pasta compartilhada, esquece a pasta
        if (!string.IsNullOrEmpty(item.RelDir))
            _settings.SharedFolders.RemoveAll(d => !_shared.Any(s => s.Path.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)));
        SaveShared();
    }

    private void OnClearShared(object sender, RoutedEventArgs e)
    {
        _shared.Clear();
        _settings.SharedFolders.Clear();
        SaveShared();
    }

    private void SaveShared()
    {
        _settings.SharedPaths = _shared.Where(s => string.IsNullOrEmpty(s.RelDir)).Select(s => s.Path).ToList();
        _settings.Save();
    }

    private void UpdateEmptyStates()
    {
        SharedEmpty.Visibility = _shared.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PeersEmpty.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadsEmpty.Visibility = _downloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
