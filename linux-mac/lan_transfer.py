#!/usr/bin/env python3
"""
LAN Transfer para Linux e Mac (Python 3.8+, sem instalar nada).

Fala o mesmo protocolo dos apps do Windows e do Android:
  - aparece para os outros aparelhos e encontra os outros na rede (UDP 48555);
  - disponibiliza os arquivos da pasta ~/LAN Transfer/Compartilhar (e os passados na linha de comando);
  - baixa arquivos dos outros aparelhos, recebe envios (com aceitar/recusar), textos e pacotes.

Uso:   python3 lan_transfer.py [arquivos ou pastas para disponibilizar...]
A tela do app abre no navegador em http://127.0.0.1:<porta>/app
"""
import hashlib, html, json, mimetypes, os, socket, sys, tarfile, threading, time, urllib.parse, urllib.request, uuid, webbrowser, zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

DISCOVERY_PORT = 48555
HOME = Path.home()
SHARE_DIR = HOME / "LAN Transfer" / "Compartilhar"
RECV_DIR = HOME / "Downloads" / "LAN Transfer"
CONF = HOME / ".config" / "lantransfer.json"
NEIGHBOURS = ["192.168.0", "192.168.1", "192.168.2", "192.168.3", "192.168.15", "192.168.100", "10.0.0", "10.0.1"]
CHUNK = 1024 * 1024


def load_conf():
    try:
        return json.loads(CONF.read_text())
    except Exception:
        c = {"id": uuid.uuid4().hex[:12], "name": socket.gethostname().split(".")[0], "requirePin": False,
             "pin": "%06d" % (int.from_bytes(os.urandom(4), "big") % 1000000), "peerPins": {}, "trusted": [], "autoAccept": False}
        save_conf(c)
        return c


def save_conf(c):
    try:
        CONF.parent.mkdir(parents=True, exist_ok=True)
        CONF.write_text(json.dumps(c, indent=2))
    except Exception:
        pass


conf = load_conf()
lock = threading.RLock()
extra_shared = []          # caminhos passados na linha de comando
peers = {}                 # id -> dict(peer, seen, files, needPin, blocked)
transfers = []             # lista de dicts para a tela
offers = {}                # id -> dict(offer, event, answer)
tokens = {}                # token -> (offer, expira)
texts = []                 # textos recebidos
PORT = 0


def safe_name(n):
    n = n.replace("\\", "/").split("/")[-1].strip().strip(".")
    for c in '<>:"|?*\0':
        n = n.replace(c, "_")
    return n or "arquivo"


def safe_rel(r):
    parts = [p.strip() for p in r.replace("\\", "/").split("/")]
    return "/".join(safe_name(p) for p in parts if p and p not in (".", ".."))


def file_id(path):
    return hashlib.sha1(str(path).encode()).hexdigest()[:12]


def shared_files():
    """[(id, Path, relDir)] — pasta Compartilhar (com subpastas) + itens da linha de comando."""
    out = []
    SHARE_DIR.mkdir(parents=True, exist_ok=True)
    roots = [(SHARE_DIR, False)] + [(Path(p), True) for p in extra_shared]
    for root, keep_name in roots:
        if root.is_file():
            out.append((file_id(root), root, ""))
        elif root.is_dir():
            for dirpath, _, names in os.walk(root):
                for n in names:
                    if n.startswith("."):
                        continue
                    p = Path(dirpath) / n
                    rel = os.path.relpath(dirpath, root.parent if keep_name else root)
                    out.append((file_id(p), p, "" if rel == "." else rel.replace(os.sep, "/")))
    return out


def unique(folder, name):
    p = folder / name
    stem, ext = os.path.splitext(name)
    i = 1
    while p.exists():
        p = folder / f"{stem} ({i}){ext}"
        i += 1
    return p


def add_transfer(**kw):
    t = dict(id=uuid.uuid4().hex, progress=0, status="Na fila")
    t.update(kw)
    with lock:
        transfers.insert(0, t)
        del transfers[200:]
    return t


# ------------------------------------------------------------------ descoberta
def local_ips():
    ips = []
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("10.255.255.255", 1))
        ips.append(s.getsockname()[0])
        s.close()
    except Exception:
        pass
    return ips or ["127.0.0.1"]


