using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PCLPatch
{
    /// <summary>
    /// 不依赖本机 Python 服务的一组原生实现：
    /// - 服务器状态（Minecraft Server List Ping，直连协议）
    /// - 内存优化（SeProfileSingleProcessPrivilege + NtSetSystemInformation）
    /// 这样「imc.re主页」预设只需要一个 exe。
    /// </summary>
    public static class ImcNative
    {
        public static string StatusHost = "play.imc.re";
        public static string ServerOffline = "off.imc.cab";
        public static string ServerPremium = "on.imc.cab";
        public static int ServerPort = 25565;

        private static string _memText = "";
        private static volatile bool _memBusy;
        private static readonly object _lock = new object();

        // ---------------- 服务器状态 ----------------

        public class ServerStatus
        {
            public bool Online;
            public string Motd = "";
            public string Version = "";
            public int OnlinePlayers;
            public int MaxPlayers = -1;
            public List<string> Players = new List<string>();
            public int Latency = -1;
        }

        public static ServerStatus Query(string host, int port)
        {
            ServerStatus st = new ServerStatus();
            Socket sock = null;
            DateTime start = DateTime.Now;
            try
            {
                IPAddress[] addrs = Dns.GetHostAddresses(host);
                if (addrs.Length == 0) return st;
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                IAsyncResult ar = sock.BeginConnect(addrs[0], port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(2500)) return st;
                sock.EndConnect(ar);
                NetworkStream ns = new NetworkStream(sock);

                // handshake
                List<byte> hs = new List<byte>();
                WriteVarInt(hs, 0);
                WriteVarInt(hs, 47);
                WriteString(hs, host);
                hs.Add((byte)(port >> 8));
                hs.Add((byte)(port & 0xFF));
                WriteVarInt(hs, 1);
                WritePacket(ns, hs);

                // status request
                List<byte> req = new List<byte>();
                WriteVarInt(req, 0);
                WritePacket(ns, req);

                int len = ReadVarInt(ns);
                if (len <= 0) return st;
                List<byte> payload = new List<byte>();
                for (int i = 0; i < len; i++)
                {
                    int b = ns.ReadByte();
                    if (b < 0) return st;
                    payload.Add((byte)b);
                }
                st.Latency = (int)(DateTime.Now - start).TotalMilliseconds;
                // payload: varint packetId, string json
                int idx = 0;
                ReadVarInt(payload, ref idx);
                string json = ReadString(payload, ref idx);
                st.Online = true;
                ParseStatus(json, st);
                return st;
            }
            catch
            {
                return st;
            }
            finally
            {
                try { if (sock != null) sock.Close(); } catch { }
            }
        }

        private static readonly System.Web.Script.Serialization.JavaScriptSerializer _js =
            new System.Web.Script.Serialization.JavaScriptSerializer();

        private static void ParseStatus(string json, ServerStatus st)
        {
            try
            {
                Dictionary<string, object> root = _js.Deserialize<Dictionary<string, object>>(json);
                object desc;
                if (root.TryGetValue("description", out desc)) st.Motd = TextOf(desc);
                object ver;
                if (root.TryGetValue("version", out ver))
                {
                    Dictionary<string, object> v = ver as Dictionary<string, object>;
                    if (v != null)
                    {
                        object name;
                        if (v.TryGetValue("name", out name) && name != null) st.Version = name.ToString();
                    }
                }
                object players;
                if (root.TryGetValue("players", out players))
                {
                    Dictionary<string, object> p = players as Dictionary<string, object>;
                    if (p != null)
                    {
                        object on, max, sample;
                        if (p.TryGetValue("online", out on) && on != null) int.TryParse(on.ToString(), out st.OnlinePlayers);
                        if (p.TryGetValue("max", out max) && max != null) int.TryParse(max.ToString(), out st.MaxPlayers);
                        if (p.TryGetValue("sample", out sample))
                        {
                            System.Collections.IEnumerable arr = sample as System.Collections.IEnumerable;
                            if (arr != null)
                            {
                                foreach (object it in arr)
                                {
                                    Dictionary<string, object> d = it as Dictionary<string, object>;
                                    if (d != null)
                                    {
                                        object nm;
                                        if (d.TryGetValue("name", out nm) && nm != null) st.Players.Add(nm.ToString());
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>把 description（字符串或 {text,extra}）拼成纯文本，并去掉 § 颜色代码。</summary>
        private static string TextOf(object node)
        {
            StringBuilder sb = new StringBuilder();
            CollectTextNode(node, sb);
            string text = sb.ToString();
            text = Regex.Replace(text, "\u00a7.", "");
            return text.Trim();
        }

        private static void CollectTextNode(object node, StringBuilder sb)
        {
            if (node == null) return;
            string s = node as string;
            if (s != null) { sb.Append(s); return; }
            Dictionary<string, object> d = node as Dictionary<string, object>;
            if (d == null)
            {
                System.Collections.IEnumerable arr = node as System.Collections.IEnumerable;
                if (arr != null) foreach (object it in arr) CollectTextNode(it, sb);
                return;
            }
            object text;
            if (d.TryGetValue("text", out text) && text != null) sb.Append(text.ToString());
            object extra;
            if (d.TryGetValue("extra", out extra)) CollectTextNode(extra, sb);
        }

        // ---------------- 极简 JSON 工具（只够解析服务器返回） ----------------

        private static string JsonRaw(string json, string key)
        {
            if (json == null) return null;
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + pat.Length);
            if (i < 0) return null;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return null;
            int end = i;
            if (json[i] == '"')
            {
                end = i + 1;
                while (end < json.Length)
                {
                    if (json[end] == '\\') { end += 2; continue; }
                    if (json[end] == '"') { end++; break; }
                    end++;
                }
            }
            else if (json[i] == '{' || json[i] == '[')
            {
                char open = json[i];
                char close = open == '{' ? '}' : ']';
                int depth = 0;
                end = i;
                while (end < json.Length)
                {
                    char c = json[end];
                    if (c == '"') { end++; while (end < json.Length && json[end] != '"') { if (json[end] == '\\') end++; end++; } }
                    else if (c == open) depth++;
                    else if (c == close) { depth--; if (depth == 0) { end++; break; } }
                    end++;
                    if (end >= json.Length) break;
                }
            }
            else
            {
                while (end < json.Length && ",}]".IndexOf(json[end]) < 0) end++;
            }
            return json.Substring(i, end - i);
        }

        private static string JsonField(string json, string key)
        {
            string v = JsonRaw(json, key);
            if (v == null) return "";
            if (v.StartsWith("\"")) return Unescape(v.Substring(1, v.Length - 2));
            return v;
        }

        private static string JsonNumber(string json, string key)
        {
            string v = JsonRaw(json, key);
            return v == null ? "" : v.Trim();
        }

        private static string JsonObject(string json, string key)
        {
            string v = JsonRaw(json, key);
            if (v == null || !v.StartsWith("{")) return null;
            return v;
        }

        private static string JsonArray(string json, string key)
        {
            string v = JsonRaw(json, key);
            if (v == null || !v.StartsWith("[")) return null;
            return v;
        }

        /// <summary>description 可能是字符串，也可能是 {"text":..,"extra":[..]}，这里拼成纯文本。</summary>
        private static string JsonText(string json, string key)
        {
            string v = JsonRaw(json, key);
            if (v == null) return "";
            if (v.StartsWith("\"")) return Unescape(v.Substring(1, v.Length - 2));
            StringBuilder sb = new StringBuilder();
            CollectText(v, sb);
            return sb.ToString();
        }

        private static void CollectText(string json, StringBuilder sb)
        {
            string text = JsonField(json, "text");
            if (!string.IsNullOrEmpty(text)) sb.Append(text);
            int idx = 0;
            string extra = JsonArray(json, "extra");
            if (extra != null)
            {
                foreach (string part in SplitArray(extra))
                {
                    if (part.StartsWith("\"")) sb.Append(Unescape(part.Substring(1, part.Length - 2)));
                    else CollectText(part, sb);
                }
            }
        }

        private static IEnumerable<string> SplitArray(string arr)
        {
            int depth = 0, start = 1;
            bool inStr = false;
            for (int i = 1; i < arr.Length - 1; i++)
            {
                char c = arr[i];
                if (inStr)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    yield return arr.Substring(start, i - start);
                    start = i + 1;
                }
            }
            if (start < arr.Length - 1) yield return arr.Substring(start, arr.Length - 1 - start);
        }

        private static IEnumerable<string> JsonArrayStrings(string arr, string key)
        {
            foreach (string part in SplitArray(arr))
            {
                string v = JsonField(part, key);
                if (!string.IsNullOrEmpty(v)) yield return v;
            }
        }

        private static string Unescape(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'u' && i + 4 < s.Length)
                    {
                        try { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); } catch { }
                        i += 4;
                    }
                    else sb.Append(n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------------- 协议读写 ----------------

        private static void WriteVarInt(List<byte> buf, int value)
        {
            uint v = (uint)value;
            do
            {
                byte b = (byte)(v & 0x7F);
                v >>= 7;
                if (v != 0) b |= 0x80;
                buf.Add(b);
            } while (v != 0);
        }

        private static void WriteString(List<byte> buf, string s)
        {
            byte[] data = Encoding.UTF8.GetBytes(s);
            WriteVarInt(buf, data.Length);
            buf.AddRange(data);
        }

        private static void WritePacket(NetworkStream ns, List<byte> payload)
        {
            List<byte> head = new List<byte>();
            WriteVarInt(head, payload.Count);
            head.AddRange(payload);
            ns.Write(head.ToArray(), 0, head.Count);
            ns.Flush();
        }

        private static int ReadVarInt(NetworkStream ns)
        {
            int result = 0, shift = 0;
            while (true)
            {
                int b = ns.ReadByte();
                if (b < 0) return -1;
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift > 35) return -1;
            }
        }

        private static int ReadVarInt(List<byte> buf, ref int idx)
        {
            int result = 0, shift = 0;
            while (idx < buf.Count)
            {
                int b = buf[idx++];
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
            }
            return -1;
        }

        private static string ReadString(List<byte> buf, ref int idx)
        {
            int len = ReadVarInt(buf, ref idx);
            if (len <= 0 || idx + len > buf.Count) return "";
            string s = Encoding.UTF8.GetString(buf.GetRange(idx, len).ToArray());
            idx += len;
            return s;
        }

        // ---------------- 组装 live.json ----------------

        public static string BuildLiveJson()
        {
            ServerStatus st = Query(StatusHost, ServerPort);
            string status = st.Online ? "on" : "off";
            string motd, info, plist;
            if (st.Online)
            {
                motd = "MOTD：" + st.Motd;
                info = "版本：" + (string.IsNullOrEmpty(st.Version) ? "—" : st.Version)
                       + "　在线：" + st.OnlinePlayers + (st.MaxPlayers >= 0 ? "/" + st.MaxPlayers : "")
                       + "　延迟：" + st.Latency + "ms";
                // 很多服务器在 sample 里塞的是装饰性文字（还带 § 颜色码），不是真玩家，统一按“未公开”处理
                plist = "服务器未公开在线玩家列表。";
            }
            else
            {
                motd = "MOTD：　（服务器当前不可达）";
                info = "版本：—　在线：—　延迟：—";
                plist = "服务器离线，无法获取玩家列表。";
            }
            Dictionary<string, string> d = new Dictionary<string, string>();
            d["status"] = status;
            d["motd"] = motd;
            d["info"] = info;
            d["plist"] = plist;
            d["memtext"] = string.IsNullOrEmpty(_memText) ? "点一下「内存优化」释放被缓存占住的物理内存" : _memText;
            // 陶瓦联机
            TcNative.LoadSettings();
            string tcCode = TcNative.Code();
            string tcUrl = TcNative.Url();
            d["tc"] = TcNative.StateText();
            d["tc2"] = "";
            d["tccode"] = tcCode;
            d["tcurl"] = tcUrl;
            d["tccodeok"] = string.IsNullOrEmpty(tcCode) ? "0" : "1";
            d["tcurlok"] = string.IsNullOrEmpty(tcUrl) ? "0" : "1";
            d["tcrun"] = TcNative.Running() ? "1" : "0";
            d["tcenable"] = TcNative.Enable ? "1" : "0";
            d["tcauto"] = TcNative.AutoStart ? "1" : "0";
            d["tcfill"] = TcNative.AutoFill ? "1" : "0";
            d["tcstop"] = TcNative.AutoStop ? "1" : "0";
            d["tcpath"] = TcNative.ExePath() ?? "未找到 terracotta.exe";
            StringBuilder sb = new StringBuilder("{");
            bool first = true;
            foreach (KeyValuePair<string, string> kv in d)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kv.Key).Append("\":\"").Append(Escape(kv.Value)).Append('"');
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // ---------------- 内存优化 ----------------

        [StructLayout(LayoutKind.Sequential)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privileges; }

        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr prev, IntPtr retLen);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        private static ulong AvailableMemory()
        {
            try
            {
                MEMORYSTATUSEX m = new MEMORYSTATUSEX();
                m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(m)) return m.ullAvailPhys;
            }
            catch { }
            return 0;
        }

        private static string Fmt(ulong bytes)
        {
            double mb = bytes / 1048576.0;
            if (mb >= 1024) return (mb / 1024.0).ToString("0.0") + " GB";
            return ((int)Math.Round(mb)) + " MB";
        }

        private static bool EnablePrivilege(string name)
        {
            try
            {
                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out token)) return false;
                LUID luid;
                if (!LookupPrivilegeValue(null, name, out luid)) { CloseHandle(token); return false; }
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Privileges.Luid = luid;
                tp.Privileges.Attributes = 0x2;
                bool ok = AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                int err = Marshal.GetLastWin32Error();
                CloseHandle(token);
                return ok && err == 0;
            }
            catch { return false; }
        }

        private static int Purge()
        {
            int done = 0;
            try
            {
                foreach (int cmd in new int[] { 2, 3, 4 })
                {
                    int v = cmd;
                    int st = NtSetSystemInformation(0x50, ref v, 4);
                    if (st == 0) done++;
                }
            }
            catch { }
            return done;
        }

        public static void MemOptimize(Action<string> onDone)
        {
            lock (_lock)
            {
                if (_memBusy) return;
                _memBusy = true;
            }
            _memText = "正在优化内存……";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                try
                {
                    ulong before = AvailableMemory();
                    bool priv = EnablePrivilege("SeProfileSingleProcessPrivilege");
                    int purged = priv ? Purge() : 0;
                    Thread.Sleep(500);
                    ulong after = AvailableMemory();
                    if (purged > 0) text = "已释放 " + Fmt(after > before ? after - before : 0) + "（可用 " + Fmt(before) + " → " + Fmt(after) + "）";
                    else text = "优化失败：需要管理员权限";
                }
                catch (Exception ex)
                {
                    text = "优化失败：" + ex.Message;
                }
                _memText = text;
                lock (_lock) { _memBusy = false; }
                if (onDone != null) onDone(text);
            });
        }

        public static string MemText { get { return _memText; } }
    }
}
