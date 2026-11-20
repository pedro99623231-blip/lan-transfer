using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Formats.Tar;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace WiFiTransfer;

public sealed record OfferFile(string Name, long Size, string Path);
public sealed record Offer(string From, string? DeviceId, List<OfferFile> Files, bool Sync = false)
{
    public long Total => Files.Sum(f => f.Size);
}

/// <summary>
/// Servidor do LAN Transfer.
///   UDP 48555  "WIFITRANSFER_DISCOVER" -> "WIFITRANSFER_PEER|id|nome|porta|pc"
///   GET  /api/ping
///   GET  /api/files          -> [{"id","name","size","path","thumb"}]
///   GET  /files/{id}         -> conteúdo (com Range, para retomar)
///   GET  /thumb/{id}         -> miniatura (fotos e vídeos)
///   GET  /api/zip[?id=..]    -> vários arquivos (navegador)
///   POST /api/offer          -> {"from","device","files":[{"name","size","path"}]} ; pergunta ao usuário; -> {"token"}
///   POST /upload?name=&path=&offer=TOKEN  -> recebe um arquivo da oferta aceita
///   GET  /                   -> página para navegador (QR code)
/// Identificação/segurança: headers X-Device, X-Device-Name, X-Pin (ou ?t= / cookie "lt").
/// </summary>
public sealed class TransferServer : IAsyncDisposable
{
    public const int DiscoveryPort = 48555;
    private const int Chunk = 1024 * 1024;