def discovery_responder():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    if hasattr(socket, "SO_REUSEPORT"):
        try:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)
        except OSError:
            pass
    s.bind(("", DISCOVERY_PORT))
    while True:
        try:
            data, addr = s.recvfrom(512)
            if data.decode(errors="ignore").strip() == "WIFITRANSFER_DISCOVER":
                kind = "mac" if sys.platform == "darwin" else "pc"
                s.sendto(f"WIFITRANSFER_PEER|{conf['id']}|{conf['name']}|{PORT}|{kind}|0|".encode(), addr)
        except Exception:
            time.sleep(0.2)


def discover(wide=False):
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    s.settimeout(0.3)
    msg = b"WIFITRANSFER_DISCOVER"
    targets = ["255.255.255.255"]
    own = [ip.rsplit(".", 1)[0] for ip in local_ips()]
    targets += [p + ".255" for p in own]
    if wide:
        targets += [f"{p}.{h}" for p in NEIGHBOURS if p not in own for h in range(1, 255)]
    found = {}
    end = time.time() + (3 if wide else 1.5)
    for _ in range(2):
        for t in targets:
            try:
                s.sendto(msg, (t, DISCOVERY_PORT))
            except OSError:
                pass
    while time.time() < end:
        try:
            data, addr = s.recvfrom(512)
        except socket.timeout:
            continue
        except OSError:
            break
        p = data.decode(errors="ignore").split("|")
        if len(p) >= 5 and p[0] == "WIFITRANSFER_PEER" and p[1] != conf["id"]:
            try:
                found[p[1]] = dict(id=p[1], name=p[2], host=addr[0], port=int(p[3]), kind=p[4])
            except ValueError:
                pass
    s.close()
    return found


def req(peer, path, method="GET", body=None, headers=None, timeout=10):
    h = {"X-Device": conf["id"], "X-Device-Name": urllib.parse.quote(conf["name"])}
    pin = conf.get("peerPins", {}).get(peer["id"])
    if pin:
        h["X-Pin"] = pin
    h.update(headers or {})
    r = urllib.request.Request(f"http://{peer['host']}:{peer['port']}{path}", data=body, method=method, headers=h)
    return urllib.request.urlopen(r, timeout=timeout)


def refresh_loop():
    n = 0
    while True:
        try:
            found = discover(wide=(n % 5 == 0))
            now = time.time()
            with lock:
                for pid, p in found.items():
                    e = peers.setdefault(pid, dict(files=[], needPin=False, blocked=False))
                    e.update(peer=p, seen=now)
                for pid in [k for k, v in peers.items() if now - v["seen"] > 40]:
                    del peers[pid]
                current = list(peers.values())
            for e in current:
                try:
                    with req(e["peer"], "/api/files", timeout=5) as r:
                        e["files"] = json.loads(r.read())
                    e["needPin"] = e["blocked"] = False
                except urllib.error.HTTPError as err:
                    e["files"] = []
                    e["needPin"] = err.code == 401
                    e["blocked"] = err.code == 403
                except Exception:
                    pass
        except Exception:
            pass
        n += 1
        time.sleep(5)


# ------------------------------------------------------------------ baixar / enviar
def download(peer, f):
    t = add_transfer(kind="down", name=f["name"], peer=peer["name"], total=f["size"])

    def run():
        folder = RECV_DIR / safe_rel(f.get("path", ""))
        folder.mkdir(parents=True, exist_ok=True)
        part = folder / (".%s.part" % hashlib.sha1(f"{peer['id']}|{f.get('path','')}|{f['name']}|{f['size']}".encode()).hexdigest()[:12])
        tries = 0
        while True:
            have = part.stat().st_size if part.exists() else 0
            if have >= f["size"]:
                break
            try:
                hdr = {"Range": f"bytes={have}-"} if have else {}
                with req(peer, f"/files/{f['id']}", headers=hdr, timeout=30) as r, open(part, "ab" if have and r.status == 206 else "wb") as out:
                    got = have if r.status == 206 else 0
                    t["status"] = "Baixando"
                    while True:
                        b = r.read(CHUNK)
                        if not b:
                            break
                        out.write(b)
                        got += len(b)
                        t["progress"] = got / max(1, f["size"])
                if got < f["size"]:
                    raise IOError("interrompido")
            except Exception as e:
                tries += 1
                if tries > 20:
                    t["status"] = f"Falhou: {e}"
                    return
                time.sleep(min(10, tries * 2))
        dest = unique(folder, safe_name(f["name"]))
        part.rename(dest)
        t.update(status="Salvo em " + str(dest), progress=1, path=str(dest))

    threading.Thread(target=run, daemon=True).start()


