using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace WiFiTransfer;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Arquivo deste PC disponível na rede (servido do local original, sem cópia).</summary>
public sealed class SharedFile : Observable
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public string Path { get; }
    /// <summary>Subpasta relativa quando veio de uma pasta compartilhada ("Fotos/2024"), senão "".</summary>
    public string RelDir { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Display => string.IsNullOrEmpty(RelDir) ? Name : RelDir + "/" + Name;
    public long Size { get; }
    public string SizeText => Format.Size(Size);
    public bool HasThumb => Format.IsMedia(Name);
    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, Translator.T(value)); }
    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    private bool _busy;
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    private System.Windows.Media.ImageSource? _thumb;
    public System.Windows.Media.ImageSource? Thumb { get => _thumb; set { Set(ref _thumb, value); Raise(nameof(ShowThumb)); } }
    public bool ShowThumb => _thumb != null;
    private bool _visible = true;
    public bool Visible { get => _visible; set => Set(ref _visible, value); }

    public SharedFile(string path, string relDir = "")
    {
        Path = path;
        RelDir = relDir.Replace('\\', '/').Trim('/');
        Size = new FileInfo(path).Length;
        _status = Translator.T(string.IsNullOrEmpty(RelDir) ? SizeText : $"{SizeText}  ·  pasta {RelDir}");
    }
}

/// <summary>Outro aparelho encontrado na rede.</summary>
public sealed record Peer(string Id, string Name, string Host, int Port, string Kind, int HttpsPort = 0, string Fingerprint = "");

public sealed class RemoteFileView : Observable
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string RelDir { get; init; } = "";
    public long Size { get; init; }
    public bool CanThumb { get; init; }
    public bool CanPreview => Format.CanPreview(Name);
    private System.Windows.Media.ImageSource? _thumb;
    public System.Windows.Media.ImageSource? Thumb { get => _thumb; set { Set(ref _thumb, value); Raise(nameof(ShowThumb)); } }
    public bool ShowThumb => _thumb != null;
    public string SizeText => string.IsNullOrEmpty(RelDir) ? Format.Size(Size) : $"{Format.Size(Size)}  ·  {RelDir}";
    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    private bool _visible = true;
    public bool Visible { get => _visible; set => Set(ref _visible, value); }
}

public sealed class PeerView : Observable
{
    public Peer Peer { get; set; }
    public string Name => Peer.Name;
    private bool _needsPin;
    public bool NeedsPin { get => _needsPin; set { Set(ref _needsPin, value); Raise(nameof(Subtitle)); } }
    private bool _blocked;
    public bool Blocked { get => _blocked; set { Set(ref _blocked, value); Raise(nameof(Subtitle)); } }
    public string Subtitle => Translator.T(SubtitleRaw);
    private string SubtitleRaw => Blocked ? $"Só compartilha com aparelhos de confiança  ·  {Peer.Host}"
        : NeedsPin ? $"Protegido por PIN  ·  {Peer.Host}"
        : $"{Files.Count} arquivo(s)  ·  {Peer.Host}";
    public Wpf.Ui.Controls.SymbolRegular Icon => Peer.Kind == "pc" ? Wpf.Ui.Controls.SymbolRegular.Desktop24 : Wpf.Ui.Controls.SymbolRegular.Phone24;
    public ObservableCollection<RemoteFileView> Files { get; } = new();
    public bool HasFiles => Files.Count > 0;
    public bool ShowEmpty => Files.Count == 0 && !NeedsPin && !Blocked;
    private bool _anyVisible = true;
    /// <summary>false quando a busca escondeu todos os arquivos deste aparelho.</summary>
    public bool AnyVisible { get => _anyVisible; set => Set(ref _anyVisible, value); }
    public PeerView(Peer peer) { Peer = peer; }
    public void Refresh() { Raise(nameof(NeedsPin)); Raise(nameof(Blocked)); Raise(nameof(Subtitle)); Raise(nameof(HasFiles)); Raise(nameof(ShowEmpty)); Raise(nameof(Name)); Raise(nameof(Icon)); }
}

/// <summary>Arquivo baixado/recebido (ou em andamento).</summary>
public sealed class DownloadItem : Observable
{
    private string _name;
    public string Name { get => _name; set => Set(ref _name, value); }
    private string? _path;
    public string? Path { get => _path; set { Set(ref _path, value); Raise(nameof(CanOpen)); } }
    public long Size { get; }
    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    private bool _busy;
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    private string _status;
    public string Status { get => _status; set => Set(ref _status, Translator.T(value)); }
    public string? From { get; init; }
    public bool Outgoing { get; init; }
    public bool IsText { get; init; }
    public string? Text { get; init; }

    // ---- velocidade, pausar e cancelar
    public System.Threading.CancellationTokenSource? Cts { get; set; }
    public bool PauseRequested { get; set; }
    public bool CancelRequested { get; set; }
    /// <summary>Ação para continuar um download pausado/interrompido.</summary>
    public Action? ResumeAction { get; set; }
    private bool _canPause, _canResume, _canCancel;
    public bool CanPause { get => _canPause; set => Set(ref _canPause, value); }
    public bool CanResume { get => _canResume; set => Set(ref _canResume, value); }
    public bool CanCancel { get => _canCancel; set => Set(ref _canCancel, value); }
    public bool CanOpen => Path != null || IsText;

