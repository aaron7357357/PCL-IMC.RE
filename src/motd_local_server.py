# -*- coding: utf-8 -*-
"""
PCL2 自定义主页 · 本地实时服务（无需远程部署）
横幅图 + 实时服务器状态 + 在线玩家列表 + 离线/正版进服 + QQ 群

用法（Windows）：
  1. 双击运行本文件（需要电脑装有 Python 3）；
  2. PCL2 中：设置 → 个性化 → 主页 → 选择「联网更新」，地址填：
         http://127.0.0.1:8848/motd.xaml
  3. 保持本窗口开着；在 PCL2 里点「刷新主页」即可看到实时状态。
     按 Ctrl+C 停止服务。

原理：
  - 直连服务器本体拉取状态（Minecraft 服务器列表协议，自动解析 SRV/A 记录，
    不经过第三方 API，能测延迟）；
  - 直连失败时自动并行回退 mcstatus.io / mcsrvstat.us 公共接口；
  - 每次请求由脚本自动生成完整主页，无需维护本地 Custom.xaml。
"""

import gzip
import json
import os
import random
import re
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# ================= 配置 =================
SERVER = "off.imc.cab"     # 离线（普通账号）进服地址，同时用于查询服务器状态
PREMIUM_SERVER = "on.imc.cab"  # 正版账号进服地址（和 SERVER 是同一台机器，只是验证方式不同）
STATUS_HOST = "play.imc.re"  # 状态卡标题显示的地址（总入口）
STATUS_QUERY = "play.imc.re"  # 状态数据从哪个地址取：跟标题一致走总入口；留空则用 SERVER（off.imc.cab）
SERVERS_DAT_SYNC = True     # 自动把几个 imc 地址写进游戏的「多人游戏」列表（servers.dat，含版本隔离的各版本目录）
MC_VERSION_MIN = "1.8"       # 主页只列出这个范围内的版本（按版本 JSON 的 clientVersion 判断）
MC_VERSION_MAX = "1.21.11"
WEBSITE = "https://imc.re"  # 服务器官网（点「进入服务器官网」按钮时在浏览器打开）
QQ_GROUPS = [               # QQ 群列表：(群号, 简介)
    ("607355046", "imc公告群"),
    ("1108248358", "imc聊天群"),
]
BANNER = "logo.png"        # 顶部横幅图片：填与脚本/exe 同目录的文件名（留空或文件不存在则不显示）
ZAN_IMAGE = "zan.jpg"
AUTHOR_QQ = "177436529"   # 创作者 QQ，用于「联系创作者」卡      # 赞赏码图片：同目录有这张图才显示「赞赏制作者」卡（默认折叠）
BANNER_HEIGHT = 140        # 横幅显示高度，宽度按图片比例自动换算
HTTP_PORT = 8848             # 本地服务端口
REFRESH_INTERVAL = 1         # 后台自动刷新秒数（1 = 每秒更新在线人数/延迟）
CACHE_TTL = 120              # 请求侧兜底缓存秒数（后台刷新正常时几乎不会用到）
LAUNCH_VERSION = "1.21.11"          # 「离线进服」默认版本：填版本名则固定；留空 = 自动用本机最高的 MC 版本
VERSIONS_SCAN = True                 # 自动扫描本机已装版本，在卡片里按版本列出「离线 / 正版」按钮
PCL_DIR = ""                         # PCL 所在目录；留空自动找（优先从正在运行的 PCL 进程取路径）
VERSIONS_DIR = ""                    # 直接指定 versions 目录；留空按 PCL 保存的设置推算
GLASS_CARD = "#BFFFFFFF"             # 卡片玻璃底色（顶部，ARGB，前两位越小越透）
GLASS_CARD2 = "#94FFFFFF"            # 卡片玻璃底色（底部，做一点从亮到透的渐变）
GLASS_BORDER = "#66FFFFFF"           # 卡片描边颜色（模拟玻璃高光边）
GLASS_CORNER = 12                    # 卡片圆角半径
GLASS_TEXT = "#FF233347"             # 卡片里的正文颜色（浅色玻璃上要用深色字）
STYLE_FILE = "style.txt"              # 样式开关文件：写 glass（毛玻璃）或 classic（PCL 原生白卡片），改完约 1 秒生效
# =========================================

_status_cache = {"time": 0, "data": None}


# ---------------- 后台自动刷新（每秒更新状态缓存） ----------------

def _background_refresh():
    """后台线程：持续拉取服务器状态并更新缓存，让主页数据始终接近实时"""
    last_servers = 0.0
    last_tcwatch = 0.0
    while True:
        try:
            st = fetch_status(STATUS_QUERY or SERVER)
            _status_cache["time"] = time.time()
            _status_cache["data"] = st
        except Exception:
            pass
        if time.time() - last_tcwatch > 4:           # 陶瓦自动停止兜底
            last_tcwatch = time.time()
            try:
                _tc_autostop_watch()
            except Exception:
                pass
            try:
                _tc_autofill_watch()
            except Exception:
                pass
        if time.time() - last_servers > 30:          # 顺手把 imc 补进多人游戏列表
            last_servers = time.time()
            try:
                _servers_dat_sync()
            except Exception:
                pass
        time.sleep(REFRESH_INTERVAL)


# ---------------- DNS（自建 UDP 查询，超时可控） ----------------

def _encode_dns_name(name):
    return b"".join(bytes([len(x)]) + x.encode("latin-1") for x in name.split(".")) + b"\x00"


def _parse_dns_name(buf, pos):
    labels = []
    while True:
        if pos >= len(buf):
            raise ValueError("DNS 数据越界")
        n = buf[pos]
        if n & 0xC0 == 0xC0:                          # 压缩指针
            ptr = ((n & 0x3F) << 8) | buf[pos + 1]
            labels.append(_parse_dns_name(buf, ptr)[0])
            return ".".join(labels), pos + 2
        pos += 1
        if n == 0:
            return ".".join(labels), pos
        labels.append(buf[pos:pos + n].decode("latin-1"))
        pos += n


def _dns_query(domain, qtype, timeout=2):
    """查询一条 DNS 记录，返回 rdata 原始字节列表；失败返回 None"""
    qname = _encode_dns_name(domain)
    tid = random.randint(0, 0xFFFF)
    query = struct.pack(">HHHHHH", tid, 0x0100, 1, 0, 0, 0) + qname + struct.pack(">HH", qtype, 1)
    for resolver in ("8.8.8.8", "223.5.5.5"):
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.settimeout(timeout)
            s.sendto(query, (resolver, 53))
            data, _ = s.recvfrom(4096)
            s.close()
        except OSError:
            continue
        try:
            if len(data) < 12 or struct.unpack(">H", data[0:2])[0] != tid:
                continue
            ancount = struct.unpack(">H", data[6:8])[0]
            pos = 12 + len(qname) + 4                  # 跳过 question 段
            answers = []
            for _ in range(ancount):
                _, pos = _parse_dns_name(data, pos)
                rtype, _rclass, _ttl, rdlen = struct.unpack(">HHIH", data[pos:pos + 10])
                pos += 10
                rdata = data[pos:pos + rdlen]
                pos += rdlen
                if rtype == qtype:
                    answers.append(rdata)
            if answers:
                return answers
        except (ValueError, struct.error):
            continue
    return None


def _resolve_srv(domain):
    """查询 _minecraft._tcp.<domain> 的 SRV，返回 (目标主机, 端口) 或 None"""
    answers = _dns_query("_minecraft._tcp." + domain, 33)
    if not answers:
        return None
    for rdata in answers:
        if len(rdata) < 7:
            continue
        _prio, _weight, port = struct.unpack(">HHH", rdata[0:6])
        try:
            target, _ = _parse_dns_name(rdata, 6)
        except ValueError:
            continue
        if target.endswith("."):
            target = target[:-1]
        return (target, port)
    return None


def _resolve_a(host):
    """解析主机名为点分 IP 字符串；失败返回 None。
    优先用系统 DNS（getaddrinfo，兼容各种网络环境），失败再走自建 UDP 查询兜底"""
    if re.match(r"^\d+\.\d+\.\d+\.\d+$", host):
        return host
    try:
        infos = socket.getaddrinfo(host, None, socket.AF_INET)
        if infos:
            return infos[0][4][0]
    except OSError:
        pass
    answers = _dns_query(host, 1)
    if not answers:
        return None
    for rdata in answers:
        if len(rdata) == 4:
            return ".".join(str(b) for b in rdata)
    return None


# ---------------- Minecraft 服务器列表协议（直连） ----------------

def write_varint(n):
    n &= 0xFFFFFFFF                       # 负数（如协议版本 -1）先归一为 32 位无符号，防止死循环
    out = b""
    while True:
        if n & ~0x7F == 0:
            out += bytes([n])
            return out
        out += bytes([(n & 0x7F) | 0x80])
        n >>= 7


def read_exact(sock, n):
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise ConnectionError("连接中断")
        buf += chunk
    return buf


def read_varint_sock(sock):
    num, shift = 0, 0
    while True:
        b = read_exact(sock, 1)[0]
        num |= (b & 0x7F) << shift
        if not (b & 0x80):
            return num
        shift += 7
        if shift > 35:
            raise ConnectionError("varint 过长")


def _parse_varint_buf(buf, pos):
    num, shift = 0, 0
    while True:
        b = buf[pos]
        pos += 1
        num |= (b & 0x7F) << shift
        if not (b & 0x80):
            return num, pos
        shift += 7


def _strip_color(s):
    return re.sub(r"\xa7[0-9a-fk-or]", "", s)


def _extract_text(node):
    """提取 1.19+ chat component 里的纯文本"""
    if isinstance(node, str):
        return node
    if isinstance(node, dict):
        return node.get("text", "") + _extract_text(node.get("extra", ""))
    if isinstance(node, list):
        return "".join(_extract_text(x) for x in node)
    return ""


def mc_ping(host, port, timeout=6):
    """直连服务器 Status Ping，返回 (json, 延迟毫秒)；失败抛异常"""
    t0 = time.time()
    sock = socket.create_connection((host, port), timeout=timeout)
    sock.settimeout(timeout)
    try:
        addr = host.encode("utf-8")
        payload = write_varint(0) + write_varint(-1) + \
            write_varint(len(addr)) + addr + struct.pack(">H", port) + write_varint(1)
        sock.sendall(write_varint(len(payload)) + payload)
        sock.sendall(b"\x01\x00")                    # Status Request

        length = read_varint_sock(sock)
        data = read_exact(sock, length)
        pos = 0
        _, pos = _parse_varint_buf(data, pos)        # packet id
        json_len, pos = _parse_varint_buf(data, pos)
        body = data[pos:pos + json_len]
        return json.loads(body.decode("utf-8", "replace")), int((time.time() - t0) * 1000)
    finally:
        sock.close()


# ---------------- 状态获取：直连与 API 并行抢答 ----------------

def _normalize_plist(raw):
    names = []
    for x in (raw or []):
        if isinstance(x, dict):
            names.append(x.get("name", ""))
        elif isinstance(x, str):
            names.append(x)
    return [n for n in names if n]