def send(peer, items, tx):
    """items: [(nome, tamanho, relDir, leitor)] — leitor(saida) escreve o conteúdo."""
    body = json.dumps({"from": conf["name"], "device": conf["id"],
                       "files": [{"name": n, "size": s, "path": r} for n, s, r, _ in items]}).encode()
    tx["status"] = "Aguardando aceitar…"
    try:
        with req(peer, "/api/offer", "POST", body, {"Content-Type": "application/json"}, timeout=100) as r:
            token = json.loads(r.read())["token"]
    except urllib.error.HTTPError:
        tx["status"] = "Recusado"
        return None
    return token


# ------------------------------------------------------------------ servidor HTTP
PAGE = """<!doctype html><html lang=pt-BR><head><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
<title>LAN Transfer</title><style>
:root{--bg:#f4f5f7;--card:#fff;--fg:#1d2330;--mut:#6b7280;--acc:#2563eb;--bd:#e3e6eb}
@media (prefers-color-scheme:dark){:root{--bg:#0f1115;--card:#181b22;--fg:#e8eaee;--mut:#9aa1ad;--acc:#60a5fa;--bd:#2a2f3a}}
body{margin:0;font:15px/1.45 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;background:var(--bg);color:var(--fg)}
main{max-width:1000px;margin:0 auto;padding:16px}h1{font-size:1.3rem}h2{font-size:1.05rem;margin:20px 0 8px}
.card{background:var(--card);border:1px solid var(--bd);border-radius:14px;padding:6px 14px;margin-bottom:12px}
.row{display:flex;gap:10px;align-items:center;padding:8px 0;border-top:1px solid var(--bd)}.row:first-child{border-top:0}
.n{flex:1;min-width:0;word-break:break-all}.m{color:var(--mut);font-size:13px}
button,label.b{background:var(--acc);color:#fff;border:0;border-radius:10px;padding:8px 12px;cursor:pointer;font-size:14px}
button.s{background:transparent;color:var(--acc);border:1px solid var(--bd)}input[type=file]{display:none}
.bar{height:5px;background:var(--bd);border-radius:3px;overflow:hidden;margin-top:4px}.bar i{display:block;height:100%;background:var(--acc)}
</style></head><body><main>
<h1>📶 LAN Transfer — {{NAME}}</h1>
<p class=m>Seus arquivos disponíveis ficam em <b>{{SHARE}}</b> (coloque arquivos ou pastas lá). Recebidos e baixados vão para <b>{{RECV}}</b>.</p>
<div id=offers></div>
<h2>Na rede</h2><div id=peers><div class=card><div class=row>Procurando aparelhos…</div></div></div>
<h2>Meus arquivos</h2><div class=card id=mine></div>
<h2>Transferências e textos</h2><div class=card id=tx></div>
</main><script>
const $=s=>document.querySelector(s),esc=s=>String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;');
const fmt=n=>n<1024?n+' B':n<1048576?(n/1024).toFixed(1)+' KB':n<1073741824?(n/1048576).toFixed(1)+' MB':(n/1073741824).toFixed(2)+' GB';
let st={};
async function tick(){try{st=await (await fetch('/local/state')).json();}catch(e){return;}draw();}
function draw(){
 $('#offers').innerHTML=st.offers.map(o=>`<div class=card><div class=row><div class=n><b>${esc(o.from)}</b> quer enviar ${o.files.length} arquivo(s) · ${fmt(o.total)}<div class=m>${o.files.slice(0,8).map(f=>esc(f.name)).join(', ')}</div></div><button class=s onclick="ans('${o.id}',0)">Recusar</button><button onclick="ans('${o.id}',1)">Aceitar</button></div></div>`).join('');
 $('#peers').innerHTML=st.peers.length?st.peers.map(p=>`<div class=card><div class=row><div class=n><b>${esc(p.peer.name)}</b><div class=m>${p.needPin?'Protegido por PIN':p.blocked?'Só compartilha com aparelhos de confiança':p.files.length+' arquivo(s)'} · ${p.peer.host}</div></div>
  ${p.needPin?`<button onclick="pin('${p.peer.id}')">Digitar PIN</button>`:''}<button class=s onclick="txt('${p.peer.id}')">Enviar texto</button><label class=b>Enviar arquivos<input type=file multiple onchange="up('${p.peer.id}',this)"></label></div>
  ${p.files.map(f=>`<div class=row><div class=n>${esc(f.name)}<div class=m>${fmt(f.size)}${f.path?' · '+esc(f.path):''}</div></div><button class=s onclick="dl('${p.peer.id}','${f.id}')">Baixar</button></div>`).join('')}</div>`).join(''):'<div class=card><div class=row>Procurando aparelhos… Abra o LAN Transfer no celular ou em outro PC.</div></div>';
 $('#mine').innerHTML=st.mine.length?st.mine.map(f=>`<div class=row><div class=n>${esc(f.name)}<div class=m>${fmt(f.size)}${f.path?' · '+esc(f.path):''}</div></div></div>`).join(''):'<div class=row class=m>Nada disponível. Coloque arquivos em {{SHARE}}.</div>';
 $('#tx').innerHTML=st.transfers.length?st.transfers.map(t=>`<div class=row><div class=n>${esc(t.name)}<div class=m>${esc(t.status)} · ${esc(t.peer||'')}</div>${t.progress>0&&t.progress<1?`<div class=bar><i style="width:${t.progress*100}%"></i></div>`:''}</div>${t.text?`<button class=s onclick="navigator.clipboard.writeText(${esc(JSON.stringify(t.text))})">Copiar</button>`:''}</div>`).join(''):'<div class=row>Nada ainda.</div>';
}
const post=(u,b)=>fetch(u,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(b)});
function ans(id,a){post('/local/answer',{id,accept:!!a}).then(tick);}
function dl(p,f){post('/local/download',{peer:p,file:f}).then(tick);}
function pin(p){const v=prompt('PIN do outro aparelho');if(v)post('/local/pin',{peer:p,pin:v}).then(tick);}
function txt(p){const v=prompt('Texto para enviar');if(v)post('/local/text',{peer:p,text:v}).then(tick);}
async function up(p,inp){const files=[...inp.files];inp.value='';if(!files.length)return;
 const r=await post('/local/offer',{peer:p,files:files.map(f=>({name:f.name,size:f.size}))});if(!r.ok){alert('Recusado ou sem resposta');return;}
 const {token,tx}=await r.json();
 for(const f of files){await fetch(`/local/upload?peer=${p}&token=${token}&tx=${tx}&name=${encodeURIComponent(f.name)}`,{method:'POST',body:f});}
 tick();}
tick();setInterval(tick,1500);
</script></body></html>"""


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "LANTransfer/5"

    def log_message(self, *a):
        pass

    # ---- utilidades
    def q(self):
        return urllib.parse.parse_qs(urllib.parse.urlsplit(self.path).query)

    def json(self, code, obj):
        b = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""

    def stream(self):
        """Corpo da requisição, inclusive chunked."""
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            return ChunkedReader(self.rfile)
        return LimitedReader(self.rfile, int(self.headers.get("Content-Length") or 0))

    def local(self):
        return self.client_address[0] in ("127.0.0.1", "::1")

    def who(self):
        n = self.headers.get("X-Device-Name")
        return urllib.parse.unquote(n) if n else f"Navegador ({self.client_address[0]})"

    def check(self):
        if self.local():
            return None
        dev = self.headers.get("X-Device") or (self.q().get("d") or [None])[0]
        if conf.get("trustedOnly") and dev not in conf.get("trusted", []):
            return 403
        if conf.get("requirePin"):
            pin = self.headers.get("X-Pin") or (self.q().get("t") or [None])[0]
            ck = self.headers.get("Cookie") or ""
            if not pin and "lt=" in ck:
                pin = ck.split("lt=")[1].split(";")[0]
            if pin != conf["pin"]:
                return 401
        return None

    def deny(self, code):
        self.json(code, {"needPin": code == 401, "blocked": code == 403})

    # ---- GET
    def do_GET(self):
        path = urllib.parse.unquote(urllib.parse.urlsplit(self.path).path)
        if path == "/api/ping":
            return self.json(200, {"app": "WiFiTransfer", "id": conf["id"], "name": conf["name"], "kind": "pc", "version": 5})
        if path == "/app" and self.local():
            page = PAGE.replace("{{NAME}}", html.escape(conf["name"])).replace("{{SHARE}}", html.escape(str(SHARE_DIR))).replace("{{RECV}}", html.escape(str(RECV_DIR)))
            b = page.encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(b)))
            self.end_headers()
            return self.wfile.write(b)
        if path == "/local/state" and self.local():
            with lock:
                return self.json(200, {
                    "peers": [{"peer": e["peer"], "files": e["files"], "needPin": e["needPin"], "blocked": e["blocked"]} for e in peers.values()],
                    "mine": [{"name": p.name, "size": p.stat().st_size, "path": r} for _, p, r in shared_files()][:500],
                    "transfers": transfers[:100],
                    "offers": [{"id": k, "from": v["offer"]["from"], "files": v["offer"]["files"], "total": sum(f["size"] for f in v["offer"]["files"])}
                               for k, v in offers.items() if v["answer"] is None],
                })
        if path == "/":
            b = ("<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width'><title>LAN Transfer</title>"
                 f"<body style='font-family:system-ui;padding:16px'><h2>📶 {html.escape(conf['name'])}</h2>"
                 + "".join(f"<p><a href='/files/{i}'>{html.escape((r + '/' if r else '') + p.name)}</a></p>" for i, p, r in shared_files())).encode()
            if self.check():
                return self.deny(self.check())
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(b)))
            self.end_headers()
            return self.wfile.write(b)
        if path == "/api/files":
            c = self.check()
            if c:
                return self.deny(c)
            return self.json(200, [{"id": i, "name": p.name, "size": p.stat().st_size, "path": r, "thumb": False} for i, p, r in shared_files()])
        if path.startswith("/files/"):
            c = self.check()
            if c:
                return self.deny(c)
            item = next(((i, p) for i, p, _ in shared_files() if i == path[7:]), None)
            if not item:
                return self.json(404, {})
            return self.send_file(item[1], "inline" in self.q())
        if path == "/api/zip":
            c = self.check()
            if c:
                return self.deny(c)
            ids = set(self.q().get("id", []))
            items = [(p, r) for i, p, r in shared_files() if not ids or i in ids]
            self.send_response(200)
            self.send_header("Content-Type", "application/zip")
            self.send_header("Content-Disposition", 'attachment; filename="arquivos.zip"')
            self.send_header("Connection", "close")
            self.end_headers()
            with zipfile.ZipFile(Unseekable(self.wfile), "w", zipfile.ZIP_STORED) as z:
                for p, r in items:
                    z.write(p, (r + "/" if r else "") + p.name)
            self.close_connection = True
            return
        self.json(404, {})

    def send_file(self, p, inline):
        total = p.stat().st_size
        start, end = 0, total - 1
        rng = self.headers.get("Range")
        partial = bool(rng and rng.startswith("bytes="))
        if partial:
            a, _, b = rng[6:].partition("-")
            start = int(a or 0)
            if b:
                end = min(int(b), total - 1)
        self.send_response(206 if partial else 200)
        self.send_header("Content-Type", (mimetypes.guess_type(p.name)[0] or "application/octet-stream") if inline else "application/octet-stream")
        self.send_header("Content-Length", str(max(0, end - start + 1)))
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Disposition", ("inline" if inline else "attachment") + "; filename*=UTF-8''" + urllib.parse.quote(p.name))
        if partial:
            self.send_header("Content-Range", f"bytes {start}-{end}/{total}")
        self.end_headers()
        with open(p, "rb") as f:
            f.seek(start)
            left = end - start + 1
            while left > 0:
                b = f.read(min(CHUNK, left))
                if not b:
                    break
                self.wfile.write(b)
                left -= len(b)

    # ---- POST
    def do_POST(self):
        path = urllib.parse.urlsplit(self.path).path
        q = self.q()
        if path == "/api/offer":
            dev = self.headers.get("X-Device")
            if conf.get("trustedOnly") and dev and dev not in conf.get("trusted", []):
                return self.deny(403)
            o = json.loads(self.body() or b"{}")
            offer = {"from": o.get("from") or self.who(), "device": dev,
                     "files": [{"name": safe_name(f.get("name", "arquivo")), "size": int(f.get("size", 0)), "path": safe_rel(f.get("path", ""))} for f in o.get("files", [])]}
            if conf.get("autoAccept") and dev in conf.get("trusted", []):
                ok = True
            else:
                oid = uuid.uuid4().hex
                ev = threading.Event()
                offers[oid] = {"offer": offer, "event": ev, "answer": None}
                notify(f"{offer['from']} quer enviar {len(offer['files'])} arquivo(s)", "Abra o LAN Transfer para aceitar")
                ev.wait(85)
                ok = bool(offers.pop(oid, {}).get("answer"))
            if not ok:
                return self.json(403, {"error": "Recusado"})
            token = uuid.uuid4().hex
            tokens[token] = (offer, time.time() + 7200)
            return self.json(200, {"token": token})
        if path == "/upload":
            token = self.headers.get("X-Offer") or (q.get("offer") or [""])[0]
            offer = tokens.get(token)
            if not offer or offer[1] < time.time():
                return self.json(403, {})
            overwrite = "overwrite" in q
            src = self.stream()
            if "tar" in q:
                t = add_transfer(kind="in", name=f"{len(offer[0]['files'])} arquivo(s) (pacote)", peer=offer[0]["from"], status="Recebendo")
                with tarfile.open(fileobj=src, mode="r|") as tf:
                    for m in tf:
                        if not m.isfile():
                            continue
                        rel = safe_rel(os.path.dirname(m.name))
                        folder = RECV_DIR / rel
                        folder.mkdir(parents=True, exist_ok=True)
                        name = safe_name(os.path.basename(m.name))
                        dest = folder / name if overwrite else unique(folder, name)
                        with tf.extractfile(m) as fsrc, open(dest, "wb") as out:
                            while True:
                                b = fsrc.read(CHUNK)
                                if not b:
                                    break
                                out.write(b)
                t.update(status="Recebido", progress=1)
                return self.json(200, {"ok": True})
            name = safe_name((q.get("name") or ["arquivo"])[0])
            folder = RECV_DIR / safe_rel((q.get("path") or [""])[0])
            folder.mkdir(parents=True, exist_ok=True)
            dest = folder / name if overwrite else unique(folder, name)
            total = int(self.headers.get("Content-Length") or 0)
            t = add_transfer(kind="in", name=name, peer=offer[0]["from"], status="Recebendo", total=total)
            got = 0
            tmp = dest.with_name(dest.name + ".part")
            with open(tmp, "wb") as out:
                while True:
                    b = src.read(CHUNK)
                    if not b:
                        break
                    out.write(b)
                    got += len(b)
                    t["progress"] = got / max(1, total)
            if total and got != total:
                tmp.unlink(missing_ok=True)
                t["status"] = "Falhou"
                return self.json(500, {})
            os.replace(tmp, dest)
            t.update(status="Recebido · " + str(dest), progress=1, path=str(dest))
            return self.json(200, {"ok": True})
        if path == "/api/text":
            dev = self.headers.get("X-Device")
            if dev:
                c = self.check()
                if c:
                    return self.deny(c)
            o = json.loads(self.body() or b"{}")
            text = o.get("text", "")
            if text:
                frm = o.get("from") or self.who()
                add_transfer(kind="text", name=text[:80], peer=frm, status="Texto recebido", text=text, progress=1)
                notify(f"Texto de {frm}", text[:120])
            return self.json(200, {"ok": True})

        # ---- tela local
        if not self.local():
            return self.json(404, {})
        if path == "/local/answer":
            o = json.loads(self.body())
            v = offers.get(o["id"])
            if v:
                v["answer"] = bool(o.get("accept"))
                v["event"].set()
            return self.json(200, {})
        if path == "/local/pin":
            o = json.loads(self.body())
            conf.setdefault("peerPins", {})[o["peer"]] = o["pin"]
            save_conf(conf)
            return self.json(200, {})
        return self.local_actions(path, q)

    def local_actions(self, path, q):
        if path in ("/local/download", "/local/text", "/local/offer"):
            o = json.loads(self.body())
            e = peers.get(o["peer"])
            if not e:
                return self.json(404, {})
            peer = e["peer"]
            if path == "/local/download":
                f = next((f for f in e["files"] if f["id"] == o["file"]), None)
                if f:
                    download(peer, f)
                return self.json(200, {})
            if path == "/local/text":
                try:
                    with req(peer, "/api/text", "POST", json.dumps({"from": conf["name"], "text": o["text"]}).encode(), {"Content-Type": "application/json"}):
                        pass
                    add_transfer(kind="text", name=o["text"][:80], peer=peer["name"], status="Texto enviado", progress=1)
                except Exception as ex:
                    add_transfer(kind="text", name=o["text"][:80], peer=peer["name"], status=f"Falhou: {ex}")
                return self.json(200, {})
            tx = add_transfer(kind="up", name=f"{len(o['files'])} arquivo(s) para {peer['name']}", peer=peer["name"])
            token = send(peer, [(f["name"], f["size"], "", None) for f in o["files"]], tx)
            if not token:
                return self.json(403, {})
            tx["status"] = "Enviando"
            return self.json(200, {"token": token, "tx": tx["id"]})
        if path == "/local/upload":
            e = peers.get((q.get("peer") or [""])[0])
            if not e:
                return self.json(404, {})
            name = (q.get("name") or ["arquivo"])[0]
            n = int(self.headers.get("Content-Length") or 0)
            tx = next((t for t in transfers if t["id"] == (q.get("tx") or [""])[0]), None)
            r = urllib.request.Request(
                f"http://{e['peer']['host']}:{e['peer']['port']}/upload?offer={q['token'][0]}&name={urllib.parse.quote(name)}",
                data=LimitedReader(self.rfile, n), method="POST",
                headers={"Content-Length": str(n), "X-Device": conf["id"], "Content-Type": "application/octet-stream"})
            try:
                urllib.request.urlopen(r, timeout=600).read()
                if tx:
                    tx.update(status="Enviado", progress=1)
                return self.json(200, {})
            except Exception as ex:
                if tx:
                    tx["status"] = f"Falhou: {ex}"
                return self.json(500, {})
        return self.json(404, {})


