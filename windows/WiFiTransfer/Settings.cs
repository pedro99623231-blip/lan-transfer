using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace WiFiTransfer;

public sealed class Settings
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public bool Sharing { get; set; } = true;
    public List<string> SharedPaths { get; set; } = new();
    /// <summary>Pastas compartilhadas inteiras (caminho da pasta raiz).</summary>
    public List<string> SharedFolders { get; set; } = new();
    public string ReceiveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LAN Transfer");
    public int Port { get; set; } = 8000;

    // Segurança
    public bool RequirePin { get; set; }
    public string Pin { get; set; } = NewPin();
    public bool TrustedOnly { get; set; }
    public List<string> Trusted { get; set; } = new();
    /// <summary>Aparelhos já vistos: id -> nome (para a lista de confiáveis).</summary>
    public Dictionary<string, string> KnownDevices { get; set; } = new();
    /// <summary>PIN digitado para cada aparelho que exige PIN: id -> PIN.</summary>
    public Dictionary<string, string> PeerPins { get; set; } = new();

    public bool AutoAcceptTrusted { get; set; }
    /// <summary>Usa HTTPS com os aparelhos que suportam (v5+).</summary>
    public bool Encrypt { get; set; } = true;
    /// <summary>Pastas enviadas automaticamente para um aparelho.</summary>
    public List<SyncJob> SyncJobs { get; set; } = new();

    // Aparência
    public string Theme { get; set; } = "system";   // system | light | dark
    public string Language { get; set; } = "pt";    // pt | en

    // Windows
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool NotifyDownloads { get; set; } = true;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WiFiTransfer");

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WiFiTransfer", "settings.json");

    public static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                if (loaded.ReceiveFolder.EndsWith(Path.Combine("Downloads", "WiFiTransfer"), StringComparison.OrdinalIgnoreCase))
                    loaded.ReceiveFolder = new Settings().ReceiveFolder;
                return loaded;
            }
        }
        catch { }
        var s = new Settings();
        s.Save();
        return s;
    }

    private static readonly object Lock = new();
    public void Save()
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}

public sealed class SyncJob
{
    public string Folder { get; set; } = "";
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    /// <summary>Arquivos já enviados: caminho relativo -> "tamanho|data".</summary>
    public Dictionary<string, string> Sent { get; set; } = new();
    public DateTime? LastSync { get; set; }
}

public sealed class HistoryEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Kind { get; set; } = "down";  // down | up | in | text-in | text-out
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Peer { get; set; } = "";
    public string? Path { get; set; }
}

/// <summary>Histórico de transferências (fica salvo depois de fechar o app).</summary>
public static class History
{
    private static readonly object Lock = new();
    private static string FilePath => System.IO.Path.Combine(Settings.Dir, "history.json");
    private static List<HistoryEntry>? _items;

    public static List<HistoryEntry> Items
    {
        get
        {
            lock (Lock)
            {
                if (_items == null)
                {
                    try { _items = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) : null; } catch { }
                    _items ??= new();
                }
                return _items.ToList();
            }
        }
    }

    public static event Action? Changed;

    public static void Add(HistoryEntry e)
    {
        lock (Lock)
        {
            _ = Items;
            _items!.Insert(0, e);
            if (_items.Count > 1000) _items.RemoveRange(1000, _items.Count - 1000);
            Save();
        }
        Changed?.Invoke();
    }

    public static void Clear() { lock (Lock) { _items = new(); Save(); } Changed?.Invoke(); }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_items));
        }
        catch { }
    }
}