def _players_text(pl):
    """在线人数：服务器没给最大人数（-1 / 0）时只显示当前人数，避免出现“145 / -1”"""
    online = pl.get("online", 0)
    try:
        mx = int(pl.get("max") or 0)
    except (TypeError, ValueError):
        mx = 0
    return str(online) if mx <= 0 else "%s / %s" % (online, mx)


def _fetch_api(server):
    apis = [
        "https://api.mcstatus.io/v2/status/java/" + urllib.parse.quote(server, safe=""),
        "https://api.mcsrvstat.us/2/" + urllib.parse.quote(server, safe=""),
    ]
    for url in apis:
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
            data = json.load(urllib.request.urlopen(req, timeout=8))
        except Exception:
            continue
        if not data.get("online"):
            return {"online": False, "motd": "", "version": "-", "players": "0 / 0",
                    "plist": [], "latency": None, "source": "API"}
        motd = data["motd"].get("clean", "") if isinstance(data.get("motd"), dict) else ""
        if isinstance(motd, list):
            motd = "\n".join(motd)
        if isinstance(data.get("version"), dict):
            ver = data["version"].get("name_clean") or data["version"].get("name_raw") or "-"
        else:
            ver = data.get("version", "-")
        pl = data.get("players", {})
        players = _players_text(pl)
        return {"online": True, "motd": motd, "version": ver, "players": players,
                "plist": _normalize_plist(pl.get("list")), "latency": None, "source": "API"}
    return None


def _do_ping(ip, port):
    """直连并解析状态，成功返回 dict，失败抛异常"""
    data, latency = mc_ping(ip, port)
    online = bool(data.get("players") is not None or data.get("online"))
    motd = _strip_color(_extract_text(data.get("description", "")))
    ver = data.get("version", {}).get("name", "-") if isinstance(data.get("version"), dict) else "-"
    pl = data.get("players", {})
    players = _players_text(pl)
    return {"online": online, "motd": motd, "version": ver, "players": players,
            "plist": _normalize_plist(pl.get("list")), "latency": latency, "source": "直连"}


def _parallel(fn):
    """后台线程执行 fn，返回可轮询的结果箱（{"r": 结果} 或 {"e": 异常}）"""
    box = {}
    def run():
        try:
            box["r"] = fn()
        except Exception:
            box["e"] = True
    threading.Thread(target=run, daemon=True).start()
    return box


def fetch_status(server):
    """直连与 API 并行抢答，谁先成功用谁；总耗时不超过 9 秒。
    直连优先用解析出的 IP；即使自建 DNS 失败，也会直接用域名让 socket 走系统解析直连"""
    host, port = server, 25565
    if ":" in server:
        host, port = server.rsplit(":", 1)
        port = int(port)
    else:
        srv = _resolve_srv(server)
        if srv:
            host, port = srv
    ip = _resolve_a(host)

    boxes = []
    if ip:
        boxes.append(_parallel(lambda: _do_ping(ip, port)))
    if not ip or host != ip:
        boxes.append(_parallel(lambda: _do_ping(host, port)))   # 域名直连（socket 自带系统 DNS 解析）
    boxes.append(_parallel(lambda: _fetch_api(server)))

    deadline_s = 9
    t0 = time.time()
    while time.time() - t0 < deadline_s:
        for b in boxes:
            if b.get("r"):
                return b["r"]
        time.sleep(0.1)
    return {"online": False, "motd": "", "version": "-", "players": "0 / 0",
            "plist": [], "latency": None, "source": "-"}


# ---------------- XAML 生成 ----------------

def xaml_escape(s):
    s = s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    s = s.replace('"', "&quot;").replace("'", "&apos;")
    s = re.sub(r"\r\n|\r|\n", "&#xA;", s)
    return s


# ---------------- 顶部横幅图片（由本服务托管，PCL 直接从 127.0.0.1 取图） ----------------

_static_files = {}          # URL 路径 -> 本地文件绝对路径（只登记主页自己引用的图片，不开放任意路径）


def _app_dir():
    """脚本所在目录；打包成 exe 后为 exe 所在目录（logo 与 exe 放一起即可）"""
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))


def _png_size(path):
    """只读 PNG 文件头拿 (宽, 高)，不依赖第三方库；读不到就返回 None"""
    try:
        with open(path, "rb") as f:
            head = f.read(24)
        if len(head) < 24 or head[:8] != b"\x89PNG\r\n\x1a\n" or head[12:16] != b"IHDR":
            return None
        return struct.unpack(">II", head[16:24])
    except OSError:
        return None


def _banner_block():
    """生成顶部横幅 XAML；也可以直接在 BANNER 里填 http(s) 地址外链图片"""
    name = (BANNER or "").strip()
    if not name:
        return ""
    path = None
    if "://" in name:
        url = name
    else:
        path = name if os.path.isabs(name) else os.path.join(_app_dir(), name)
        if not os.path.isfile(path):
            return ""                    # 图片没放好就当没配置，主页照常刷新不报错
        fname = os.path.basename(path)
        key = "/banner/" + fname
        _static_files[key] = path
        _static_files[urllib.parse.quote(key)] = path
        url = "http://127.0.0.1:%d%s" % (HTTP_PORT, urllib.parse.quote(key))

    size_attr = 'Height="%d"' % BANNER_HEIGHT
    if path:
        wh = _png_size(path)
        if wh and wh[1]:
            size_attr = 'Width="%d" Height="%d"' % (
                int(round(wh[0] * BANNER_HEIGHT / wh[1])), BANNER_HEIGHT)
    return ('<local:MyImage ' + size_attr + ' HorizontalAlignment="Center" Margin="0,0,0,15"\n'
            '            Source="' + xaml_escape(url) + '" EnableCache="True" />\n\n')


# ---------------- 本机已装版本（用于「按版本进服」） ----------------

_ver_cache = {"pcl": None, "pcl_time": 0.0, "vers": None, "vers_time": 0.0}
_MC_VER_RE = re.compile(r"^(\d+)\.(\d+)(?:\.(\d+))?$")