class LimitedReader:
    def __init__(self, f, n):
        self.f, self.left = f, n

    def read(self, n=-1):
        if self.left <= 0:
            return b""
        n = self.left if n is None or n < 0 else min(n, self.left)
        b = self.f.read(n)
        self.left -= len(b)
        return b


class ChunkedReader:
    def __init__(self, f):
        self.f, self.left, self.done, self.buf = f, 0, False, b""

    def read(self, n=-1):
        out = b""
        while (n < 0 or len(out) < n) and not self.done:
            if self.left == 0:
                line = self.f.readline().strip()
                size = int(line.split(b";")[0] or b"0", 16)
                if size == 0:
                    while self.f.readline().strip():
                        pass
                    self.done = True
                    break
                self.left = size
            want = self.left if n < 0 else min(self.left, n - len(out))
            b = self.f.read(want)
            if not b:
                self.done = True
                break
            out += b
            self.left -= len(b)
            if self.left == 0:
                self.f.readline()
        return out


class Unseekable:
    """Permite gravar zip direto na resposta (sem seek)."""
    def __init__(self, f):
        self.f, self.pos = f, 0

    def write(self, b):
        self.f.write(b)
        self.pos += len(b)
        return len(b)

    def tell(self):
        return self.pos

    def flush(self):
        self.f.flush()


