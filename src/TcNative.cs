using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Threading;

namespace PCLPatch
{
    /// <summary>
    /// 陶瓦联机的原生实现（不再依赖 PCL主页服务 那个 Python 进程）：
    /// 启动/停止组件、创建房间、加入房间、断开房间、状态与邀请码。
    /// </summary>
    public static class TcNative
    {
        public static Action<string> LogHook;

        private static void Log(string msg)
        {
            try { if (LogHook != null) LogHook("tc: " + msg); } catch { }
        }

        public static string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCL", "Terracotta");
        public static string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCL", "Terracotta", "terracotta-home.json");

        private static Process _proc;
        private static int _port;
        private static readonly object _lock = new object();
        private static string _lastPortScan = "";

        public static bool Enable = true;
        public static bool AutoStart;
        public static bool AutoFill;
        public static bool AutoStop;
        public static string Nick = "";
        public static string CustomPath = "";
        private static bool _loaded;

        // ---------------- 设置 ----------------

        public static void LoadSettings()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(SettingsPath)) return;
                string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                Enable = Bool(json, "enable", true);
                AutoStart = Bool(json, "autostart", false);
                AutoStop = Bool(json, "autostop", false);
                AutoFill = Bool(json, "autofill", false);
                Nick = Str(json, "nick", "");
                CustomPath = Str(json, "path", "");
            }
            catch { }
        }

        public static void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                Directory.CreateDirectory(Dir);
                string json = "{\"enable\":" + (Enable ? "true" : "false")
                    + ",\"autostart\":" + (AutoStart ? "true" : "false")
                    + ",\"autostop\":" + (AutoStop ? "true" : "false")
                    + ",\"autofill\":" + (AutoFill ? "true" : "false")
                    + ",\"nick\":\"" + Esc(Nick) + "\",\"path\":\"" + Esc(CustomPath) + "\"}";
                File.WriteAllText(SettingsPath, json, Encoding.UTF8);
            }
            catch { }
        }

        private static bool Bool(string json, string key, bool def)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)");
            if (!m.Success) return def;
            return m.Groups[1].Value == "true";
        }

        private static string Str(string json, string key, string def)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : def;
        }

        private static string Esc(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ---------------- 程序路径 ----------------

        public static string ExePath()
        {
            LoadSettings();
            if (!string.IsNullOrEmpty(CustomPath) && File.Exists(CustomPath)) return CustomPath;
            try
            {
                if (!Directory.Exists(Dir)) return null;
                string best = null;
                foreach (string sub in Directory.GetDirectories(Dir))
                {
                    string exe = Path.Combine(sub, "terracotta.exe");
                    if (File.Exists(exe))
                    {
                        if (best == null || string.CompareOrdinal(Path.GetFileName(sub), Path.GetFileName(Path.GetDirectoryName(best))) > 0)
                            best = exe;
                    }
                }
                if (best != null) return best;
            }
            catch { }
            return null;
        }

        // ---------------- 进程 ----------------

        public static bool Running()
        {
            lock (_lock)
            {
                if (_proc != null && !_proc.HasExited) return true;
            }
            // 不是我们启动的（比如 PCL 自己拉起来的）也认
            try
            {
                Process[] all = Process.GetProcessesByName("terracotta");
                return all.Length > 0;
            }
            catch { return false; }
        }

        /// <summary>端口未知时，从陶瓦自己的日志里找 "Rocket has launched from http://127.0.0.1:PORT"。</summary>
        private static void DiscoverPort()
        {
            if (_port != 0) return;
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "terracotta");
                if (!Directory.Exists(dir)) { Log("no terracotta temp dir"); return; }
                string[] logs = Directory.GetFiles(dir, "application.log", SearchOption.AllDirectories);
                Log("logs found=" + logs.Length + " dir=" + dir);
                Array.Sort(logs, delegate(string a, string b) { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
                int checkedCount = 0;
                foreach (string log in logs)
                {
                    if (checkedCount++ >= 12) break;
                    try
                    {
                        string text;
                        using (FileStream fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                            text = sr.ReadToEnd();
                        Log("log " + Path.GetFileName(Path.GetDirectoryName(log)) + " len=" + text.Length);
                        Match m = Regex.Match(text, @"http://127\.0\.0\.1:(\d+)");
                        if (m.Success) { _port = int.Parse(m.Groups[1].Value); Log("port=" + _port + " from " + log); return; }
                    }
                    catch (Exception ex) { Log("read log error: " + ex.Message); }
                }
            }
            catch (Exception ex) { Log("discover error: " + ex.Message); }
        }

        public static bool Start()
        {
            lock (_lock)
            {
                if (_proc != null && !_proc.HasExited) return true;
                string exe = ExePath();
                if (exe == null) return false;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(exe, "--hmcl2 --hmcl");
                    psi.WorkingDirectory = Path.GetDirectoryName(exe);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    Process p = new Process();
                    p.StartInfo = psi;
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        Match m = Regex.Match(e.Data, @"http://127\.0\.0\.1:(\d+)");
                        if (m.Success) _port = int.Parse(m.Groups[1].Value);
                    };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    _proc = p;
                    _port = 0;
                    for (int i = 0; i < 60 && _port == 0; i++) Thread.Sleep(100);
                    return _port != 0 || !p.HasExited;
                }
                catch { return false; }
            }
        }

        public static bool Stop()
        {
            lock (_lock)
            {
                try
                {
                    if (_port != 0) Http("/state/ide", 1500);
                    if (_proc != null && !_proc.HasExited) { _proc.Kill(); _proc.WaitForExit(3000); }
                }
                catch { }
                try
                {
                    foreach (Process p in Process.GetProcessesByName("terracotta"))
                    {
                        try { p.Kill(); } catch { }
                    }
                }
                catch { }
                _proc = null;
                _port = 0;
                return true;
            }
        }

        // ---------------- HTTP ----------------

        private static string Http(string path, int timeout)
        {
            if (_port == 0) return null;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + _port + path);
                req.Timeout = timeout;
                req.ReadWriteTimeout = timeout;
                req.Proxy = null;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch { return null; }
        }

        private static Dictionary<string, string> _state = new Dictionary<string, string>();
        private static DateTime _stateTime = DateTime.MinValue;

        public static Dictionary<string, string> State()
        {
            if (!Running()) return new Dictionary<string, string>();
            DiscoverPort();
            DiscoverPortByPid();
            if ((DateTime.Now - _stateTime).TotalMilliseconds < 500) return _state;
            string json = Http("/state", 800);
            Log("state http port=" + _port + " json=" + (json == null ? "null" : json.Substring(0, Math.Min(120, json.Length))));
            Dictionary<string, string> d = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(json))
            {
                foreach (Match m in Regex.Matches(json, "\"([a-zA-Z0-9_]+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                    d[m.Groups[1].Value] = Regex.Unescape(m.Groups[2].Value);
                foreach (Match m in Regex.Matches(json, "\"([a-zA-Z0-9_]+)\"\\s*:\\s*(\\d+)"))
                    if (!d.ContainsKey(m.Groups[1].Value)) d[m.Groups[1].Value] = m.Groups[2].Value;
            }
            _state = d;
            _stateTime = DateTime.Now;
            return d;
        }

        private static string Field(string key)
        {
            string v;
            return State().TryGetValue(key, out v) ? v : "";
        }

        public static string Code()
        {
            string c = Field("room");
            if (!string.IsNullOrEmpty(c)) return c;
            string url = Field("url");
            if (!string.IsNullOrEmpty(url) && url.Contains("="))
            {
                string part = url.Substring(url.IndexOf('=') + 1);
                return part;
            }
            return "";
        }

        public static string Url() { return Field("url"); }

        public static string StateText()
        {
            if (!Enable) return "已关闭（勾上「启用陶瓦联机」才能用）";
            if (ExePath() == null) return "本机还没装陶瓦联机";
            if (!Running()) return "陶瓦未启动（点下面的按钮会自动拉起）";
            string state = Field("state");
            string text;
            switch (state)
            {
                case "waiting": text = "空闲"; break;
                case "host-scanning": text = "等待你开放局域网世界"; break;
                case "host-starting": text = "正在建立联机通道……"; break;
                case "host-ok": text = "房间已就绪"; break;
                case "guest-connecting": text = "正在连接房间……"; break;
                case "guest-starting": text = "正在启动联机……"; break;
                case "guest-ok": text = "已加入房间"; break;
                case "exception": text = "出错：" + Field("type"); break;
                default: text = string.IsNullOrEmpty(state) ? "正在检查陶瓦……" : state;
                    break;
            }
            string code = Code();
            if (!string.IsNullOrEmpty(code)) text += "　联机码：" + code;
            return text;
        }

        // ---------------- 动作 ----------------

        public static bool Host()
        {
            if (!Enable) return false;
            if (!Start()) return false;
            Http("/state/scanning?player=" + Uri.EscapeDataString(Nick), 3000);
            return true;
        }

        public static bool Join(string code)
        {
            if (!Enable || string.IsNullOrEmpty(code)) return false;
            if (!Start()) return false;
            Http("/state/guesting?room=" + Uri.EscapeDataString(code) + "&player=" + Uri.EscapeDataString(Nick), 3000);
            return true;
        }

        public static bool Detach()
        {
            if (!Running()) return false;
            Http("/state/ide", 2000);
            return true;
        }

        // ---------------- 开关（主页复选框） ----------------

        public static void Toggle(string key)
        {
            LoadSettings();
            switch (key)
            {
                case "tcenable": Enable = !Enable; break;
                case "tcauto": AutoStart = !AutoStart; break;
                case "tcfill": AutoFill = !AutoFill; break;
                case "tcstop": AutoStop = !AutoStop; break;
            }
            SaveSettings();
        }

        // ---------------- 首次自动安装 ----------------

        public const string Version = "0.4.2";
        public const string DownloadUrl = "https://github.com/burningtnt/Terracotta/releases/download/v0.4.2/terracotta-0.4.2-windows-x86_64-pkg.tar.gz";

        public static bool EnsureInstalled()
        {
            if (ExePath() != null) return true;
            try
            {
                string temp = Path.Combine(Path.GetTempPath(), "PCL-Terracotta-dl");
                Directory.CreateDirectory(temp);
                string pkg = Path.Combine(temp, "tc.tar.gz");
                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "PCL-IMC");
                    wc.DownloadFile(DownloadUrl, pkg);
                }
                string target = Path.Combine(Dir, Version);
                Directory.CreateDirectory(target);
                ProcessStartInfo psi = new ProcessStartInfo("tar.exe", "-xzf \"" + pkg + "\" -C \"" + temp + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);
                p.WaitForExit(60000);
                // 解压出来的文件拷进目标目录
                foreach (string f in Directory.GetFiles(temp, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(f);
                    if (name.EndsWith(".tar.gz")) continue;
                    string dst = Path.Combine(target, name);
                    try { File.Copy(f, dst, true); } catch { }
                }
                try { Directory.Delete(temp, true); } catch { }
                return File.Exists(Path.Combine(target, "terracotta.exe"));
            }
            catch { return false; }
        }

        // ---------------- servers.dat（自动填入服务器地址） ----------------

        public static int PutServer(string name, string ip)
        {
            int n = 0;
            foreach (string file in FindServersDat())
            {
                try
                {
                    Dictionary<string, string> entries = ReadServersDat(file);
                    entries[name] = ip;
                    WriteServersDat(file, entries);
                    n++;
                }
                catch { }
            }
            return n;
        }

        private static IEnumerable<string> FindServersDat()
        {
            List<string> list = new List<string>();
            List<string> roots = new List<string>();
            try
            {
                string app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
                if (Directory.Exists(app)) roots.Add(app);
            }
            catch { }
            try
            {
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".minecraft");
                if (Directory.Exists(local)) roots.Add(local);
            }
            catch { }
            foreach (string root in roots)
            {
                string baseFile = Path.Combine(root, "servers.dat");
                if (File.Exists(baseFile)) list.Add(baseFile);
                string versions = Path.Combine(root, "versions");
                if (!Directory.Exists(versions)) continue;
                try
                {
                    foreach (string ver in Directory.GetDirectories(versions))
                    {
                        string f = Path.Combine(ver, "servers.dat");
                        if (File.Exists(f)) list.Add(f);
                    }
                }
                catch { }
            }
            return list;
        }

        private static Dictionary<string, string> ReadServersDat(string file)
        {
            Dictionary<string, string> dict = new Dictionary<string, string>();
            try
            {
                byte[] data = File.ReadAllBytes(file);
                int pos = 0;
                int type = data[pos++];
                pos = SkipString(data, pos);
                if (type != 10) return dict;
                while (true)
                {
                    int tag = data[pos++];
                    if (tag == 0) break;
                    string name;
                    pos = ReadString(data, pos, out name);
                    if (tag == 9 && name == "servers")
                    {
                        int elemType = data[pos++];
                        int len = ReadInt(data, ref pos);
                        for (int i = 0; i < len; i++)
                        {
                            if (data[pos] != 10) { pos = SkipPayload(data, pos, data[pos]); continue; }
                            pos++;
                            string entryName = "", entryIp = "";
                            while (true)
                            {
                                int t = data[pos++];
                                if (t == 0) break;
                                string key;
                                pos = ReadString(data, pos, out key);
                                if (t == 8)
                                {
                                    string val;
                                    pos = ReadString(data, pos, out val);
                                    if (key == "name") entryName = val;
                                    else if (key == "ip") entryIp = val;
                                }
                                else pos = SkipPayload(data, pos, t);
                            }
                            if (entryName != "") dict[entryName] = entryIp;
                        }
                    }
                    else pos = SkipPayload(data, pos, tag);
                }
            }
            catch { }
            return dict;
        }

        private static void WriteServersDat(string file, Dictionary<string, string> entries)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                WriteByte(ms, 10);
                WriteStr(ms, "");
                WriteByte(ms, 9);
                WriteStr(ms, "servers");
                WriteByte(ms, 10);
                WriteInt(ms, entries.Count);
                foreach (KeyValuePair<string, string> kv in entries)
                {
                    WriteByte(ms, 8); WriteStr(ms, "name"); WriteStr(ms, kv.Key);
                    WriteByte(ms, 8); WriteStr(ms, "ip"); WriteStr(ms, kv.Value);
                    WriteByte(ms, 0);
                }
                WriteByte(ms, 0);
                File.WriteAllBytes(file, ms.ToArray());
            }
        }

        private static void WriteByte(Stream s, int b) { s.WriteByte((byte)b); }
        private static void WriteStr(Stream s, string v)
        {
            byte[] data = Encoding.UTF8.GetBytes(v ?? "");
            s.WriteByte((byte)(data.Length >> 8));
            s.WriteByte((byte)(data.Length & 0xFF));
            s.Write(data, 0, data.Length);
        }
        private static void WriteInt(Stream s, int v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }

        private static int ReadInt(byte[] d, ref int pos)
        {
            int v = (d[pos] << 24) | (d[pos + 1] << 16) | (d[pos + 2] << 8) | d[pos + 3];
            pos += 4;
            return v;
        }

        private static int SkipString(byte[] d, int pos)
        {
            string s;
            return ReadString(d, pos, out s);
        }

        private static int ReadString(byte[] d, int pos, out string value)
        {
            int len = (d[pos] << 8) | d[pos + 1];
            pos += 2;
            value = Encoding.UTF8.GetString(d, pos, len);
            return pos + len;
        }

        private static int SkipPayload(byte[] d, int pos, int tag)
        {
            switch (tag)
            {
                case 1: return pos + 1;
                case 2: return pos + 2;
                case 3: return pos + 4;
                case 4: return pos + 8;
                case 5: return pos + 4;
                case 6: return pos + 8;
                case 7: return pos + ReadInt(d, ref pos) + pos - pos;
                case 8: { string s; return ReadString(d, pos, out s); }
                case 9:
                    {
                        int elem = d[pos++];
                        int len = ReadInt(d, ref pos);
                        for (int i = 0; i < len; i++) pos = SkipPayload(d, pos, elem);
                        return pos;
                    }
                case 10:
                    {
                        while (true)
                        {
                            int t = d[pos++];
                            if (t == 0) return pos;
                            string key;
                            pos = ReadString(d, pos, out key);
                            pos = SkipPayload(d, pos, t);
                        }
                    }
                case 11: { int len = ReadInt(d, ref pos); return pos + len * 4; }
                case 12: { int len = ReadInt(d, ref pos); return pos + len * 8; }
                default: return pos;
            }
        }

        public static int FillServerNow()
        {
            string url = Url();
            if (string.IsNullOrEmpty(url)) return 0;
            return PutServer("陶瓦联机", url);
        }


        // ---------------- 端口发现（按进程 PID 查 TCP 监听表） ----------------

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint dwState;
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwRemoteAddr;
            public uint dwRemotePort;
            public uint dwOwningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

        private static List<int> ListenPortsOf( HashSet<int> pids )
        {
            List<int> ports = new List<int>();
            try
            {
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
                if (size <= 4) return ports;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(buf, ref size, false, 2, 3, 0) != 0) return ports;
                    int count = Marshal.ReadInt32(buf);
                    IntPtr rowPtr = (IntPtr)((long)buf + 4);
                    int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                    for (int i = 0; i < count; i++)
                    {
                        MIB_TCPROW_OWNER_PID row = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure((IntPtr)((long)rowPtr + i * rowSize), typeof(MIB_TCPROW_OWNER_PID));
                        if (!pids.Contains((int)row.dwOwningPid)) continue;
                        int port = (int)(((row.dwLocalPort & 0xFF) << 8) | ((row.dwLocalPort >> 8) & 0xFF));
                        if (port > 0) ports.Add(port);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { }
            return ports;
        }

        private static void DiscoverPortByPid()
        {
            if (_port != 0) return;
            try
            {
                HashSet<int> pids = new HashSet<int>();
                foreach (Process p in Process.GetProcessesByName("terracotta")) pids.Add(p.Id);
                if (pids.Count == 0) return;
                foreach (int port in ListenPortsOf(pids))
                {
                    int old = _port;
                    _port = port;
                    string json = Http("/state", 600);
                    if (json != null)
                    {
                        Log("port found by pid=" + port);
                        return;
                    }
                    _port = old;
                }
            }
            catch { }
        }

    }
}