def _process_paths():
    """列出所有进程的 exe 路径，只用来找正在运行的 PCL；失败返回空列表"""
    try:
        import ctypes
        from ctypes import wintypes

        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        psapi = ctypes.WinDLL("psapi", use_last_error=True)
        k32.OpenProcess.restype = wintypes.HANDLE
        k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        k32.QueryFullProcessImageNameW.argtypes = [
            wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        k32.CloseHandle.argtypes = [wintypes.HANDLE]
        psapi.EnumProcesses.argtypes = [
            ctypes.POINTER(wintypes.DWORD), wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]

        arr = (wintypes.DWORD * 4096)()
        needed = wintypes.DWORD()
        if not psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(needed)):
            return []
        paths = []
        for i in range(needed.value // ctypes.sizeof(wintypes.DWORD)):
            pid = arr[i]
            if not pid:
                continue
            handle = k32.OpenProcess(0x1000, False, pid)   # PROCESS_QUERY_LIMITED_INFORMATION
            if not handle:
                continue
            try:
                buf = ctypes.create_unicode_buffer(1024)
                size = wintypes.DWORD(1024)
                if k32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
                    paths.append(buf.value)
            finally:
                k32.CloseHandle(handle)
        return paths
    except Exception:
        return []


def _detect_pcl_dir():
    """找 PCL 目录：配置 > 本服务目录附近 > 正在运行的 PCL 进程"""
    candidates = []
    if PCL_DIR:
        candidates.append(PCL_DIR)
    d = _app_dir()
    for _ in range(3):
        candidates.append(d)
        parent = os.path.dirname(d)
        if parent == d:
            break
        d = parent
    for c in candidates:
        if c and os.path.isfile(os.path.join(c, "Plain Craft Launcher 2.exe")):
            return c
    for p in _process_paths():
        if os.path.basename(p).lower() == "plain craft launcher 2.exe":
            return os.path.dirname(p)
    return ""


def _pcl_dir():
    now = time.time()
    if _ver_cache["pcl"] is None or now - _ver_cache["pcl_time"] > 60:
        _ver_cache["pcl"] = _detect_pcl_dir()
        _ver_cache["pcl_time"] = now
    return _ver_cache["pcl"]


def _game_dir(pcl_dir):
    """从 PCL 目录推游戏目录：优先用 PCL\\Setup.ini 里的 LaunchFolderSelect"""
    if not pcl_dir:
        return ""
    folder = ""
    try:
        with open(os.path.join(pcl_dir, "PCL", "Setup.ini"), "r", encoding="utf-8", errors="ignore") as f:
            for line in f:
                if line.strip().startswith("LaunchFolderSelect:"):
                    folder = line.split(":", 1)[1].strip()
                    break
    except OSError:
        pass
    if folder:
        if folder.startswith("$"):              # $ 代表 PCL 所在目录
            folder = folder[1:]
        folder = folder.replace("/", "\\").strip("\\") or ".minecraft"
        if not os.path.isabs(folder):
            folder = os.path.join(pcl_dir, folder)
    else:
        folder = os.path.join(pcl_dir, ".minecraft")
    return folder


# ---------------- 多人游戏列表（servers.dat） ----------------

SERVERS_DAT_ENTRIES = [        # 要写进「多人游戏」列表的服务器：(显示名, 地址)
    ("IMC 总入口", STATUS_HOST or SERVER),
    ("IMC 离线进服", SERVER),
    ("IMC 正版进服", PREMIUM_SERVER),
]


def _nbt_read_utf(buf, i):
    n = struct.unpack_from(">H", buf, i)[0]
    i += 2
    return buf[i:i + n].decode("utf-8", "replace"), i + n


def _nbt_read_payload(buf, i, t):
    if t == 1:
        return struct.unpack_from(">b", buf, i)[0], i + 1
    if t == 2:
        return struct.unpack_from(">h", buf, i)[0], i + 2
    if t == 3:
        return struct.unpack_from(">i", buf, i)[0], i + 4
    if t == 4:
        return struct.unpack_from(">q", buf, i)[0], i + 8
    if t == 5:
        return struct.unpack_from(">f", buf, i)[0], i + 4
    if t == 6:
        return struct.unpack_from(">d", buf, i)[0], i + 8
    if t == 7:
        n = struct.unpack_from(">i", buf, i)[0]
        i += 4
        return buf[i:i + n], i + n
    if t == 8:
        return _nbt_read_utf(buf, i)
    if t == 9:
        et = buf[i]
        n = struct.unpack_from(">i", buf, i + 1)[0]
        i += 5
        items = []
        for _ in range(n):
            v, i = _nbt_read_payload(buf, i, et)
            items.append(v)
        return (et, items), i
    if t == 10:
        d = {}
        while True:
            nt = buf[i]
            i += 1
            if nt == 0:
                break
            nm, i = _nbt_read_utf(buf, i)
            v, i = _nbt_read_payload(buf, i, nt)
            d[nm] = (nt, v)
        return d, i
    if t == 11:
        n = struct.unpack_from(">i", buf, i)[0]
        i += 4
        return [struct.unpack_from(">i", buf, i + 4 * k)[0] for k in range(n)], i + 4 * n
    if t == 12:
        n = struct.unpack_from(">i", buf, i)[0]
        i += 4
        return [struct.unpack_from(">q", buf, i + 8 * k)[0] for k in range(n)], i + 8 * n
    raise ValueError("未知 NBT 类型 %d" % t)


def _nbt_write_payload(t, v):
    if t == 1:
        return struct.pack(">b", v)
    if t == 2:
        return struct.pack(">h", v)
    if t == 3:
        return struct.pack(">i", v)
    if t == 4:
        return struct.pack(">q", v)
    if t == 5:
        return struct.pack(">f", v)
    if t == 6:
        return struct.pack(">d", v)
    if t == 7:
        return struct.pack(">i", len(v)) + bytes(v)
    if t == 8:
        b = str(v).encode("utf-8")
        return struct.pack(">H", len(b)) + b
    if t == 9:
        et, items = v
        out = bytes([et]) + struct.pack(">i", len(items))
        for it in items:
            out += _nbt_write_payload(et, it)
        return out
    if t == 10:
        out = b""
        for nm, (nt, nv) in v.items():
            b = str(nm).encode("utf-8")
            out += bytes([nt]) + struct.pack(">H", len(b)) + b + _nbt_write_payload(nt, nv)
        return out + b"\x00"
    if t == 11:
        out = struct.pack(">i", len(v))
        for x in v:
            out += struct.pack(">i", x)
        return out
    if t == 12:
        out = struct.pack(">i", len(v))
        for x in v:
            out += struct.pack(">q", x)
        return out
    raise ValueError("未知 NBT 类型 %d" % t)


def _game_running():
    """有 java 在跑（多半正在玩游戏）就先不动 servers.dat，免得游戏退出时用内存里的列表覆盖回去。

    不能用 tasklist：本程序是无窗口的，spawn 控制台程序会闪一下黑框。
    改成枚举本机进程快照（ctypes 调 kernel32，零窗口、零子进程）。
    """
    try:
        import ctypes
        from ctypes import wintypes
        TH32CS_SNAPPROCESS = 0x00000002

        class PROCESSENTRY32(ctypes.Structure):
            _fields_ = [("dwSize", wintypes.DWORD),
                        ("cntUsage", wintypes.DWORD),
                        ("th32ProcessID", wintypes.DWORD),
                        ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
                        ("th32ModuleID", wintypes.DWORD),
                        ("cntThreads", wintypes.DWORD),
                        ("th32ParentProcessID", wintypes.DWORD),
                        ("pcPriClassBase", ctypes.c_long),
                        ("dwFlags", wintypes.DWORD),
                        ("szExeFile", ctypes.c_char * 260)]

        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
        if snap == -1:
            return False
        try:
            entry = PROCESSENTRY32()
            entry.dwSize = ctypes.sizeof(PROCESSENTRY32)
            if not k32.Process32First(snap, ctypes.byref(entry)):
                return False
            while True:
                name = entry.szExeFile.decode("mbcs", "replace").lower()
                if name in ("javaw.exe", "java.exe"):
                    return True
                if not k32.Process32Next(snap, ctypes.byref(entry)):
                    break
            return False
        finally:
            k32.CloseHandle(snap)
    except Exception:
        return True


def _servers_dat_paths():
    """会读到的 servers.dat：游戏根目录 + 每个版本目录（PCL 开了版本隔离时各有一份）"""
    game = _game_dir(_pcl_dir())
    if not game or not os.path.isdir(game):
        return []
    out = []
    root = os.path.join(game, "servers.dat")
    if os.path.isfile(root):
        out.append(root)
    vdir = os.path.join(game, "versions")
    if os.path.isdir(vdir):
        for name in os.listdir(vdir):
            p = os.path.join(vdir, name, "servers.dat")
            if os.path.isfile(p):
                out.append(p)
    return out


def _servers_dat_read(path):
    """读 servers.dat：新版游戏写的是未压缩 NBT，老版本是 gzip，两种都认"""
    with open(path, "rb") as f:
        raw = f.read()
    packed = raw[:2] == b"\x1f\x8b"
    if packed:
        raw = gzip.decompress(raw)
    root, _ = _nbt_read_payload(raw, 3, 10)                 # 跳过 0x0A + 根名字长度
    if not isinstance(root, dict):
        root = {}
    return root, packed


def _servers_dat_write(path, root, packed):
    data = b"\x0a\x00\x00" + _nbt_write_payload(10, root)
    if packed:
        data = gzip.compress(data)
    tmp = path + ".tmp"
    with open(tmp, "wb") as f:
        f.write(data)
    os.replace(tmp, path)


def _servers_dat_sync():
    """把 imc 的几个地址补进各份 servers.dat（多人游戏列表），已有的不重复加、格式保持原样"""
    if not SERVERS_DAT_SYNC or _game_running():
        return
    for path in _servers_dat_paths():
        try:
            root, packed = _servers_dat_read(path)
        except Exception:
            continue
        tag = root.get("servers")
        items = []
        if tag and tag[0] == 9 and isinstance(tag[1], tuple) and tag[1][0] == 10:
            items = list(tag[1][1])
        have = set()
        for it in items:
            if isinstance(it, dict) and "ip" in it and it["ip"][0] == 8:
                have.add(str(it["ip"][1]).strip().lower())
        added = 0
        for name, ip in SERVERS_DAT_ENTRIES:
            ip = (ip or "").strip()
            if not ip or ip.lower() in have:
                continue
            items.append({"name": (8, name), "ip": (8, ip), "acceptTextures": (1, 1)})
            have.add(ip.lower())
            added += 1
        if not added:
            continue
        root["servers"] = (9, (10, items))
        try:
            _servers_dat_write(path, root, packed)
        except Exception:
            pass


def _servers_dat_put(name, ip):
    """把一个服务器写进所有 servers.dat（已有同名同地址就跳过）；返回写入份数"""
    ip = (ip or "").strip()
    if not ip:
        return 0
    if _game_running():
        return 0
    n = 0
    for path in _servers_dat_paths():
        try:
            root, packed = _servers_dat_read(path)
        except Exception:
            continue
        tag = root.get("servers")
        items = []
        if tag and tag[0] == 9 and isinstance(tag[1], tuple) and tag[1][0] == 10:
            items = list(tag[1][1])
        hit = False
        for it in items:
            if not isinstance(it, dict):
                continue
            ipv = it.get("ip")
            if ipv and ipv[0] == 8 and str(ipv[1]).strip().lower() == ip.lower():
                hit = True
                if it.get("name") and it["name"][0] == 8 and str(it["name"][1]) != name:
                    it["name"] = (8, name)
                break
        if hit:
            continue
        items.append({"name": (8, name), "ip": (8, ip), "acceptTextures": (1, 1)})
        root["servers"] = (9, (10, items))
        try:
            _servers_dat_write(path, root, packed)
            n += 1
        except Exception:
            pass
    return n


_tc_fill_last = {"url": ""}


def _tc_autofill_watch():
    """设置开了「联机成功后自动填入服务器地址」时，把陶瓦给的连接地址写进多人游戏列表"""
    try:
        _tc_cfg_load()
        if not _tc_cfg.get("autofill"):
            return
        st = _tc_state()
        if not isinstance(st, dict):
            return
        if str(st.get("state", "")) != "guest-ok":
            return
        url = (st.get("url") or "").strip()
        if not url or url == _tc_fill_last["url"]:
            return
        _tc_fill_last["url"] = url
        n = _servers_dat_put("陶瓦联机", url)
        if n:
            _tc_log("已把陶瓦联机地址写入 %d 份 servers.dat：%s" % (n, url))
    except Exception:
        pass


def _sort_versions(names):
    """MC 版本号倒序在前，整合包/自定义版本名排在后面"""
    def key(n):
        m = _MC_VER_RE.match(n)
        if m:
            return (0, tuple(-int(x or 0) for x in m.groups()), n.lower())
        return (1, (0, 0, 0), n.lower())
    return sorted(names, key=key)


_MC_VER_IN_TEXT = re.compile(r"(?<!\d)1\.(\d{1,2})(?:\.(\d{1,2}))?(?!\d)")


def _mc_tuple(txt):
    """从任意字符串里抠出 MC 版本号（1.8 / 1.12.2 / 1.21.11），返回可比较的元组"""
    if not txt:
        return None
    m = _MC_VER_IN_TEXT.search(str(txt))
    if not m:
        return None
    return (1, int(m.group(1)), int(m.group(2) or 0))


def _version_mc(name):
    """这个版本实际是哪一版 MC：优先版本 JSON 的 clientVersion，再看 jar / inheritsFrom / id，最后看文件夹名"""
    vdir = VERSIONS_DIR.strip() or os.path.join(_game_dir(_pcl_dir()), "versions")
    txts = []
    if vdir and name:
        jpath = os.path.join(vdir, name, name + ".json")
        if os.path.isfile(jpath):
            try:
                with open(jpath, "r", encoding="utf-8", errors="ignore") as f:
                    j = json.load(f)
            except Exception:
                j = {}
            for k in ("clientVersion", "jar", "inheritsFrom", "id"):
                v = j.get(k) if isinstance(j, dict) else None
                if isinstance(v, str) and v.strip():
                    txts.append(v)
    txts.append(name or "")
    for c in txts:
        t = _mc_tuple(c)
        if t:
            return t, c
    return None, ""


def _in_mc_range(name):
    """是否落在 MC_VERSION_MIN ~ MC_VERSION_MAX 之间（检测不出来的一律不算）"""
    t, _ = _version_mc(name)
    if not t:
        return False
    lo = _mc_tuple(MC_VERSION_MIN) or (1, 8, 0)
    hi = _mc_tuple(MC_VERSION_MAX) or (1, 21, 11)
    return lo <= t <= hi


def _scan_versions():
    """本机已装版本名（5 秒缓存）；扫不到就返回空列表"""

    now = time.time()
    if _ver_cache["vers"] is not None and now - _ver_cache["vers_time"] < 5:
        return _ver_cache["vers"]
    names = []
    if VERSIONS_SCAN:
        try:
            vdir = VERSIONS_DIR.strip() or os.path.join(_game_dir(_pcl_dir()), "versions")
            if vdir and os.path.isdir(vdir):
                for name in os.listdir(vdir):
                    if os.path.isfile(os.path.join(vdir, name, name + ".json")):
                        names.append(name)
        except OSError:
            pass
    names = _sort_versions(names)
    _ver_cache["vers"] = names
    _ver_cache["vers_time"] = now
    return names


def _btn(text, event_type, data, tip, width=None, height=35, margin="0,0,0,0", column=None):
    """按钮。width=None 时横向拉伸（跟着窗口变），指定 column 时放进 Grid 的对应列。"""
    S = xaml_escape
    col = '' if column is None else ' Grid.Column="%d"' % column
    size = 'HorizontalAlignment="Stretch"' if width is None else 'Width="%d"' % width
    return ('            <local:MyButton%s %s Height="%d" Margin="%s" Padding="10,0,10,0" ColorType="Highlight"\n'
            '                        Text="%s" EventType="%s" EventData="%s" ToolTip="%s" />\n'
            % (col, size, height, margin, S(text), event_type, S(data), S(tip)))


def _version_row(name):
    S = xaml_escape
    short = name if len(name) <= 16 else name[:15] + "…"
    return ('        <Grid Margin="0,0,0,6">\n'
            '            <Grid.ColumnDefinitions>\n'
            '                <ColumnDefinition Width="150" />\n'
            '                <ColumnDefinition Width="*" />\n'
            '                <ColumnDefinition Width="*" />\n'
            '            </Grid.ColumnDefinitions>\n'
            '            <TextBlock Grid.Column="0" VerticalAlignment="Center" Text="' + S(short)
            + '" ToolTip="' + S(name) + '" ' + _fg() + ' />\n'
            + _btn("正版", "启动游戏", name + "|" + PREMIUM_SERVER,
                   "用 %s 进入 %s（正版账号）" % (name, PREMIUM_SERVER),
                   height=32, margin="0,0,4,0", column=1)
            + _btn("离线", "启动游戏", name + "|" + SERVER,
                   "用 %s 进入 %s（离线账号）" % (name, SERVER),
                   height=32, margin="4,0,0,0", column=2)
            + '        </Grid>\n')


def _launch_block():
    """状态卡里的进服区：只剩「服务器官网」一个按钮，横向拉满整行"""
    return ('        <Grid Margin="0,2,0,0">\n'
            '            <local:MyButton HorizontalAlignment="Stretch" Height="35" Padding="13,0,13,0" ColorType="Highlight"\n'
            '                        Text="服务器官网" EventType="打开网页" EventData="' + WEBSITE + '"\n'
            '                        ToolTip="在浏览器打开 IMC 服务器官网 ' + WEBSITE + '" />\n'
            '        </Grid>\n')


def _versions_card():
    """「按版本进服」：可折叠卡片，只列检测到 1.8~1.21.11 的版本，每个版本一行 正版 / 离线"""
    all_vers = _scan_versions()
    vers = [v for v in all_vers if _in_mc_range(v)]
    default = ""
    if LAUNCH_VERSION and LAUNCH_VERSION in vers:
        default = LAUNCH_VERSION
    elif vers:
        default = vers[0]

    if not all_vers:
        return _card("按版本进服",
                     '        <TextBlock TextWrapping="Wrap" Foreground="#FF6B7885"\n'
                     '                   Text="没扫描到本机已安装的游戏版本，可用首页左侧的「启动游戏」进入。" />\n')

    if not vers:
        return _card("按版本进服",
                     '        <TextBlock TextWrapping="Wrap" Foreground="#FF6B7885"\n'
                     '                   Text="已装版本里没有检测到 ' + MC_VERSION_MIN + '~' + MC_VERSION_MAX
                     + ' 的兼容版本，可用首页左侧的「启动游戏」进入。" />\n')

    rows = [default] + [v for v in vers if v != default]
    inner = ('        <TextBlock TextWrapping="Wrap" Margin="0,0,0,8" Foreground="#FF6B7885"\n'
             '                   Text="正版点「正版」，离线点「离线」；只列 ' + MC_VERSION_MIN + '~' + MC_VERSION_MAX
             + ' 的版本（' + str(len(rows)) + ' 个）。" />\n')
    inner += "".join(_version_row(v) for v in rows)
    # 用 PCL 原生卡片包起来，就能点标题折叠 / 展开（玻璃卡片不支持折叠）
    # 折叠 / 展开要 PCL 原生 MyCard 才行；外面再套一层毛玻璃 Border，
    # 服务里的补丁会把 MyCard 自带的白底去掉，于是折叠卡也是毛玻璃的。
    card = ('<local:MyCard Title="按版本进服（点标题折叠 / 展开）" CanSwap="True" IsSwapped="False" Background="Transparent" HasMouseAnimation="False" Tag="glass">\n'
            '        <StackPanel Margin="22,40,22,12">\n' + inner + '        </StackPanel>\n'
            '    </local:MyCard>\n')
    if _style() == "classic":
        return '<local:MyCard Title="按版本进服（点标题折叠 / 展开）" Margin="0,0,0,15" CanSwap="True" IsSwapped="False">\n' \
               '    <StackPanel Margin="25,40,23,10">\n' + inner + '    </StackPanel>\n</local:MyCard>\n\n'
    return ('<Border BorderBrush="' + GLASS_BORDER + '" BorderThickness="1" CornerRadius="' + str(GLASS_CORNER) + '" Margin="0,0,0,15">\n'
            '    <Border.Background>\n'
            '        <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">\n'
            '            <GradientStop Color="' + GLASS_CARD + '" Offset="0" />\n'
            '            <GradientStop Color="' + GLASS_CARD2 + '" Offset="1" />\n'
            '        </LinearGradientBrush>\n'
            '    </Border.Background>\n'
            '    <Border.Effect>\n'
            '        <DropShadowEffect BlurRadius="22" ShadowDepth="2" Direction="270" Opacity="0.25" Color="#FF15283C" />\n'
            '    </Border.Effect>\n    ' + card +
            '</Border>\n\n')


def _style():
    """当前卡片样式：glass = 毛玻璃，classic = PCL 原生白卡片。
    读同目录的样式文件，改完不需要重启服务，主页下次刷新（约 1 秒）就换过来。"""
    try:
        with open(os.path.join(_app_dir(), STYLE_FILE), "r", encoding="utf-8", errors="ignore") as f:
            txt = f.read().strip().lower()
    except OSError:
        return "glass"
    return "classic" if ("classic" in txt or "经典" in txt or "关闭" in txt) else "glass"


def _fg():
    """玻璃卡片在浅色半透明底上要用深色字；经典卡片跟随 PCL 主题，不硬指定颜色"""
    return 'Foreground="' + GLASS_TEXT + '"' if _style() == "glass" else ""


def _card(title, inner, margin="0,0,0,14"):
    """卡片：毛玻璃模式用半透明 Border 自己画；经典模式用 PCL 自带的 MyCard"""
    S = xaml_escape
    if _style() == "classic":
        return ('<local:MyCard Title="' + S(title) + '" Margin="0,0,0,15" CanSwap="True" IsSwapped="False">\n'
                '    <StackPanel Margin="25,40,23,15">\n' + inner + '    </StackPanel>\n'
                '</local:MyCard>\n\n')
    head = ""
    if title:
        head = ('            <TextBlock FontWeight="Bold" FontSize="15" Margin="0,0,0,8" TextWrapping="Wrap"\n'
                '                       ' + _fg() + ' Text="' + S(title) + '" />\n')
    return ('<Border BorderBrush="' + GLASS_BORDER + '" BorderThickness="1"\n'
            '        CornerRadius="' + str(GLASS_CORNER) + '" Margin="' + margin + '">\n'
            '    <Border.Background>\n'
            '        <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">\n'
            '            <GradientStop Color="' + GLASS_CARD + '" Offset="0" />\n'
            '            <GradientStop Color="' + GLASS_CARD2 + '" Offset="1" />\n'
            '        </LinearGradientBrush>\n'
            '    </Border.Background>\n'
            '    <Border.Effect>\n'
            '        <DropShadowEffect BlurRadius="22" ShadowDepth="2" Direction="270" Opacity="0.25" Color="#FF15283C" />\n'
            '    </Border.Effect>\n'
            '    <StackPanel Margin="22,15,22,16">\n'
            + head + inner +
            '    </StackPanel>\n'
            '</Border>\n\n')


def _fetch_with_deadline(server, deadline_s=12):
    """后台线程执行状态获取，硬超时兜底：任何网络异常都不会卡住主页刷新"""
    box = {}
    def run():
        box["r"] = fetch_status(server)
    threading.Thread(target=run, daemon=True).start()
    t0 = time.time()
    while time.time() - t0 < deadline_s:
        if "r" in box:
            return box["r"]
        time.sleep(0.1)
    return {"online": False, "motd": "", "version": "-", "players": "0 / 0",
            "plist": [], "latency": None, "source": "-"}


def _live_values(st):
    """实时数据：主页本身保持恒定，只有带 Tag="live:*" 的几个控件由 PCLPatch.dll 改文字。

    这样 PCL 每秒拿到的 XAML 完全一样，不会重建页面；
    折叠状态、滚动位置、毛玻璃都不会被刷新冲掉。
    """
    if st["online"]:
        status = "on"
        motd = "MOTD：" + st["motd"]
        info = "版本：" + st["version"] + "　在线：" + st["players"]
        if st["latency"]:
            info += "　延迟：" + str(st["latency"]) + "ms"
    else:
        status = "off"
        motd = "服务器暂不可用，请稍后再试"
        info = "版本：-　在线：-"
    if st["online"] and st["plist"]:
        shown = st["plist"]
        more = ""
        if len(shown) > 40:
            shown = shown[:40]
            more = "　等 %d 人" % int(st["players"].split(" / ")[0])
        plist = "、".join(shown) + more
    elif st["online"]:
        plist = "服务器未公开在线玩家列表。"
    else:
        plist = "服务器离线，无法获取玩家列表。"
    return {"status": status, "motd": motd, "info": info, "plist": plist,
            "tc": _tc_text(), "tc2": _tc_text2(), "tccode": _tc_code(_tc_state() or {}),
            "tcurl": _tc_url(_tc_state() or {}),
            "tccodeok": "1" if _tc_code(_tc_state() or {}) else "0",
            "tcurlok": "1" if _tc_url(_tc_state() or {}) else "0",
            "tcrun": "1" if _tc_running() else "0",
            "tcenable": "1" if _tc_cfg_load()["enable"] else "0",
            "tcauto": "1" if _tc_cfg_load()["autostart"] else "0",
            "tcstop": "1" if _tc_cfg_load()["autostop"] else "0",
            "tcfill": "1" if _tc_cfg_load()["autofill"] else "0",
            "tcpath": _tc_path_text(),
            "btnenable": ("☑ " if _tc_cfg_load()["enable"] else "☐ ") + "启用陶瓦联机",
            "btnautostart": ("☑ " if _tc_cfg_load()["autostart"] else "☐ ") + "启动启动器时自动启动",
            "btnautofill": ("☑ " if _tc_cfg_load()["autofill"] else "☐ ") + "联机成功后自动填入服务器地址",
            "btnautostop": ("☑ " if _tc_cfg_load()["autostop"] else "☐ ") + "退出启动器时停止陶瓦联机"}


# ---------------- 陶瓦联机（Terracotta） ----------------

TC_DIR = os.path.expandvars(r"%LOCALAPPDATA%\PCL\Terracotta")
# 没装陶瓦时自动下载这个（官方 GitHub release，和 PCL 自己下载的是同一个包）
TC_VERSION = "0.4.2"
TC_DOWNLOAD_URL = ("https://github.com/burningtnt/Terracotta/releases/download/v%s/"
                   "terracotta-%s-windows-x86_64-pkg.tar.gz" % (TC_VERSION, TC_VERSION))
TC_STATE_TEXT = {
    "waiting": "空闲",
    "host-scanning": "等待你开放局域网世界",
    "host-starting": "正在建立联机通道……",
    "host-ok": "房间已就绪",
    "guest-connecting": "正在连接房间……",
    "guest-starting": "正在启动联机……",
    "guest-ok": "已加入房间",
    "error": "出错",
    "panic": "出错",
    "exception": "联机出错（公共节点连不上）",
}


TC_SETTINGS_FILE = "terracotta-home.json"
_tc_cfg = {"loaded": False, "enable": True, "autostart": False,
           "autostop": False, "autofill": False, "nick": "", "path": ""}


def _tc_cfg_path():
    return os.path.join(TC_DIR, TC_SETTINGS_FILE)


def _tc_cfg_load():
    """读本项目的陶瓦设置（存在 %LOCALAPPDATA%\\PCL\\Terracotta\\terracotta-home.json）"""
    global _tc_cfg
    if _tc_cfg["loaded"]:
        return _tc_cfg
    try:
        with open(_tc_cfg_path(), "r", encoding="utf-8") as f:
            data = json.load(f)
        for k in ("enable", "autostart", "autostop", "autofill"):
            if k in data:
                _tc_cfg[k] = bool(data[k])
        for k in ("nick", "path"):
            if k in data and isinstance(data[k], str):
                _tc_cfg[k] = data[k]
    except (OSError, ValueError):
        pass
    _tc_cfg["loaded"] = True
    return _tc_cfg


def _tc_cfg_save():
    try:
        os.makedirs(TC_DIR, exist_ok=True)
        with open(_tc_cfg_path(), "w", encoding="utf-8") as f:
            json.dump({"enable": _tc_cfg["enable"], "autostart": _tc_cfg["autostart"],
                       "autostop": _tc_cfg["autostop"], "autofill": _tc_cfg["autofill"],
                       "nick": _tc_cfg["nick"], "path": _tc_cfg["path"]}, f, ensure_ascii=False)
    except OSError:
        pass


# 点「自动检测」/「浏览」后在路径那一行显示结果；不自动消失，路径变了才恢复
_tc_notice = ""
_tc_notice_path = ""


def _tc_say(text, path=""):
    global _tc_notice, _tc_notice_path
    _tc_notice = text
    _tc_notice_path = path


def _tc_path_text():
    """路径那一行：刚点过按钮就显示结果，路径变了才恢复成真实路径"""
    global _tc_notice
    cur = _tc_exe() or ""
    if _tc_notice:
        if _tc_notice_path:
            if cur == _tc_notice_path:
                return _tc_notice
            _tc_notice = ""
        elif not cur:
            return _tc_notice
        else:
            _tc_notice = ""
    return cur or "未找到 terracotta.exe（点「浏览」手动选）"


def _tc_exe():
    """只查有没有装好（不下载，避免卡住主页刷新）"""
    _tc_cfg_load()
    p = _tc_cfg.get("path") or ""
    if p and os.path.isfile(p):
        return p
    if not os.path.isdir(TC_DIR):
        return None
    for name in sorted(os.listdir(TC_DIR), reverse=True):
        p = os.path.join(TC_DIR, name, "terracotta.exe")
        if os.path.isfile(p):
            return p
    return None


def _tc_install_async():
    """后台下载陶瓦，主页只显示进度，不阻塞"""
    global _tc_install_busy, _tc_install_msg
    with _tc_install_lock:
        if _tc_install_busy:
            return False
        _tc_install_busy = True
        _tc_install_msg = "正在下载陶瓦联机……"
    threading.Thread(target=_tc_install, daemon=True).start()
    return True


def _tc_install():
    """从 GitHub 拉最新版陶瓦，解压到 %LOCALAPPDATA%\\PCL\\Terracotta\\<版本>\\，
    目录结构跟 PCL 自己下载的一致，所以装完就是正常可用的状态。
    只在后台线程里调用（busy 标记由 _tc_install_async 负责）。"""
    try:
        return _tc_install_inner()
    finally:
        global _tc_install_busy
        with _tc_install_lock:
            _tc_install_busy = False


_tc_install_lock = threading.Lock()
_tc_install_busy = False
_tc_install_msg = ""


def _tc_install_inner():
    global _tc_install_msg
    url = TC_DOWNLOAD_URL
    try:
        _tc_log("开始下载陶瓦：" + url)
        _tc_install_msg = "正在下载陶瓦联机……"
        req = urllib.request.Request(url, headers={"User-Agent": "PCL-Patch"})
        with urllib.request.urlopen(req, timeout=60) as r:
            data = r.read()
        if len(data) < 1024 * 1024:
            _tc_log("下载内容过小，放弃：" + str(len(data)))
            _tc_install_msg = "陶瓦下载失败（文件不完整）"
            return None

        ver = TC_VERSION
        tmpdir = tempfile.mkdtemp(prefix="tc-pkg-")
        pkg = os.path.join(tmpdir, "tc.tar.gz")
        with open(pkg, "wb") as f:
            f.write(data)

        _tc_install_msg = "正在解压陶瓦联机……"
        target = os.path.join(TC_DIR, ver)
        os.makedirs(target, exist_ok=True)
        # 用系统自带 tar（Win10 17063+ 就有），避免额外依赖
        subprocess.check_call(["tar", "-xzf", pkg, "-C", tmpdir], **_hidden_kwargs())
        got = False
        for name in os.listdir(tmpdir):
            src = os.path.join(tmpdir, name)
            if not os.path.isfile(src):
                continue
            low = name.lower()
            if low.endswith(".exe"):
                dst = os.path.join(target, "terracotta.exe")
                if os.path.exists(dst):
                    os.remove(dst)
                os.replace(src, dst)
                got = True
            elif low.endswith(".dll"):
                os.replace(src, os.path.join(target, name))
        try:
            os.remove(pkg)
            os.rmdir(tmpdir)
        except OSError:
            pass
        if not got:
            _tc_log("压缩包里没有 exe")
            _tc_install_msg = "陶瓦安装失败（包里没有主程序）"
            return None
        _tc_log("陶瓦已安装到 " + target)
        _tc_install_msg = "陶瓦已就绪"
        return os.path.join(target, "terracotta.exe")
    except Exception as ex:
        _tc_log("陶瓦下载/安装失败：" + repr(ex))
        _tc_install_msg = "陶瓦下载失败，请检查网络"
        return None


def _tc_log(msg):
    try:
        p = os.path.join(os.path.dirname(os.path.abspath(sys.argv[0])), "terracotta.log")
        with open(p, "a", encoding="utf-8") as f:
            f.write(time.strftime("%H:%M:%S ") + msg + "\n")
    except OSError:
        pass


def _hidden_kwargs():
    """子进程一律用隐藏窗口方式启动。

    只加 creationflags=CREATE_NO_WINDOW 在部分机器上还是会闪一下黑框，
    必须同时给 STARTUPINFO 指定 SW_HIDE，才能真正做到“看不见”。
    """
    si = subprocess.STARTUPINFO()
    si.dwFlags = subprocess.STARTF_USESHOWWINDOW
    si.wShowWindow = 0  # SW_HIDE
    return {"creationflags": 0x08000000, "startupinfo": si}


def _run(args, timeout=6):
    """跑一个控制台程序并拿回输出（全程无窗口）"""
    try:
        return subprocess.check_output(args, timeout=timeout,
                                       **_hidden_kwargs()).decode("gbk", "replace")
    except Exception:
        return ""


def _tc_port_note(port):
    """把自己发现的端口记到 %TEMP%\\PCL-Terracotta\\port-home.json，
    这样服务重启后（或陶瓦是别的东西拉起来的）也能立刻找回端口，
    不用再去 tasklist / netstat 扫进程。"""
    global _tc_launched_port
    _tc_launched_port = port
    try:
        d = os.path.join(tempfile.gettempdir(), "PCL-Terracotta")
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, "port-home.json"), "w", encoding="utf-8") as f:
            json.dump({"port": port}, f)
    except OSError:
        pass