    private DateTime _lastT = DateTime.MinValue;
    private long _lastB;
    private double _speed;
    /// <summary>Atualiza progresso, velocidade e tempo restante.</summary>
    public void Report(long bytes, string verb)
    {
        var now = DateTime.UtcNow;
        if (_lastT == DateTime.MinValue) { _lastT = now; _lastB = bytes; }
        double dt = (now - _lastT).TotalSeconds;
        if (dt >= 0.5)
        {
            double inst = (bytes - _lastB) / dt;
            _speed = _speed <= 0 ? inst : _speed * 0.7 + inst * 0.3;
            _lastT = now; _lastB = bytes;
        }
        Progress = Size > 0 ? bytes * 100.0 / Size : 0;
        string extra = _speed > 0 ? $"  ·  {Format.Speed(_speed)}  ·  faltam {Format.Eta((Size - bytes) / _speed)}" : "";
        Status = $"{verb}… {Progress:0}%  ·  {Format.Size(bytes)} de {Format.Size(Size)}{extra}";
    }
    public void ResetSpeed() { _lastT = DateTime.MinValue; _speed = 0; }
    public Wpf.Ui.Controls.SymbolRegular Icon => IsText ? Wpf.Ui.Controls.SymbolRegular.ClipboardPaste24
        : Outgoing ? Wpf.Ui.Controls.SymbolRegular.ArrowUpload24 : Wpf.Ui.Controls.SymbolRegular.ArrowDownload24;

    public DownloadItem(string name, long size, bool busy, string status)
    {
        _name = name; Size = size; _busy = busy; _status = Translator.T(status);
    }
}

/// <summary>Aparelho na lista de confiáveis (tela de configurações).</summary>
public sealed class KnownDevice : Observable
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    private bool _trusted;
    public bool Trusted { get => _trusted; set => Set(ref _trusted, value); }
}

public static class Format
{
    public static string Size(double n)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; }
        return i == 0 ? $"{n:0} {u[i]}" : $"{n:0.0} {u[i]}";
    }

    private static readonly string[] MediaExt = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff",
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".3gp", ".m4v" };
    public static bool IsMedia(string name) => MediaExt.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant());

    private static readonly System.Collections.Generic.Dictionary<string, string> Mimes = new()
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".bmp"] = "image/bmp", [".heic"] = "image/heic", [".svg"] = "image/svg+xml",
        [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".webm"] = "video/webm", [".mov"] = "video/quicktime", [".mkv"] = "video/x-matroska",
        [".3gp"] = "video/3gpp", [".avi"] = "video/x-msvideo",
        [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4", [".aac"] = "audio/aac", [".ogg"] = "audio/ogg", [".opus"] = "audio/ogg",
        [".wav"] = "audio/wav", [".flac"] = "audio/flac",
        [".pdf"] = "application/pdf", [".txt"] = "text/plain; charset=utf-8", [".md"] = "text/plain; charset=utf-8",
        [".json"] = "application/json", [".html"] = "text/plain; charset=utf-8", [".csv"] = "text/plain; charset=utf-8",
    };
    public static string Mime(string name) => Mimes.GetValueOrDefault(System.IO.Path.GetExtension(name).ToLowerInvariant(), "application/octet-stream");
    public static bool CanPreview(string name) => Mimes.ContainsKey(System.IO.Path.GetExtension(name).ToLowerInvariant());

    public static string Speed(double bytesPerSec) => bytesPerSec <= 0 ? "" : Size(bytesPerSec) + "/s";
    public static string Eta(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) return "";
        var t = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}" : t.TotalMinutes >= 1 ? $"{t.Minutes}min{t.Seconds:00}s" : $"{t.Seconds}s";
    }
}

public sealed class HistoryView
{
    public HistoryEntry E { get; init; } = new();
    public string Name => E.Name;
    public string Sub => Translator.T(SubRaw);
    private string SubRaw => $"{E.Time:dd/MM HH:mm}  ·  " + E.Kind switch
    {
        "down" => $"baixado de {E.Peer}",
        "up" => $"enviado para {E.Peer}",
        "in" => $"recebido de {E.Peer}",
        "text-in" => $"texto recebido de {E.Peer}",
        "text-out" => $"texto enviado para {E.Peer}",
        _ => E.Peer
    } + (E.Size > 0 ? $"  ·  {Format.Size(E.Size)}" : "");
    public Wpf.Ui.Controls.SymbolRegular Icon => E.Kind switch
    {
        "up" => Wpf.Ui.Controls.SymbolRegular.ArrowUpload24,
        "text-in" or "text-out" => Wpf.Ui.Controls.SymbolRegular.ClipboardPaste24,
        _ => Wpf.Ui.Controls.SymbolRegular.ArrowDownload24
    };
}

public sealed class SyncView
{
    public SyncJob Job { get; init; } = new();
    public string Title => System.IO.Path.GetFileName(Job.Folder.TrimEnd('\\')) + "  →  " + Job.PeerName;
    public string Sub => Translator.T(Job.Folder + (Job.LastSync is { } t ? $"  ·  última vez {t:dd/MM HH:mm}" : "  ·  ainda não sincronizada") + $"  ·  {Job.Sent.Count} arquivo(s) enviados");
}