    private readonly Settings _settings;
    private readonly Func<List<SharedFile>> _shared;
    private readonly Func<Offer, Task<bool>> _askOffer;
    private readonly ConcurrentDictionary<string, (Offer offer, DateTime expires)> _offers = new();
    private WebApplication? _app;
    private UdpClient? _udp;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; private set; }
    public int HttpsPort { get; private set; }
    public string Fingerprint { get; private set; } = "";
    public event Action<string, string, bool>? TextReceived;      // de quem, texto, de aparelho confiável
    public event Action<SharedFile, long>? Serving;               // item, bytes enviados
    public event Action<SharedFile, bool, string>? Served;        // item, completo, quem baixou
    public event Action<string, string, long, string>? IncomingStart;    // id, nome, tamanho, de quem
    public event Action<string, long>? IncomingProgress;
    public event Action<string, string?, string?>? IncomingDone;  // id, caminho, erro
    public event Action<string, string>? DeviceSeen;              // id, nome

    public TransferServer(Settings settings, Func<List<SharedFile>> shared, Func<Offer, Task<bool>> askOffer)
    {
        _settings = settings;
        _shared = shared;
        _askOffer = askOffer;
    }

    private List<SharedFile> Visible() => _settings.Sharing ? _shared().Where(f => File.Exists(f.Path)).ToList() : new();

    public async Task StartAsync()
    {
        Exception? last = null;
        for (int port = _settings.Port; port < _settings.Port + 20; port++)
        {
            try
            {
                var builder = WebApplication.CreateSlimBuilder();
                builder.Logging.ClearProviders();
                var cert = Certificate();
                builder.WebHost.UseKestrelHttpsConfiguration();
                builder.WebHost.UseKestrel(o =>
                {
                    o.ListenAnyIP(port);
                    // HTTPS entre aparelhos com o app (o navegador continua em HTTP)
                    if (cert != null) o.ListenAnyIP(port + 100, l => l.UseHttps(cert));
                    o.AllowSynchronousIO = true;
                    o.Limits.MaxRequestBodySize = null;
                });
                HttpsPort = cert != null ? port + 100 : 0;
                Fingerprint = cert != null ? Convert.ToHexString(SHA256.HashData(cert.RawData)) : "";
                var app = builder.Build();
                Map(app);
                await app.StartAsync();
                _app = app;
                Port = port;
                break;
            }
            catch (Exception ex) when (ex is IOException || ex.InnerException is IOException)
            {
                last = ex;
            }
        }
        if (_app == null) throw new InvalidOperationException("Nenhuma porta livre", last);
        StartDiscoveryResponder(_cts.Token);
    }

    /// <summary>Certificado próprio (ECDSA, autoassinado), criado uma vez e guardado nas configurações.</summary>
    private static X509Certificate2? Certificate()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WiFiTransfer", "cert.pfx");
            const string pwd = "lantransfer";
            if (!File.Exists(path))
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var req = new CertificateRequest("CN=LAN Transfer " + Environment.MachineName, key, HashAlgorithmName.SHA256);
                using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, c.Export(X509ContentType.Pfx, pwd));
            }
            return new X509Certificate2(File.ReadAllBytes(path), pwd, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ acesso
    private static string? Header(HttpContext ctx, string name) => ctx.Request.Headers[name].FirstOrDefault();

    private static string Who(HttpContext ctx)
    {
        var n = Header(ctx, "X-Device-Name");
        if (!string.IsNullOrEmpty(n)) return Uri.UnescapeDataString(n);
        return "Navegador (" + ctx.Connection.RemoteIpAddress?.MapToIPv4() + ")";
    }

    /// <summary>null = liberado; senão o código HTTP a devolver (401 PIN, 403 não confiável).</summary>
    private int? Check(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote != null && IPAddress.IsLoopback(remote)) return null;
        string? dev = Header(ctx, "X-Device") ?? ctx.Request.Query["d"].FirstOrDefault();
        if (dev != null)
        {
            var name = Header(ctx, "X-Device-Name");
            DeviceSeen?.Invoke(dev, name != null ? Uri.UnescapeDataString(name) : dev);
        }
        if (_settings.TrustedOnly && (dev == null || !_settings.Trusted.Contains(dev))) return 403;
        if (_settings.RequirePin)
        {
            string? pin = Header(ctx, "X-Pin") ?? ctx.Request.Query["t"].FirstOrDefault() ?? ctx.Request.Cookies["lt"];
            if (pin != _settings.Pin) return 401;
            if (ctx.Request.Query.ContainsKey("t"))
                ctx.Response.Cookies.Append("lt", pin, new CookieOptions { Path = "/", SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(1) });
        }
        return null;
    }

    private static Task Deny(HttpContext ctx, int code)
    {
        ctx.Response.StatusCode = code;
        return ctx.Response.WriteAsJsonAsync(new { needPin = code == 401, blocked = code == 403 });
    }

    // ------------------------------------------------------------------ rotas
    private void Map(WebApplication app)
    {
        app.MapGet("/api/ping", () => Results.Json(new
        {
            app = "WiFiTransfer", id = _settings.DeviceId, name = Environment.MachineName, kind = "pc", version = 5,
            httpsPort = HttpsPort, fingerprint = Fingerprint,
            pin = _settings.RequirePin, trustedOnly = _settings.TrustedOnly
        }));

        app.MapGet("/", async (HttpContext ctx) =>
        {
            var code = Check(ctx);
            string html;
            if (code == 401) html = Resource("pin.html");
            else if (code == 403) html = "<meta name=viewport content='width=device-width'><h2>Acesso não permitido</h2><p>Este aparelho só compartilha com aparelhos de confiança.</p>";
            else html = Resource("index.html");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(html.Replace("{{NAME}}", WebUtility.HtmlEncode(Environment.MachineName)));
        });

        app.MapGet("/api/files", async (HttpContext ctx) =>
        {
            if (Check(ctx) is int c) { await Deny(ctx, c); return; }
            await ctx.Response.WriteAsJsonAsync(Visible().Select(i => new { id = i.Id, name = i.Name, size = i.Size, path = i.RelDir, thumb = i.HasThumb }));
        });

        app.MapGet("/files/{id}", async (HttpContext ctx, string id) =>
        {
            if (Check(ctx) is int c) { await Deny(ctx, c); return; }
            var item = Visible().FirstOrDefault(i => i.Id == id);
            if (item == null) { ctx.Response.StatusCode = 404; return; }
            await SendFile(ctx, item, ctx.Request.Query.ContainsKey("inline"));
        });

        app.MapGet("/thumb/{id}", async (HttpContext ctx, string id) =>
        {
            if (Check(ctx) is int c) { await Deny(ctx, c); return; }
            var item = Visible().FirstOrDefault(i => i.Id == id);
            if (item == null || !item.HasThumb) { ctx.Response.StatusCode = 404; return; }
            var bytes = await Thumbnail(item);
            if (bytes == null) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = "image/png";
            ctx.Response.Headers.CacheControl = "max-age=3600";
            await ctx.Response.Body.WriteAsync(bytes);
        });

        app.MapGet("/api/zip", async (HttpContext ctx) =>
        {
            if (Check(ctx) is int c) { await Deny(ctx, c); return; }
            var ids = ctx.Request.Query["id"].Where(i => i != null).ToHashSet();
            var items = Visible().Where(i => ids.Count == 0 || ids.Contains(i.Id)).ToList();
            if (items.Count == 0) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers.ContentDisposition = "attachment; filename=\"arquivos-" + Environment.MachineName + ".zip\"";
            string who = Who(ctx);
            using var zip = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                string entryName = item.Display;
                for (int n = 1; !used.Add(entryName); n++)
                    entryName = $"{Path.GetFileNameWithoutExtension(item.Display)} ({n}){Path.GetExtension(item.Name)}";
                using var es = zip.CreateEntry(entryName, CompressionLevel.NoCompression).Open();
                using var fs = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, Chunk);
                fs.CopyTo(es, Chunk);
                Served?.Invoke(item, true, who);
            }
        });

        // ---- receber arquivos (envio direto de outro aparelho ou pelo navegador)
        app.MapPost("/api/offer", async (HttpContext ctx) =>
        {
            // Ofertas sempre pedem confirmação na tela; respeitam "só confiáveis" para aparelhos com o app.
            var dev = Header(ctx, "X-Device");
            if (_settings.TrustedOnly && dev != null && !_settings.Trusted.Contains(dev)) { await Deny(ctx, 403); return; }
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = doc.RootElement;
            var files = root.GetProperty("files").EnumerateArray().Select(f => new OfferFile(
                SafeName(f.GetProperty("name").GetString() ?? "arquivo"),
                f.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                f.TryGetProperty("path", out var p) ? SafeRelDir(p.GetString() ?? "") : "")).ToList();
            string from = root.TryGetProperty("from", out var fr) && !string.IsNullOrWhiteSpace(fr.GetString()) ? fr.GetString()! : Who(ctx);
            bool sync = root.TryGetProperty("sync", out var sy) && sy.ValueKind == JsonValueKind.True;
            var offer = new Offer(from, dev, files, sync);
            bool ok;
            try { ok = await _askOffer(offer).WaitAsync(TimeSpan.FromSeconds(90)); }
            catch (TimeoutException) { ok = false; }
            if (!ok) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "Recusado" }); return; }
            string token = Guid.NewGuid().ToString("N");
            _offers[token] = (offer, DateTime.UtcNow.AddHours(2));
            await ctx.Response.WriteAsJsonAsync(new { token });
        });

        app.MapPost("/api/text", async (HttpContext ctx) =>
        {
            var dev = Header(ctx, "X-Device");
            if (_settings.TrustedOnly && dev != null && !_settings.Trusted.Contains(dev)) { await Deny(ctx, 403); return; }
            if (dev != null && Check(ctx) is int c) { await Deny(ctx, c); return; }
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            string text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (text.Length == 0 || text.Length > 1_000_000) { ctx.Response.StatusCode = 400; return; }
            string from = doc.RootElement.TryGetProperty("from", out var fr) && !string.IsNullOrWhiteSpace(fr.GetString()) ? fr.GetString()! : Who(ctx);
            TextReceived?.Invoke(from, text, dev != null && _settings.Trusted.Contains(dev));
            await ctx.Response.WriteAsJsonAsync(new { ok = true });
        });

        app.MapPost("/upload", async (HttpContext ctx) =>
        {
            if (ctx.Request.Query.ContainsKey("tar")) { await ReceiveTar(ctx); return; }
            string token = Header(ctx, "X-Offer") ?? ctx.Request.Query["offer"].FirstOrDefault() ?? "";
            if (!_offers.TryGetValue(token, out var entry) || entry.expires < DateTime.UtcNow)
            { ctx.Response.StatusCode = 403; return; }
            string name = SafeName(ctx.Request.Query["name"].FirstOrDefault() ?? "arquivo");
            string rel = SafeRelDir(ctx.Request.Query["path"].FirstOrDefault() ?? "");
            long? length = ctx.Request.ContentLength;
            string folder = string.IsNullOrEmpty(rel) ? _settings.ReceiveFolder : Path.Combine(_settings.ReceiveFolder, rel);
            Directory.CreateDirectory(folder);
            string id = Guid.NewGuid().ToString("N");
            string tmp = Path.Combine(_settings.ReceiveFolder, ".part-" + id[..8]);
            IncomingStart?.Invoke(id, name, length ?? 0, entry.offer.From);
            long received = 0;
            try
            {
                var sw = Stopwatch.StartNew();
                await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, Chunk, true))
                {
                    var buf = new byte[Chunk];
                    int n;
                    while ((n = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                        received += n;
                        if (sw.ElapsedMilliseconds > 200) { IncomingProgress?.Invoke(id, received); sw.Restart(); }
                    }
                }
                if (length != null && received != length) throw new IOException("Transferência interrompida");
                bool overwrite = ctx.Request.Query.ContainsKey("overwrite");
                string dest = overwrite ? Path.Combine(folder, name) : UniquePath(folder, name);
                if (overwrite && File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
                IncomingDone?.Invoke(id, dest, null);
                await ctx.Response.WriteAsJsonAsync(new { ok = true });
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                IncomingDone?.Invoke(id, null, ex.Message);
                ctx.Response.StatusCode = 500;
            }
        });
    }

    /// <summary>Vários arquivos pequenos num pacote só (tar), para ir mais rápido.</summary>
    private async Task ReceiveTar(HttpContext ctx)
    {
        string token = Header(ctx, "X-Offer") ?? ctx.Request.Query["offer"].FirstOrDefault() ?? "";
        if (!_offers.TryGetValue(token, out var entry) || entry.expires < DateTime.UtcNow) { ctx.Response.StatusCode = 403; return; }
        bool overwrite = ctx.Request.Query.ContainsKey("overwrite");
        string id = Guid.NewGuid().ToString("N");
        long total = entry.offer.Total, got = 0;
        IncomingStart?.Invoke(id, $"{entry.offer.Files.Count} arquivo(s) (pacote)", total, entry.offer.From);
        string? last = null;
        try
        {
            await using var reader = new TarReader(ctx.Request.Body, leaveOpen: true);
            var sw = Stopwatch.StartNew();
            TarEntry? e;
            while ((e = await reader.GetNextEntryAsync(copyData: false, ctx.RequestAborted)) != null)
            {
                if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || e.DataStream == null) continue;
                string rel = SafeRelDir(Path.GetDirectoryName(e.Name.Replace('\\', '/')) ?? "");
                string name = SafeName(Path.GetFileName(e.Name));
                string folder = string.IsNullOrEmpty(rel) ? _settings.ReceiveFolder : Path.Combine(_settings.ReceiveFolder, rel);
                Directory.CreateDirectory(folder);
                string dest = overwrite ? Path.Combine(folder, name) : UniquePath(folder, name);
                string tmp = dest + ".part";
                await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, Chunk, true))
                {
                    var buf = new byte[Chunk]; int n;
                    while ((n = await e.DataStream.ReadAsync(buf, ctx.RequestAborted)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                        got += n;
                        if (sw.ElapsedMilliseconds > 200) { IncomingProgress?.Invoke(id, got); sw.Restart(); }
                    }
                }
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
                last = dest;
            }
            IncomingDone?.Invoke(id, last ?? _settings.ReceiveFolder, null);
            await ctx.Response.WriteAsJsonAsync(new { ok = true });
        }
        catch (Exception ex)
        {
            IncomingDone?.Invoke(id, null, ex.Message);
            ctx.Response.StatusCode = 500;
        }
    }

    private static string Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static readonly object NameLock = new();
    private static string UniquePath(string folder, string name)
    {
        lock (NameLock)
        {
            string p = Path.Combine(folder, name);
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int n = 1; File.Exists(p); n++) p = Path.Combine(folder, $"{stem} ({n}){ext}");
            using (File.Create(p)) { } // reserva o nome
            File.Delete(p);
            return p;
        }
    }

    private static Task<byte[]?> Thumbnail(SharedFile item) => Thumbs.LocalAsync(item.Path);

    private async Task SendFile(HttpContext ctx, SharedFile item, bool inline = false)
    {
        long total = new FileInfo(item.Path).Length, start = 0, end = total - 1;
        var range = ctx.Request.Headers.Range.FirstOrDefault();
        if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes="))
        {
            var parts = range[6..].Split('-');
            if (long.TryParse(parts[0], out var s)) start = s;
            if (parts.Length > 1 && long.TryParse(parts[1], out var e)) end = Math.Min(e, total - 1);
            ctx.Response.StatusCode = 206;
            ctx.Response.Headers.ContentRange = $"bytes {start}-{end}/{total}";
        }
        ctx.Response.ContentType = inline ? Format.Mime(item.Name) : "application/octet-stream";
        ctx.Response.ContentLength = Math.Max(0, end - start + 1);
        ctx.Response.Headers.AcceptRanges = "bytes";
        ctx.Response.Headers.ContentDisposition = (inline ? "inline" : "attachment") + "; filename*=UTF-8''" + Uri.EscapeDataString(item.Name);

        bool ok = false;
        string who = Who(ctx);
        try
        {
            await using var fs = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, Chunk, true);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[Chunk];
            long left = end - start + 1, sent = start;
            var sw = Stopwatch.StartNew();
            while (left > 0)
            {
                int n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), ctx.RequestAborted);
                if (n <= 0) break;
                await ctx.Response.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                left -= n; sent += n;
                if (sw.ElapsedMilliseconds > 250) { Serving?.Invoke(item, sent); sw.Restart(); }
            }
            ok = left <= 0;
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        Served?.Invoke(item, ok, who);
    }

    private void StartDiscoveryResponder(CancellationToken ct)
    {
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        }
        catch { _udp = null; return; }
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var r = await _udp.ReceiveAsync(ct);
                    if (Encoding.UTF8.GetString(r.Buffer).Trim() == "WIFITRANSFER_DISCOVER")
                    {
                        var reply = Encoding.UTF8.GetBytes($"WIFITRANSFER_PEER|{_settings.DeviceId}|{Environment.MachineName}|{Port}|pc|{HttpsPort}|{Fingerprint}");
                        await _udp.SendAsync(reply, r.RemoteEndPoint, ct);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(200); }
            }
        }, ct);
    }

    public static string SafeName(string name)
    {
        name = Path.GetFileName(name.Replace('\\', '/').Split('/').Last()).Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim('.', ' ');
        return string.IsNullOrEmpty(name) ? "arquivo" : name;
    }

    /// <summary>Subpasta relativa segura ("a/b"), sem "..", raiz ou caracteres inválidos.</summary>
    public static string SafeRelDir(string rel)
    {
        var parts = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim()).Where(p => p.Length > 0 && p != "." && p != "..").Select(SafeName);
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    /// <summary>IPs IPv4 da rede local, o principal primeiro (ignora loopback, VPN/túneis e 169.254).</summary>
    public static List<string> LocalIPs()
    {
        var list = new List<string>();
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("10.255.255.255", 1);
            if (s.LocalEndPoint is IPEndPoint ep) list.Add(ep.Address.ToString());
        }
        catch { }
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
            if (d.Contains("tailscale") || d.Contains("vpn") || d.Contains("virtual") || d.Contains("vethernet") || d.Contains("wsl")) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string ip = ua.Address.ToString();
                if (ip.StartsWith("169.254.") || ip.StartsWith("127.") || list.Contains(ip)) continue;
                list.Add(ip);
            }
        }
        if (list.Count > 1 && list[0].StartsWith("100.")) list.RemoveAt(0);
        if (list.Count == 0) list.Add("127.0.0.1");
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udp?.Dispose();
        if (_app != null)
        {
            try { using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await _app.StopAsync(stopCts.Token); } catch { }
            await _app.DisposeAsync();
        }
    }
}