def _tc_port_forget():
    global _tc_launched_port
    _tc_launched_port = None
    try:
        os.remove(os.path.join(tempfile.gettempdir(), "PCL-Terracotta", "port-home.json"))
    except OSError:
        pass


def _tc_pcl_ports():
    """PCL 自己启动陶瓦时会写 %TEMP%\\PCL-Terracotta\\port-<pid>-<guid>.json，
    内容是 {"port": 端口}。读这个文件就知道“别人拉起来的陶瓦”在哪个端口，
    完全不用去 tasklist 查进程（查进程会闪控制台窗口）。"""
    ports = []
    d = os.path.join(tempfile.gettempdir(), "PCL-Terracotta")
    try:
        names = os.listdir(d)
    except OSError:
        return ports
    for name in names:
        if not (name.startswith("port-") and name.endswith(".json")):
            continue
        try:
            with open(os.path.join(d, name), "r", encoding="utf-8") as f:
                data = json.load(f)
            p = data.get("port") if isinstance(data, dict) else data
            if isinstance(p, int) and 0 < p < 65536:
                try:
                    mt = os.path.getmtime(os.path.join(d, name))
                except OSError:
                    mt = 0.0
                ports.append((mt, p))
        except (OSError, ValueError):
            pass
    ports.sort(reverse=True)
    return ports


