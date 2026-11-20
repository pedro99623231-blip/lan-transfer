using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WiFiTransfer;

public sealed record RemoteFile(string Id, string Name, long Size, string RelDir = "", bool Thumb = false);

/// <summary>Descobre outros aparelhos (mesma rede e redes vizinhas do mesmo modem) e baixa arquivos deles.</summary>
public static class PeerNetwork
{
    private const int Chunk = 1024 * 1024;
    /// <summary>Certificados conhecidos (anunciados na descoberta). HTTPS só aceita estes.</summary>
    private static readonly ConcurrentDictionary<string, byte> KnownCerts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> HttpsFailed = new();
    public static bool Encrypt = true;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(4),
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
                cert != null && KnownCerts.ContainsKey(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData())))
        }
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private static bool UseHttps(Peer p) => Encrypt && p.HttpsPort > 0 && p.Fingerprint.Length > 0
        && !(HttpsFailed.TryGetValue(p.Id, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(2));

    public static bool IsEncrypted(Peer p) => UseHttps(p);

    private static string Base(Peer p) => UseHttps(p) ? $"https://{p.Host}:{p.HttpsPort}" : $"http://{p.Host}:{p.Port}";

    private static readonly string[] CommonPrefixes = { "192.168.0", "192.168.1", "192.168.2", "192.168.3", "192.168.15", "192.168.100", "10.0.0", "10.0.1" };
    private static List<string>? _routerPrefixes;

    /// <summary>Envia o pedido de descoberta e coleta as respostas. [wide] = também varre as redes vizinhas.</summary>
    public static async Task<List<Peer>> DiscoverAsync(string myId, bool wide)
    {
        var found = new ConcurrentDictionary<string, Peer>();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var msg = Encoding.UTF8.GetBytes("WIFITRANSFER_DISCOVER");

        var targets = new List<IPAddress> { IPAddress.Broadcast };
        var own = new HashSet<string>();
        foreach (var ip in TransferServer.LocalIPs())
        {
            var p = Prefix(ip);
            if (own.Add(p)) targets.Add(IPAddress.Parse(p + ".255"));
        }
        if (wide)
        {
            _routerPrefixes ??= await RouterPrefixesAsync();
            var prefixes = new HashSet<string>(_routerPrefixes.Concat(CommonPrefixes));
            prefixes.ExceptWith(own);
            foreach (var p in prefixes)
                for (int h = 1; h < 255; h++) targets.Add(IPAddress.Parse($"{p}.{h}"));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(wide ? 3000 : 1500));
        var recv = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var r = await udp.ReceiveAsync(cts.Token);
                    var parts = Encoding.UTF8.GetString(r.Buffer).Split('|');
                    if (parts.Length >= 5 && parts[0] == "WIFITRANSFER_PEER" && parts[1] != myId && int.TryParse(parts[3], out var port))
                    {
                        int https = parts.Length >= 7 && int.TryParse(parts[5], out var hp) ? hp : 0;
                        string fp = parts.Length >= 7 ? parts[6].Trim() : "";
                        if (fp.Length > 0) KnownCerts[fp] = 1;
                        found[parts[1]] = new Peer(parts[1], parts[2], r.RemoteEndPoint.Address.MapToIPv4().ToString(), port, parts[4], https, fp);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { }
            }
        });
        for (int round = 0; round < 2; round++)
        {
            foreach (var t in targets)
                try { await udp.SendAsync(msg, msg.Length, new IPEndPoint(t, TransferServer.DiscoveryPort)); } catch { }
            await Task.Delay(300);
        }
        await recv;
        return found.Values.ToList();
    }

    /// <summary>Faixas /24 dos roteadores entre este PC e a internet (ex.: modem da operadora).</summary>
    private static async Task<List<string>> RouterPrefixesAsync()
    {
        var list = new List<string>();
        using var ping = new Ping();
        for (int ttl = 1; ttl <= 4; ttl++)
        {
            try
            {
                var reply = await ping.SendPingAsync(IPAddress.Parse("8.8.8.8"), 800, new byte[8], new PingOptions(ttl, true));
                var a = reply.Address?.ToString();
                if (a != null && IsPrivate(a)) list.Add(Prefix(a));
                if (reply.Status == IPStatus.Success) break;
            }
            catch { }
        }
        return list;
    }

    private static string Prefix(string ip) => string.Join('.', ip.Split('.').Take(3));

    private static bool IsPrivate(string ip)
    {
        var b = ip.Split('.').Select(int.Parse).ToArray();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    // ------------------------------------------------------------ identificação
    public static string MyId = "";
    public static Func<string, string?> PinFor = _ => null;

    private static HttpRequestMessage Req(HttpMethod m, Peer peer, string path)
    {
        var r = new HttpRequestMessage(m, Base(peer) + path);
        r.Headers.TryAddWithoutValidation("X-Device", MyId);
        r.Headers.TryAddWithoutValidation("X-Device-Name", Uri.EscapeDataString(Environment.MachineName));
        var pin = PinFor(peer.Id);
        if (!string.IsNullOrEmpty(pin)) r.Headers.TryAddWithoutValidation("X-Pin", pin);
        return r;
    }

    public sealed class AccessException : Exception
    {
        public bool NeedPin { get; }
        public AccessException(bool needPin) : base(needPin ? "PIN necessário" : "Acesso não permitido") { NeedPin = needPin; }
    }

    private static void Check(HttpResponseMessage r)
    {
        if (r.StatusCode == HttpStatusCode.Unauthorized) throw new AccessException(true);
        if (r.StatusCode == HttpStatusCode.Forbidden) throw new AccessException(false);
        r.EnsureSuccessStatusCode();
    }

    public static async Task<List<RemoteFile>> FilesAsync(Peer peer)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        HttpResponseMessage r;
        try { r = await Http.SendAsync(Req(HttpMethod.Get, peer, "/api/files"), cts.Token); }
        catch (Exception) when (UseHttps(peer))
        {
            HttpsFailed[peer.Id] = DateTime.UtcNow;  // volta para HTTP por um tempo
            r = await Http.SendAsync(Req(HttpMethod.Get, peer, "/api/files"), cts.Token);
        }
        using var _r = r;
        Check(r);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(cts.Token));
        return doc.RootElement.EnumerateArray()
            .Select(e => new RemoteFile(e.GetProperty("id").GetString()!, e.GetProperty("name").GetString() ?? "arquivo",
                e.GetProperty("size").GetInt64(),
                e.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                e.TryGetProperty("thumb", out var t) && t.ValueKind == JsonValueKind.True))
            .ToList();
    }

    /// <summary>Endereço para abrir o arquivo no navegador/player sem baixar (HTTP, com PIN e identificação na URL).</summary>
    public static string PreviewUrl(Peer peer, string id)
    {
        string q = $"?inline=1&d={MyId}";
        var pin = PinFor(peer.Id);
        if (!string.IsNullOrEmpty(pin)) q += "&t=" + Uri.EscapeDataString(pin);
        return $"http://{peer.Host}:{peer.Port}/files/{id}{q}";
    }

    public static string PartialPath(Peer peer, RemoteFile file, string folder)
    {
        string rel = TransferServer.SafeRelDir(file.RelDir);
        string target = string.IsNullOrEmpty(rel) ? folder : Path.Combine(folder, rel);
        string key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes($"{peer.Id}|{file.RelDir}|{file.Name}|{file.Size}")))[..12];
        return Path.Combine(target, $".{key}.part");
    }

    public static async Task SendTextAsync(Peer peer, string text)
    {
        var req = Req(HttpMethod.Post, peer, "/api/text");
        req.Content = new StringContent(JsonSerializer.Serialize(new { from = Environment.MachineName, text }), Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var r = await Http.SendAsync(req, cts.Token);
        Check(r);
    }

    public static async Task<byte[]?> ThumbAsync(Peer peer, string id)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var r = await Http.SendAsync(Req(HttpMethod.Get, peer, "/thumb/" + id), cts.Token);
            if (!r.IsSuccessStatusCode) return null;
            return await r.Content.ReadAsByteArrayAsync(cts.Token);
        }
        catch { return null; }
    }

    /// <summary>
    /// Baixa para a pasta indicada (mantendo a subpasta de origem) e devolve o caminho final.
    /// Se a conexão cair, retoma de onde parou (até 20 tentativas) usando Range.
    /// </summary>
    public static async Task<string> DownloadAsync(Peer peer, RemoteFile file, string folder, Action<long> progress, CancellationToken ct = default)
    {
        string rel = TransferServer.SafeRelDir(file.RelDir);
        string target = string.IsNullOrEmpty(rel) ? folder : Path.Combine(folder, rel);
        Directory.CreateDirectory(target);
        // nome estável do .part: permite retomar mesmo depois de fechar o app
        string tmp = PartialPath(peer, file, folder);
        int failures = 0;
        while (true)
        {
            long have = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
            if (have > file.Size) { File.Delete(tmp); have = 0; }
            if (have == file.Size) break;
            try
            {
                var req = Req(HttpMethod.Get, peer, "/files/" + file.Id);
                if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using var r = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                Check(r);
                bool append = have > 0 && r.StatusCode == HttpStatusCode.PartialContent;
                if (!append) have = 0;
                await using var src = await r.Content.ReadAsStreamAsync(ct);
                await using var fs = new FileStream(tmp, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, Chunk, true);
                var buf = new byte[Chunk];
                long got = have; var last = DateTime.UtcNow;
                progress(got);
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                int n;
                while (true)
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(20)); // conexão parada por 20 s = caiu
                    n = await src.ReadAsync(buf, idle.Token);
                    if (n <= 0) break;
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    if ((DateTime.UtcNow - last).TotalMilliseconds > 200) { progress(got); last = DateTime.UtcNow; failures = 0; }
                }
                progress(got);
                if (got == file.Size) break;
                throw new IOException("Transferência interrompida");
            }
            catch (AccessException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) when (!ct.IsCancellationRequested && ++failures <= 20)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, failures * 2)), ct);
            }
        }
        string name = TransferServer.SafeName(file.Name);
        string dest = Path.Combine(target, name);
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 1; File.Exists(dest); i++) dest = Path.Combine(target, $"{stem} ({i}){ext}");
        File.Move(tmp, dest);
        return dest;
    }

    // ------------------------------------------------------------ enviar direto
    public sealed record OutFile(string Path, string RelDir, long Size)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }

    /// <summary>Pede para enviar; true se o outro aparelho aceitou. Lança se não respondeu.</summary>
    public static async Task<string?> OfferAsync(Peer peer, IReadOnlyList<OutFile> files, CancellationToken ct = default, bool sync = false)
    {
        var req = Req(HttpMethod.Post, peer, "/api/offer");
        var body = JsonSerializer.Serialize(new
        {
            from = Environment.MachineName,
            device = MyId,
            sync,
            files = files.Select(f => new { name = f.Name, size = f.Size, path = f.RelDir.Replace('\\', '/') })
        });
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(100));
        using var r = await Http.SendAsync(req, cts.Token);
        if (r.StatusCode == HttpStatusCode.Forbidden) return null;
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(cts.Token));
        return doc.RootElement.GetProperty("token").GetString();
    }

    /// <summary>Vale agrupar num pacote: muitos arquivos pequenos.</summary>
    public static bool ShouldBundle(IReadOnlyList<OutFile> files) =>
        files.Count >= 15 && files.Sum(f => f.Size) / files.Count < 2 * 1024 * 1024;

    /// <summary>Envia vários arquivos num pacote só (tar, sem recomprimir), mantendo as subpastas.</summary>
    public static async Task UploadBundleAsync(Peer peer, string token, IReadOnlyList<OutFile> files, Action<long> progress, CancellationToken ct = default, bool overwrite = false)
    {
        var req = Req(HttpMethod.Post, peer, $"/upload?offer={token}&tar=1" + (overwrite ? "&overwrite=1" : ""));
        req.Content = new TarContent(files, progress);
        using var r = await Http.SendAsync(req, ct);
        r.EnsureSuccessStatusCode();
    }

    private sealed class TarContent : HttpContent
    {
        private readonly IReadOnlyList<OutFile> _files; private readonly Action<long> _progress;
        public TarContent(IReadOnlyList<OutFile> files, Action<long> progress)
        {
            _files = files; _progress = progress;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
        }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            long sent = 0; var last = DateTime.UtcNow;
            await using (var w = new System.Formats.Tar.TarWriter(stream, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
            {
                foreach (var f in _files)
                {
                    string name = string.IsNullOrEmpty(f.RelDir) ? f.Name : f.RelDir.Replace('\\', '/') + "/" + f.Name;
                    await using var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, Chunk, true);
                    var e = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, name) { DataStream = fs };
                    await w.WriteEntryAsync(e);
                    sent += f.Size;
                    if ((DateTime.UtcNow - last).TotalMilliseconds > 200) { _progress(sent); last = DateTime.UtcNow; }
                }
            }
            _progress(sent);
        }
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }

    public static async Task UploadAsync(Peer peer, string token, OutFile file, Action<long> progress, CancellationToken ct = default, bool overwrite = false)
    {
        string q = $"/upload?offer={token}&name={Uri.EscapeDataString(file.Name)}&path={Uri.EscapeDataString(file.RelDir.Replace('\\', '/'))}"
            + (overwrite ? "&overwrite=1" : "");
        var req = Req(HttpMethod.Post, peer, q);
        await using var fs = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, Chunk, true);
        req.Content = new ProgressContent(fs, file.Size, progress);
        using var r = await Http.SendAsync(req, ct);
        r.EnsureSuccessStatusCode();
    }

    private sealed class ProgressContent : HttpContent
    {
        private readonly Stream _src; private readonly long _len; private readonly Action<long> _progress;
        public ProgressContent(Stream src, long len, Action<long> progress)
        {
            _src = src; _len = len; _progress = progress;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buf = new byte[Chunk]; long sent = 0; var last = DateTime.UtcNow; int n;
            while ((n = await _src.ReadAsync(buf)) > 0)
            {
                await stream.WriteAsync(buf.AsMemory(0, n));
                sent += n;
                if ((DateTime.UtcNow - last).TotalMilliseconds > 200) { _progress(sent); last = DateTime.UtcNow; }
            }
            _progress(sent);
        }
        protected override bool TryComputeLength(out long length) { length = _len; return true; }
    }
}