def notify(title, text):
    try:
        if sys.platform == "darwin":
            os.system("osascript -e %s >/dev/null 2>&1" % json.dumps(f'display notification {json.dumps(text)} with title {json.dumps(title)}'))
        else:
            os.system("notify-send %s %s >/dev/null 2>&1" % (json.dumps(title), json.dumps(text)))
    except Exception:
        pass


def main():
    global PORT
    extra_shared.extend(os.path.abspath(a) for a in sys.argv[1:] if not a.startswith("-"))
    RECV_DIR.mkdir(parents=True, exist_ok=True)
    SHARE_DIR.mkdir(parents=True, exist_ok=True)
    srv = None
    for port in range(8000, 8020):
        try:
            srv = ThreadingHTTPServer(("", port), Handler)
            PORT = port
            break
        except OSError:
            continue
    if not srv:
        sys.exit("Nenhuma porta livre entre 8000 e 8019")
    srv.daemon_threads = True
    threading.Thread(target=discovery_responder, daemon=True).start()
    threading.Thread(target=refresh_loop, daemon=True).start()
    url = f"http://127.0.0.1:{PORT}/app"
    print(f"LAN Transfer rodando. Abra {url}")
    print(f"Outros aparelhos na rede (navegador): http://{local_ips()[0]}:{PORT}/")
    print("Para sair: Ctrl+C")
    if "--no-browser" not in sys.argv:
        threading.Timer(1, lambda: webbrowser.open(url)).start()
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