def _tc_port():
    """找陶瓦网页端口——全程不启动子进程，并且结果缓存 2 秒。

    /live.json 每 0.5 秒被问一次，如果每次都去连端口，光超时就能把服务拖死。
    """
    global _tc_launched_port
    now = time.time()
    if _tc_launched_port:
        if _tc_probe(_tc_launched_port):
            return _tc_launched_port
        _tc_launched_port = None
    if _tc_port_cache[2] and now - _tc_port_cache[1] < 2.0:
        return _tc_port_cache[0]
    port = None
    files = _tc_pcl_ports()          # [(mtime, port), ...] 按新旧排
    for _, p in files[:2]:           # 最多探两个最近的，别把时间耗在过期文件上
        if _tc_probe(p):
            port = p
            break
    _tc_port_cache[0] = port
    _tc_port_cache[1] = now
    _tc_port_cache[2] = True
    return port

def _tc_port_deep():
    """便宜路径找不到时，才按进程 PID 反查端口。

    只在点「创建房间 / 加入房间」这类动作里调用，主页每秒刷新时绝不走这里。
    """
    p = _tc_port()
    if p:
        return p
    pids = _tc_pids(force=True)
    if not pids:
        return None
    cached = _tc_port_cache
    if cached[0] in pids:
        return cached[1]
    out = _run(["netstat", "-ano", "-p", "TCP"])
    if not out:
        return None
    ports = set()
    for line in out.splitlines():
        parts = line.split()
        if len(parts) < 5 or parts[3] != "LISTENING":
            continue
        try:
            pid = int(parts[4])
        except ValueError:
            continue
        if pid not in pids:
            continue
        addr = parts[1]
        if not addr.startswith("127.0.0.1:"):
            continue
        try:
            ports.add(int(addr.split(":")[-1]))
        except ValueError:
            pass
    for port in sorted(ports):
        if _tc_probe(port):
            _tc_port_cache[0] = pids[0]
            _tc_port_cache[1] = port
            return port
    return None


_tc_port_cache = [None, 0.0, False]
_tc_launched_port = None


def _tc_probe(port):
    try:
        with urllib.request.urlopen("http://127.0.0.1:%d/meta" % port, timeout=0.3) as r:
            return b"terracotta" in r.read().lower()
    except Exception:
        return False


# 主页每 0.5 秒问一次状态，每次都去 tasklist 太浪费（还会闪窗口），缓存 3 秒
_tc_pid_cache = {"t": 0.0, "pids": []}


def _tc_pids(force=False):
    now = time.time()
    if not force and now - _tc_pid_cache["t"] < 3.0:
        return _tc_pid_cache["pids"]
    out = _run(["tasklist", "/FI", "IMAGENAME eq terracotta.exe", "/FO", "CSV", "/NH"])
    pids = []
    for line in out.splitlines():
        cells = [c.strip('"') for c in line.split('","')]
        if len(cells) >= 2 and cells[0].lower().startswith("terracotta"):
            try:
                pids.append(int(cells[1]))
            except ValueError:
                pass
    _tc_pid_cache["t"] = now
    _tc_pid_cache["pids"] = pids
    return pids


def _tc_http(path, timeout=2.5):
    port = _tc_port()
    if not port:
        return None
    try:
        req = urllib.request.Request("http://127.0.0.1:%d%s" % (port, path),
                                     headers={"User-Agent": "PCL-Homepage"})
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.read().decode("utf-8", "replace")
    except Exception:
        return None


_state_cache = {"t": 0.0, "v": None}


def _tc_state():
    """给 /live.json 用：2 秒内直接返回缓存。

    主页每 0.5 秒问一次，每次真去连陶瓦会把服务拖垮（端口探测有超时），
    所以这里必须缓存，宁可晚 2 秒也不要把请求堆起来。
    """
    now = time.time()
    if now - _state_cache["t"] < 2.0:
        return _state_cache["v"]
    raw = _tc_http("/state")
    res = None
    if raw:
        try:
            res = json.loads(raw)
        except ValueError:
            res = None
    _state_cache["t"] = now
    _state_cache["v"] = res
    return res


def _tc_running():
    # 只探端口，不问 tasklist：主页每秒刷新都会调到这里，绝不能启动控制台程序
    return _tc_port() is not None


def _tc_start():
    """确保陶瓦在跑：没装就先下载，没跑就拉起来，等它的网页端口就绪"""
    if _tc_port_deep():
        return True
    exe = _tc_exe()
    if not exe:
        if not _tc_install_busy:
            _tc_install_async()
        # 等下载 + 解压，最多 90 秒
        for _ in range(360):
            time.sleep(0.25)
            exe = _tc_exe()
            if exe:
                break
        if not exe:
            return False
    try:
        # 把陶瓦的 stdout 收进文件：它会打印网页端口，读出来就不用再 tasklist/netstat
        outpath = os.path.join(tempfile.gettempdir(), "PCL-Terracotta-launch.log")
        try:
            outf = open(outpath, "wb")
        except OSError:
            outf = subprocess.DEVNULL
        # --hmcl2 --hmcl 是陶瓦给启动器集成用的模式：只开接口，不弹浏览器。
        # 不加这两个参数，陶瓦一启动就会自己打开网页，用户还得手动关掉。
        subprocess.Popen([exe, "--hmcl2", "--hmcl"], cwd=os.path.dirname(exe),
                         stdout=outf, stderr=subprocess.STDOUT,
                         **_hidden_kwargs())
        port = _tc_wait_port_file(outpath)
        if port:
            _tc_port_note(port)
            return True
    except OSError:
        return False
    _tc_pids(force=True)     # 没读到端口，退回按进程反查
    for _ in range(60):
        time.sleep(0.25)
        p = _tc_port_deep()
        if p:
            _tc_port_note(p)
            return True
    return False


def _tc_wait_port_file(path, seconds=12):
    """从陶瓦的启动输出里抠出 http://127.0.0.1:<端口>"""
    deadline = time.time() + seconds
    try:
        while time.time() < deadline:
            time.sleep(0.2)
            if not os.path.isfile(path):
                continue
            with open(path, "rb") as f:
                txt = f.read().decode("utf-8", "replace")
            m = re.search(r"127\.0\.0\.1:(\d+)", txt)
            if m:
                return int(m.group(1))
    except OSError:
        pass
    return None


def _tc_fire(path):
    """/state/scanning 这类接口会一直挂到状态机变化才返回，
    所以丢到后台线程里发，接口本身立刻返回，主页不用干等。"""
    def run():
        _tc_http(path, timeout=120)
    threading.Thread(target=run, daemon=True).start()


def _tc_enabled():
    """「启用陶瓦联机」关掉后，所有陶瓦动作都不做"""
    _tc_cfg_load()
    return bool(_tc_cfg.get("enable"))


def _tc_nick_q(player):
    """陶瓦用 query 参数 player 传联机昵称（留空就用它的默认昵称）"""
    p = (player or "").strip()
    return "" if not p else "?player=" + urllib.parse.quote(p)


def _tc_host(player=""):
    if not _tc_enabled():
        return False
    if not _tc_start():
        return False
    _tc_fire("/state/scanning" + _tc_nick_q(player))
    return True


def _tc_join(code, player=""):
    if not _tc_enabled():
        return False
    if not _tc_start():
        return False
    # 如果现在正在开房（host-*），先退回空闲，否则陶瓦会忽略加入请求
    cur = str((_tc_state() or {}).get("state", ""))
    if cur and cur != "waiting":
        _tc_http("/state/ide", timeout=3)
        for _ in range(20):
            time.sleep(0.2)
            if str((_tc_state() or {}).get("state", "")) == "waiting":
                break
    _tc_fire("/state/guesting?room=" + urllib.parse.quote(code) + _tc_nick_q(player).replace("?", "&"))
    return True


def _tc_open_url():
    port = _tc_port()
    return "http://127.0.0.1:%d/" % port if port else None


def _pcl_running():
    """PCL 主程序在不在跑（用进程快照，不启动任何子进程）"""
    try:
        import ctypes
        from ctypes import wintypes
        TH32CS_SNAPPROCESS = 0x00000002

        class PE32(ctypes.Structure):
            _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
                        ("th32ProcessID", wintypes.DWORD),
                        ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
                        ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
                        ("th32ParentProcessID", wintypes.DWORD),
                        ("pcPriClassBase", ctypes.c_long), ("dwFlags", wintypes.DWORD),
                        ("szExeFile", ctypes.c_char * 260)]

        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
        if snap == -1:
            return True
        try:
            e = PE32()
            e.dwSize = ctypes.sizeof(PE32)
            if not k32.Process32First(snap, ctypes.byref(e)):
                return True
            while True:
                name = e.szExeFile.decode("mbcs", "replace").lower()
                if name == "plain craft launcher 2.exe":
                    return True
                if not k32.Process32Next(snap, ctypes.byref(e)):
                    break
            return False
        finally:
            k32.CloseHandle(snap)
    except Exception:
        return True


def _tc_autostop_watch():
    """服务端兜底：设置开了「退出启动器时停止」，而 PCL 已经不在了，就把陶瓦停掉"""
    try:
        _tc_cfg_load()
        if not _tc_cfg.get("autostop"):
            return
        if _pcl_running():
            return
        if not _tc_running():
            return
        _tc_log("PCL 已退出，按设置停止陶瓦")
        _tc_stop()
    except Exception:
        pass


def _tc_setpath(path):
    """手动指定 terracotta.exe 路径（浏览按钮）"""
    _tc_cfg_load()
    p = (path or "").strip().strip('"')
    if not p or not os.path.isfile(p):
        return False
    _tc_cfg["path"] = p
    _tc_cfg_save()
    _tc_port_forget()
    _tc_say("✔ 已设置为：" + p, p)
    return True


def _tc_fillnow():
    """立即把当前连接地址写进多人游戏列表（用于验证自动填入是否可用）"""
    st = _tc_state() or {}
    url = _tc_url(st)
    if not url:
        return False
    n = _servers_dat_put("陶瓦联机", url)
    _tc_log("手动填入：写入 %d 份 servers.dat，地址 %s" % (n, url))
    return n > 0


def _tc_autostart():
    """PCL 启动时调用：如果开了「启动启动器时自动启动」就把陶瓦拉起来"""
    _tc_cfg_load()
    if not _tc_cfg.get("enable") or not _tc_cfg.get("autostart"):
        return False
    if _tc_running():
        return True
    return _tc_start()


def _tc_autostop():
    """PCL 退出时调用：如果开了「退出启动器时停止陶瓦联机」就结束陶瓦"""
    _tc_cfg_load()
    if not _tc_cfg.get("autostop"):
        return False
    if not _tc_running():
        return True
    return _tc_stop()


def _tc_toggle(key):
    """翻转主页上的开关（enable / autostart / autostop / autofill），并写回配置文件"""
    _tc_cfg_load()
    # 主页上传的是实时字段名（tcenable / tcauto / tcfill / tcstop），这里映射成配置键
    alias = {"tcenable": "enable", "tcauto": "autostart",
             "tcfill": "autofill", "tcstop": "autostop"}
    key = alias.get(key, key)
    if key not in ("enable", "autostart", "autostop", "autofill"):
        return False
    _tc_cfg[key] = not bool(_tc_cfg.get(key))
    _tc_cfg_save()
    return True


def _tc_detect():
    """自动检测陶瓦程序路径：先常见目录，再问 PCL 的安装目录"""
    _tc_cfg_load()
    cands = []
    for ver in ("0.4.2", "0.4.1", "0.4.0"):
        cands.append(os.path.join(TC_DIR, ver, "terracotta.exe"))
    try:
        for name in sorted(os.listdir(TC_DIR), reverse=True):
            cands.append(os.path.join(TC_DIR, name, "terracotta.exe"))
    except OSError:
        pass
    for p in cands:
        if os.path.isfile(p):
            _tc_cfg["path"] = p
            _tc_cfg_save()
            # 点完必须能看出变化：路径那一行先闪 6 秒「已找到」
            _tc_say("✔ 已找到：" + p, p)
            return True
    _tc_say("✘ 没找到 terracotta.exe，点「浏览」手动选")
    return False


def _tc_detach():
    """断开房间：只退回空闲状态，陶瓦进程留着，下次建房不用重新启动"""
    _tc_http("/state/ide", timeout=6)
    state = ""
    for _ in range(20):
        time.sleep(0.2)
        state = str((_tc_state() or {}).get("state", ""))
        if state == "waiting":
            break
    return state == "waiting"


def _tc_stop():
    """退出房间并关掉陶瓦进程"""
    _tc_http("/state/ide")
    time.sleep(0.3)
    try:
        subprocess.call(["taskkill", "/IM", "terracotta.exe", "/F"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                        **_hidden_kwargs())
    except Exception:
        return False
    _tc_port_forget()
    _tc_pids(force=True)
    time.sleep(0.3)
    return not _tc_running()


def _tc_text():
    """给主页显示的一行状态"""
    if not _tc_enabled():
        return "已关闭（勾上「启用陶瓦联机」才能用）"
    if _tc_install_busy:
        return _tc_install_msg or "正在安装陶瓦联机……"
    if not _tc_exe():
        return "首次使用会自动下载陶瓦联机（约 8 MB）"
    if not _tc_running():
        return "陶瓦未启动（点下面的按钮会自动拉起）"
    st = _tc_state()
    if not st:
        return "陶瓦正在启动……"
    state = str(st.get("state", ""))
    text = _tc_exc_text(st) if state == "exception" else TC_STATE_TEXT.get(state, state or "未知状态")
    code = _tc_code(st)
    if code:
        return text + "　联机码：" + code
    return text


def _tc_text2():
    if _tc_install_busy:
        return "下载中，装好会自动打开陶瓦，不用管它。装完点「创建房间」就能用。"
    if not _tc_exe():
        return "本机还没装陶瓦联机。第一次点「创建房间」会自动下载安装（约 8 MB，只会装一次）。"
    st = _tc_state() or {}
    code = _tc_code(st)
    if code:
        return "把这个码发给朋友，让对方在「加入 / 管理房间」里输入即可加入。"
    state = str(st.get("state", ""))
    if state == "host-scanning":
        return "进单人存档 → 按 ESC → 「对局域网开放」→ 点创建局域网世界，陶瓦会自动接管并生成联机码。"
    if state == "host-starting":
        return "已在存档里，正在建立联机通道，稍等几秒。"
    if state == "guest-connecting":
        return "正在连接房主的房间，连上后会写进游戏的多人列表。"
    return "两个人要玩同一个版本的存档，不需要装 Mod，也不用端口映射。"


def _tc_url(st):
    """加入房间成功时 /state 会带 url（备用联机地址），可以直接填进游戏的服务器地址"""
    if not isinstance(st, dict):
        return ""
    v = st.get("url")
    return v.strip() if isinstance(v, str) else ""


# /state 的 exception 带 type：0 加入失败 1 连接断开 2 加入时 EasyTier 崩溃
# 3 建房时 EasyTier 崩溃 4 房间已关闭（退出存档）5 协议错误
TC_EXC_TEXT = {
    0: "加入房间失败（房间已关闭或网络不稳定）",
    1: "房间连接断开（房间已关闭或网络不稳定）",
    2: "加入房间失败（EasyTier 崩溃）",
    3: "创建房间失败（EasyTier 崩溃）",
    4: "房间已关闭（你退出了存档）",
    5: "协议错误（房主返回了异常数据）",
}


def _tc_exc_text(st):
    if not isinstance(st, dict):
        return "联机出错"
    try:
        k = int(st.get("type"))
    except (TypeError, ValueError):
        return "联机出错（公共节点连不上就换个网络试试）"
    return TC_EXC_TEXT.get(k, "联机出错")

def _tc_code(st):
    """联机码在 /state 的 room 字段里（陶瓦自己的网页就是从 r.room 取的）"""
    if not isinstance(st, dict):
        return ""
    v = st.get("room")
    return v.strip() if isinstance(v, str) else ""


def _live_json():
    st = _status_cache["data"]
    if st is None:
        st = _fetch_with_deadline(STATUS_QUERY or SERVER, 12)
        _status_cache["data"] = st
    return json.dumps(_live_values(st), ensure_ascii=False)


def build_page():
    global _status_cache
    now = time.time()
    st = _status_cache["data"]
    # 后台刷新线程持续更新缓存；只有极早期缓存尚未就绪时才现场拉一次兜底
    if st is None:
        st = _fetch_with_deadline(STATUS_QUERY or SERVER, 12)
        _status_cache["data"] = st
        _status_cache["time"] = now
    S = xaml_escape

    # 顶部横幅（logo.png 放在脚本/exe 同目录就会自动显示）
    banner = _banner_block()

    # 服务器状态卡
    # 这里刻意不写真实数字：页面文本恒定，数字由 PCLPatch.dll 每秒按 Tag 改，
    # 这样 PCL 不会因为内容变化而重建整页（折叠、滚动、毛玻璃都保得住）。
    status_card = _card("服务器状态 · " + S(STATUS_HOST),
                        '        <TextBlock Tag="live:on" TextWrapping="Wrap" Margin="0,0,0,4" FontWeight="Bold"\n'
                        '                   Foreground="#67C23A" Visibility="Collapsed" Text="在线" />\n'
                        '        <TextBlock Tag="live:off" TextWrapping="Wrap" Margin="0,0,0,4" FontWeight="Bold"\n'
                        '                   Foreground="#F56C6C" Visibility="Collapsed" Text="离线" />\n'
                        '        <TextBlock Tag="live:motd" TextWrapping="Wrap" Margin="0,0,0,4" ' + _fg() + '\n'
                        '                   Text="正在连接服务器……" />\n'
                        '        <TextBlock Tag="live:info" TextWrapping="Wrap" Margin="0,0,0,12" ' + _fg() + '\n'
                        '                   Text="版本：—　在线：—" />\n'
                        + _launch_block())

    # 在线玩家卡（标题固定，人数和名单在正文里实时更新）
    pl_card = _card("在线玩家",
                    '        <TextBlock Tag="live:plist" TextWrapping="Wrap" ' + _fg() + '\n'
                    '                   Text="正在读取玩家列表……" />\n')

    # 联机卡：按钮带 Tag="act:*"，由 PCLPatch.dll 接管点击，直接驱动本机陶瓦（Terracotta）
    _TC_CARD_XAML = """        <local:MyCheckBox Tag="chk:tcenable" Text="启用陶瓦联机" Checked="True" Margin="0,0,0,8" ToolTip="关掉后主页只显示状态，不再自动拉起陶瓦" />
        <Grid Margin="0,0,0,8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Tag="live:tcpath" TextWrapping="NoWrap" TextTrimming="CharacterEllipsis"
                       VerticalAlignment="Center" Margin="0,0,8,0" Foreground="#FF6B7885" FontSize="12"
                       Text="未找到 terracotta.exe" />
            <local:MyButton Grid.Column="1" Tag="act:tc-pick" Height="30" Margin="0,0,6,0" Padding="10,0,10,0"
                            ColorType="Highlight" Text="浏览" ToolTip="手动选择 terracotta.exe" />
            <local:MyButton Grid.Column="2" Tag="act:tc-detect" Height="30" Padding="10,0,10,0"
                            ColorType="Highlight" Text="自动检测" ToolTip="在 PCL 的陶瓦目录里找 terracotta.exe" />
        </Grid>
        <local:MyTextBox Tag="in:nick" Height="32" Margin="0,0,0,8" FontSize="13"
                         VerticalContentAlignment="Center" Padding="8,0,8,0"
                         HintText="联机昵称（可留空，留空用陶瓦默认昵称）" />
        <TextBlock Tag="live:tc" TextWrapping="Wrap" Margin="0,0,0,8" FontWeight="Bold" @@FG@@
                   Text="正在检查陶瓦……" />
        <Grid Margin="0,0,0,8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="1.4*" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <local:MyTextBox Grid.Column="0" Tag="in:code" HorizontalAlignment="Stretch" Height="32" Margin="0,0,6,0"
                             FontSize="13" VerticalContentAlignment="Center" Padding="8,0,8,0"
                             HintText="U/XXXX-XXXX-XXXX-XXXX" />
            <local:MyButton Grid.Column="1" Tag="act:tc-join|dis:tcenable" HorizontalAlignment="Stretch" Height="32" Margin="0,0,6,0"
                            Padding="10,0,10,0" ColorType="Highlight" Text="加入房间" />
            <local:MyButton Grid.Column="2" Tag="act:tc-copy|dis:tccodeok" HorizontalAlignment="Stretch" Height="32"
                            Padding="10,0,10,0" ColorType="Highlight" Text="复制邀请码" />
        </Grid>
        <Grid Margin="0,0,0,8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <local:MyButton Grid.Column="0" Tag="act:tc-start|dis:tcenable" HorizontalAlignment="Stretch" Height="32" Margin="0,0,4,0" Padding="6,0,6,0" ColorType="Highlight" Text="启动组件" ToolTip="只启动陶瓦，不进房间" />
            <local:MyButton Grid.Column="1" Tag="act:tc-stop|dis:tcrun" HorizontalAlignment="Stretch" Height="32" Margin="0,0,4,0" Padding="6,0,6,0" ColorType="Highlight" Text="停止组件" ToolTip="结束房间并退出陶瓦进程" />
            <local:MyButton Grid.Column="2" Tag="act:tc-host|dis:tcenable" HorizontalAlignment="Stretch" Height="32" Margin="0,0,4,0" Padding="6,0,6,0" ColorType="Highlight" Text="创建房间" ToolTip="启动陶瓦并等待你开放局域网世界" />
            <local:MyButton Grid.Column="3" Tag="act:tc-detach|dis:tcenable" HorizontalAlignment="Stretch" Height="32" Margin="0,0,4,0" Padding="6,0,6,0" ColorType="Highlight" Text="断开房间" ToolTip="只退出房间，陶瓦进程留着" />
            <local:MyButton Grid.Column="4" Tag="act:tc-url|dis:tcurlok" HorizontalAlignment="Stretch" Height="32" Padding="6,0,6,0" ColorType="Highlight" Text="复制连接地址" ToolTip="加入成功后，把连接地址填进游戏的服务器地址" />
        </Grid>
        <Grid Margin="0,0,0,4">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <local:MyCheckBox Grid.Column="0" Tag="chk:tcauto" Text="启动启动器时自动启动" Checked="False" Margin="0,0,6,0" />
            <local:MyCheckBox Grid.Column="1" Tag="chk:tcfill" Text="联机成功后自动填入服务器地址" Checked="False" />
        </Grid>
        <local:MyCheckBox Tag="chk:tcstop" Text="退出启动器时停止陶瓦联机" Checked="False" Margin="0,0,0,8" />
        <TextBlock TextWrapping="Wrap" Foreground="#FF6B7885" FontSize="12"
                   Text="创建房间后，进单人存档按 ESC → 对局域网开放即可生成邀请码；加入成功后点「复制连接地址」填进服务器地址。" />
"""

    link_card = _card("联机 · 陶瓦", _TC_CARD_XAML.replace("@@FG@@", _fg()))

    # QQ 群卡
    qq_rows = []
    for gid, gname in QQ_GROUPS:
        qq_rows.append(
            '        <TextBlock TextWrapping="Wrap" Margin="0,0,0,6" ' + _fg() + '\n'
            '                   Text="群号：' + S(gid) + '　' + S(gname) + '" />\n'
            '        <Grid Margin="0,0,0,12">\n'
            '            <Grid.ColumnDefinitions>\n'
            '                <ColumnDefinition Width="*" />\n'
            '                <ColumnDefinition Width="*" />\n'
            '            </Grid.ColumnDefinitions>\n'
            '            <local:MyButton Grid.Column="0" HorizontalAlignment="Stretch" Margin="0,0,4,0" Height="35" Padding="13,0,13,0" ColorType="Highlight"\n'
            '                        Text="复制' + S(gname) + '号" EventType="复制文本" EventData="' + S(gid) + '" ToolTip="点击复制群号，粘贴到 QQ 搜索即可找到群" />\n'
            '            <local:MyButton Grid.Column="1" HorizontalAlignment="Stretch" Margin="4,0,0,0" Height="35" Padding="13,0,13,0" ColorType="Highlight"\n'
            '                        Text="打开加群页" EventType="打开网页" EventData="https://qun.qq.com/" ToolTip="在浏览器打开 QQ 群官网；如有群邀请链接可替换此地址实现一键加群" />\n'
            '        </Grid>\n'
        )
    qq_card = _card("加入 QQ 群", "".join(qq_rows))

    return banner + status_card + link_card + _versions_card() + pl_card + qq_card + _zan_card() + _author_card() + _about_card()


def _zan_card():
    """赞赏制作者：默认收起，点「展开赞赏码」才显示；点图片在浏览器里放大看。

    折叠没用 MyCard 的 CanSwap（那个属性在预设/联网页面里不生效），
    改成按钮 + 图片显隐，由 PCLPatch.dll 的 act:zan-toggle 直接切换。
    """
    name = ZAN_IMAGE
    path = os.path.join(_app_dir(), name) if not os.path.isabs(name) else name
    if not os.path.isfile(path):
        return ""                     # 没放赞赏码就不显示这张卡
    key = "/zan/" + os.path.basename(path)
    _static_files[key] = path
    _static_files[urllib.parse.quote(key)] = path
    url = "http://127.0.0.1:%d%s" % (HTTP_PORT, urllib.parse.quote(key))
    S = xaml_escape

    inner = ('        <TextBlock TextWrapping="Wrap" Margin="0,0,0,10" ' + _fg() + '\n'
             '                   Text="如果这个启动器让你玩得开心，可以请作者喝杯奶茶。" />\n'
             '        <Grid Margin="0,2,0,10">\n'
             '            <local:MyButton Tag="act:zan-toggle" HorizontalAlignment="Stretch" Height="35"\n'
             '                        Padding="13,0,13,0" ColorType="Highlight"\n'
             '                        Text="展开赞赏码" ToolTip="展开 / 收起赞赏码" />\n'
             '        </Grid>\n'
             '        <local:MyImage Tag="act:zan-open" Width="220" Height="220" HorizontalAlignment="Left"\n'
             '                    Margin="0,0,0,6" Visibility="Collapsed"\n'
             '                    Source="' + S(url) + '" EnableCache="True"\n'
             '                    ToolTip="微信扫码赞赏；点击图片可在浏览器里放大" />\n'
             '        <TextBlock TextWrapping="Wrap" Foreground="#FF6B7885" FontSize="12" Margin="0,2,0,0"\n'
             '                   Text="微信扫码赞赏 · 点图片可放大查看" />\n')
    return _card("赞赏制作者", inner)


def _author_card():
    """联系创作者：QQ 号一键复制 / 一键加好友"""
    S = xaml_escape
    inner = ('        <TextBlock TextWrapping="Wrap" Margin="0,0,0,8" ' + _fg() + '\n'
             '                   Text="QQ：' + S(AUTHOR_QQ) + '" />\n'
             '        <Grid Margin="0,2,0,0">\n'
             '            <local:MyButton HorizontalAlignment="Stretch" Height="35" Padding="13,0,13,0" ColorType="Highlight"\n'
             '                        Text="复制 QQ 号" EventType="复制文本" EventData="' + S(AUTHOR_QQ) + '"\n'
             '                        ToolTip="点击复制，粘贴到 QQ 搜索就能加好友" />\n'
             '        </Grid>\n')
    return _card("联系创作者", inner)

def _about_card():
    """关于：把 README 里的内容放进主页（这是什么 / 做了什么 / 怎么用 / 原理 / 授权 / AI）。"""
    def H(txt):
        return ('        <TextBlock TextWrapping="Wrap" Margin="0,0,0,8" FontWeight="Bold" ' + _fg() + '\n'
                '                   Text="' + txt + '" />\n')

    def P(txt, margin="0,0,0,14", small=False):
        # 注意：Foreground 只能出现一次，否则 XAML 解析会报“重复的特性名称”
        style = ' Foreground="#FF6B7885" FontSize="12"' if small else ' ' + _fg()
        return ('        <TextBlock TextWrapping="Wrap" Margin="' + margin + '"' + style + '\n'
                '                   Text="' + txt + '" />\n')

    inner = ""
    inner += H("这是什么")
    inner += P("给 IMC.RE 服务器做的 PCL2 主页：把服务器实时状态、按版本进服、陶瓦联机都搬进启动器，"
               "打开就能看到，点一下就能进服，不用再开网页。")

    inner += H("功能介绍")
    inner += P("· 服务器状态：在线 / 离线、MOTD、版本、在线人数、延迟，每秒刷新")
    inner += P("· 按版本进服：1.8 ~ 1.21.11 每个版本一行，「正版」连 on.imc.cab，「离线」连 off.imc.cab")
    inner += P("· 联机 · 陶瓦：创建房间 / 复制联机码 / 加入房间 / 关闭联机，全程不弹网页；"
               "本机没装陶瓦时首次使用会自动下载安装")
    inner += P("· 毛玻璃卡片：去掉 PCL 卡片自带的白底，能透出启动器背景图")
    inner += P("· 折叠卡：版本列表、赞赏码都可以点标题 / 按钮收起，页面刷新时折叠状态不会丢", "0,0,0,14")

    inner += H("怎么用")
    inner += P("1. 解压发布包，双击「启动主页服务（无窗口）.vbs」（占 8848 端口，无窗口）")
    inner += P("2. 打开「改版PCL」里的启动器；用你自己的 PCL 就先运行一次「PCL2-主页补丁安装器」")
    inner += P("3. 想开机自启，双击一次「安装开机自启.bat」", "0,0,0,14")

    inner += H("原理")
    inner += P("页面 XAML 本身恒定不变，补丁每 0.5 秒从本机服务的 live.json 取数据，"
               "只改带 live 标记的控件，所以 PCL 不会重建页面，折叠和滚动都不会被打断。"
               "按钮通过 act 标记由补丁接管，直接调用陶瓦的本地接口。", "0,0,0,14")

    inner += H("关于授权")
    inner += P("改版PCL 里的启动器基于官方开源版 PCL2 构建：没有修改 PCL 源码，"
               "只在初始化处注入一次补丁、并替换内置预设的主页。PCL 版权属于其作者。")
    inner += P("主页服务、注入补丁、页面与安装器的源码使用 MIT 授权。", "0,0,0,14")

    inner += H("制作过程中使用的 AI")
    inner += P("· 模型：DeepSeek-V4.1-Flash")
    inner += P("· 工具：DeepSeek Harness（DeepSeek 的编码代理）")
    inner += P("页面 XAML、主页服务（Python）、注入补丁（C#）都在 AI 协作下完成；"
               "需求、测试与验收由人负责。", "0,0,0,14")

    inner += H("反馈")
    inner += P("有问题或想法，欢迎到上面的 QQ 群，或直接加 QQ 177436529 反馈。", "0,0,0,0", True)
    return _card("关于", inner)


class Handler(BaseHTTPRequestHandler):
    def _send_json(self, body):
        payload = body.encode("utf-8")
        try:
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
        except (BrokenPipeError, ConnectionAbortedError, ConnectionResetError):
            pass

    def do_GET(self):
        path = urllib.parse.urlparse(self.path).path
        if path == "/live.json":
            try:
                self._send_json(_live_json())
            except Exception as e:
                self._send_json('{"status":"off","motd":"\u72b6\u6001\u83b7\u53d6\u5931\u8d25","info":"'
                                + xaml_escape(str(e)).replace('"', '') + '","plist":"","tc":"","tc2":""}')
            return
        # 陶瓦联机的动作入口：/?do=tc-host / ?do=tc-join&code=xxx / ?do=tc-stop / ?do=tc-panel
        if path == "/tc":
            q = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            action = (q.get("do") or [""])[0]
            ok = False
            if action == "tc-host":
                ok = _tc_host((q.get("nick") or [""])[0])
            elif action == "tc-join":
                ok = _tc_join((q.get("code") or [""])[0], (q.get("nick") or [""])[0])
            elif action == "tc-detach":
                ok = _tc_detach()
            elif action.startswith("tc-tog:"):
                ok = _tc_toggle(action[7:])
            elif action == "tc-detect":
                ok = _tc_detect()
            elif action == "tc-start":
                ok = _tc_start()
            elif action == "tc-stopcomp":
                ok = _tc_detach()
            elif action == "tc-setpath":
                ok = _tc_setpath((q.get("path") or [""])[0])
            elif action == "tc-fillnow":
                ok = _tc_fillnow()
            elif action == "tc-autostart":
                ok = _tc_autostart()
            elif action == "tc-autostop":
                ok = _tc_autostop()
            elif action == "tc-stop":
                ok = _tc_stop()
            elif action == "tc-panel":
                ok = _tc_start()
            st = _tc_state() or {}
            self._send_json(json.dumps({
                "ok": ok,
                "state": st,
                "code": _tc_code(st),
                "url": _tc_url(st),
                "panel": _tc_open_url() or "",
                "echo": action,
            }, ensure_ascii=False))
            return
        if self._serve_static():
            return
        try:
            payload = build_page().encode("utf-8")
        except Exception as e:
            payload = ("<local:MyCard Title=\"错误\"><StackPanel Margin=\"25,40,23,15\">"
                       "<TextBlock TextWrapping=\"Wrap\" Text=\"主页生成失败：" + xaml_escape(str(e)) + "\" />"
                       "</StackPanel></local:MyCard>").encode("utf-8")
        try:
            self.send_response(200)
            self.send_header("Content-Type", "text/plain; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
        except (BrokenPipeError, ConnectionAbortedError, ConnectionResetError):
            pass  # 客户端（PCL2）提前断开连接，属正常现象，静默忽略

    def _serve_static(self):
        """返回顶部横幅等已登记的图片；不是图片请求则返回 False，继续走主页生成"""
        path = urllib.parse.urlparse(self.path).path
        target = _static_files.get(path)
        if not target:
            return False
        try:
            with open(target, "rb") as f:
                data = f.read()
        except OSError:
            self.send_response(404)
            self.end_headers()
            return True
        ctype = {".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg",
                 ".gif": "image/gif", ".webp": "image/webp", ".svg": "image/svg+xml"}.get(
                     os.path.splitext(target)[1].lower(), "application/octet-stream")
        try:
            self.send_response(200)
            self.send_header("Content-Type", ctype)
            self.send_header("Cache-Control", "public, max-age=3600")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        except (BrokenPipeError, ConnectionAbortedError, ConnectionResetError):
            pass
        return True

    def log_message(self, *args):
        pass


def main():
    print("=" * 56)
    print("  PCL2 本地实时主页服务（后台模式）")
    print(f"  服务器：{SERVER}")
    print(f"  主页地址：http://127.0.0.1:{HTTP_PORT}/motd.xaml")
    print("=" * 56)
    try:
        srv = ThreadingHTTPServer(("127.0.0.1", HTTP_PORT), Handler)
    except OSError as e:
        # 端口被占用 = 服务已在后台运行，静默退出（无窗口模式看不到报错）
        if getattr(e, "winerror", None) == 10048 or getattr(e, "errno", None) == 98:
            print(f"检测到服务已在后台运行（端口 {HTTP_PORT} 已被占用），无需重复启动。")
            sys.exit(0)
        print("启动失败：", e)
        print(f"端口 {HTTP_PORT} 可能被占用，请修改脚本顶部的 HTTP_PORT 后重试。")
        sys.exit(1)
    print(f"  服务运行中（后台模式），每 {REFRESH_INTERVAL} 秒自动刷新在线人数与延迟。")
    print("  停止服务：运行同目录下的 stop.bat，或在任务管理器中结束进程。")
    threading.Thread(target=_background_refresh, daemon=True).start()
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        print("已停止服务。")


if __name__ == "__main__":
    main()
#（注：内容由AI生成）



