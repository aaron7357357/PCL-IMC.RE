using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace PCLPatch
{
    /// <summary>
    /// PCL2 鑷畾涔変富椤碉紙鑱旂綉鏇存柊锛夎嚜鍔ㄥ埛鏂拌ˉ涓併€?    /// 鍚庡彴涓嬭浇 鈫?鍐呭鍙樺寲 鈫?UI 绾跨▼鍘熷湴鏇存柊鏂囨湰锛岄伩鍏嶆暣椤甸噸寤洪€犳垚鐨勯棯鐑併€?    /// </summary>
    public static class Bootstrap
    {
        private sealed class State
        {
            public object Page;
            public MethodInfo LoadContent;
            public MethodInfo SettingsGet;
            public MethodInfo ArgumentReplace;
            public MethodInfo GetObjectFromXML;
            public FieldInfo PanCustom;
            public DispatcherTimer Timer;
            public int Busy;
            public string Last;
            public string LogPath;
            public int Applied;
            public int Merged;
            public int Rebuilt;
            public bool MergeBroken;
            public bool Styled;
            public int LiveBusy;
            public string LiveUrl;
            public string LiveBase;
            public string LiveLast;
        }

        private static readonly Dictionary<object, State> States = new Dictionary<object, State>();
        private static readonly object Gate = new object();

        private const string XamlPrefix =
            "<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:sys=\"clr-namespace:System;assembly=mscorlib\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
            "xmlns:local=\"clr-namespace:PCL;assembly=Plain Craft Launcher 2\" " +
            "xmlns:core=\"clr-namespace:MeloongCore;assembly=MeloongCore\" " +
            "xmlns:corewpf=\"clr-namespace:MeloongCore.Wpf;assembly=MeloongCore.Wpf\">";

        public static void Install(object page)
        {
            try
            {
                if (page == null) return;
                Type type = page.GetType();
                if (type.FullName != "PCL.PageLaunchRight") return;

                lock (Gate)
                {
                    if (States.ContainsKey(page)) return;

                    State st = new State();
                    st.Page = page;
                    st.LoadContent = type.GetMethod("LoadContent", BindingFlags.Instance | BindingFlags.NonPublic);
                    st.SettingsGet = FindSettingsGet(type.Assembly);
                    st.ArgumentReplace = FindMethod(type.Assembly, "PCL.ModMain", "ArgumentReplace", 3, typeof(string));
                    st.GetObjectFromXML = FindMethod(type.Assembly, "PCL.ModBase", "GetObjectFromXML", 1, typeof(string));
                    st.PanCustom = type.GetField("_PanCustom", BindingFlags.Instance | BindingFlags.NonPublic);
                    st.LogPath = Path.Combine(AppDir(), "PCL", "PCLPatch.log");
                    States[page] = st;

                    int ms = ReadIntervalMs();
                    Log(st, "install: interval=" + ms + "ms, LoadContent=" + (st.LoadContent != null)
                        + ", SettingsGet=" + (st.SettingsGet != null)
                        + ", ArgumentReplace=" + (st.ArgumentReplace != null)
                        + ", GetObjectFromXML=" + (st.GetObjectFromXML != null)
                        + ", PanCustom=" + (st.PanCustom != null));
                    if (ms <= 0 || st.LoadContent == null || st.SettingsGet == null) return;

                    st.Timer = new DispatcherTimer(DispatcherPriority.Background, ((DispatcherObject)page).Dispatcher);
                    st.Timer.Interval = TimeSpan.FromMilliseconds(ms);
                    st.Timer.Tick += delegate { OnTick(st); };
                    st.Timer.Start();
                }
            }
            catch (Exception ex)
            {
                TryLog("install failed: " + ex);
            }
        }

        private static MethodInfo FindSettingsGet(Assembly asm)
        {
            Type settings = asm.GetType("PCL.Settings");
            if (settings == null) return null;
            foreach (MethodInfo m in settings.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "Get" || m.IsGenericMethodDefinition) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 2 && ps[0].ParameterType == typeof(string)) return m;
            }
            return null;
        }

        private static MethodInfo FindMethod(Assembly asm, string typeName, string methodName, int paramCount, Type firstParam)
        {
            try
            {
                Type t = asm.GetType(typeName);
                if (t == null) return null;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != methodName) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == paramCount && ps[0].ParameterType == firstParam) return m;
                }
            }
            catch { }
            return null;
        }

        private static object GetSetting(State st, string key)
        {
            try { return st.SettingsGet.Invoke(null, new object[] { key, null }); }
            catch { return null; }
        }

        private static void OnTick(State st)
        {
            try
            {
                if (st.Page == null || !((UIElement)st.Page).IsVisible) return;

                // 毛玻璃处理要在任何模式（联网 / 本地文件 / 预设）下都跑一遍，
                // 否则切到预设模式时卡片会露出 PCL 自带的白色底。
                // 注意不能只做一次：按 F5 刷新主页后 PCL 会重建控件，新实例又带回白底，
                // 所以每次 tick 都过一遍（函数内部只对“还没处理过”的控件赋值，开销很小）。
                st.Styled = StyleGlassCards(st);

                // 实时数据：三种模式都跑。页面本身恒定，只有带 Tag="live:*" 的控件改文字。
                StartLive(st);

                object typeValue = GetSetting(st, "UiCustomType");
                if (!(typeValue is int) || (int)typeValue != 2) return;
                object urlValue = GetSetting(st, "UiCustomNet");
                string url = urlValue as string;
                if (string.IsNullOrEmpty(url) || url.Trim().Length == 0) return;
                if (Interlocked.CompareExchange(ref st.Busy, 1, 0) != 0) return;
                ThreadPool.QueueUserWorkItem(delegate { FetchApply(st, url.Trim()); });
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref st.Busy, 0);
                Log(st, "tick error: " + ex.Message);
            }
        }

        /// <summary>
        /// 实时数据通道：页面本身保持恒定（PCL 不会重建），
        /// 只有带 Tag="live:xxx" 的控件由这里按 live.json 改文字 / 显隐。
        /// 预设模式下页面是写死在 exe 里的，也一样能刷。
        /// </summary>
        private static void StartLive(State st)
        {
            string url = DeriveLiveUrl(st);
            if (url == null) return;
            if (Interlocked.CompareExchange(ref st.LiveBusy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate { LiveFetch(st, url); });
        }

        private static string DeriveLiveUrl(State st)
        {
            if (st.LiveUrl != null) return st.LiveUrl;
            object v = GetSetting(st, "UiCustomNet");
            string s = v as string;
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            int cut = s.LastIndexOf('/');
            if (cut <= 7) return null;
            st.LiveBase = s.Substring(0, cut + 1);
            st.LiveUrl = st.LiveBase + "live.json";
            return st.LiveUrl;
        }

        private static void LiveFetch(State st, string url)
        {
            try
            {
                string text = Fetch(url);
                if (string.IsNullOrEmpty(text)) return;
                st.LiveLast = text;
                // 每次都套用：按 F5 后 PCL 会重建控件，新控件需要重新填一遍实时值
                ((DispatcherObject)st.Page).Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(delegate { ApplyLive(st, text); }));
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref st.LiveBusy, 0);
            }
        }

        private static void ApplyLive(State st, string json)
        {
            try
            {
                Dictionary<string, string> d = ParseFlatJson(json);
                if (d == null || d.Count == 0) return;
                Panel host = st.PanCustom.GetValue(st.Page) as Panel;
                if (host == null) return;
                string status;
                if (!d.TryGetValue("status", out status)) status = "";
                WalkLive(host, d, status);
            }
            catch (Exception ex)
            {
                Log(st, "live error: " + ex.Message);
            }
        }

        private static void WalkLive(DependencyObject el, Dictionary<string, string> d, string status)
        {
            if (el == null) return;
            FrameworkElement fe = el as FrameworkElement;
            if (fe != null)
            {
                string tag = fe.Tag as string;
                if (tag != null && tag.StartsWith("act:", StringComparison.Ordinal))
                {
                    HookAction(fe, tag);
                }
                else if (tag != null && tag.StartsWith("in:", StringComparison.Ordinal))
                {
                    HookInput(fe, tag);
                }
                else if (tag != null && tag.StartsWith("live:", StringComparison.Ordinal))
                {
                    string key = tag.Substring(5);
                    if (key == "on" || key == "off")
                    {
                        bool show = (key == "on") ? (status == "on") : (status == "off");
                        Visibility want = show ? Visibility.Visible : Visibility.Collapsed;
                        if (fe.Visibility != want) fe.Visibility = want;
                    }
                    else
                    {
                        string val;
                        if (d.TryGetValue(key, out val) && val != null)
                        {
                            TextBlock tb = fe as TextBlock;
                            if (tb != null && tb.Text != val) tb.Text = val;
                        }
                    }
                }
            }
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(el);
            for (int i = 0; i < n; i++)
                WalkLive(System.Windows.Media.VisualTreeHelper.GetChild(el, i), d, status);
        }

        /// <summary>只解析一层 { "key": "value", ... } 的小 JSON（服务端也是我们自己写的）</summary>
        private static Dictionary<string, string> ParseFlatJson(string s)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            int i = 0;
            while (i < s.Length)
            {
                int q = s.IndexOf('"', i);
                if (q < 0) break;
                int q2 = FindStringEnd(s, q + 1);
                if (q2 < 0) break;
                string key = Unescape(s.Substring(q + 1, q2 - q - 1));
                int c = s.IndexOf(':', q2);
                if (c < 0) break;
                int j = c + 1;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                if (j >= s.Length) break;
                if (s[j] == '"')
                {
                    int e = FindStringEnd(s, j + 1);
                    if (e < 0) break;
                    d[key] = Unescape(s.Substring(j + 1, e - j - 1));
                    i = e + 1;
                }
                else
                {
                    int e = j;
                    while (e < s.Length && s[e] != ',' && s[e] != '}') e++;
                    d[key] = s.Substring(j, e - j).Trim();
                    i = e;
                }
            }
            return d;
        }

        private static int FindStringEnd(string s, int from)
        {
            for (int i = from; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '"') return i;
            }
            return -1;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char n = s[++i];
                if (n == 'n') sb.Append('\n');
                else if (n == 'r') sb.Append('\r');
                else if (n == 't') sb.Append('\t');
                else if (n == 'u' && i + 4 < s.Length)
                {
                    int code;
                    if (int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber,
                                     null, out code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                    else sb.Append('u');
                }
                else sb.Append(n);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 主页上的动作按钮（Tag="act:xxx"）：PCL 的自定义主页事件表达不了“驱动本机陶瓦”，
        /// 所以这里自己接管点击，请求本地服务的 /tc 接口。
        /// </summary>
        private static void HookAction(FrameworkElement fe, string tag)
        {
            if (fe is ButtonBase) return;
            if (!ActionHooked.Add(fe)) return;
            string action = tag.Substring(4);
            MouseButtonEventHandler mb = delegate(object s, MouseButtonEventArgs e)
            {
                try { RunAction(fe, action); }
                catch (Exception ex) { Log(null, "action error: " + ex.Message); }
            };
            fe.AddHandler(UIElement.MouseLeftButtonUpEvent, mb, true);
        }

        private static readonly HashSet<object> ActionHooked = new HashSet<object>();

        /// <summary>
        /// 主页上的输入框（Tag="in:xxx"）：记下引用，点按钮时直接读它的文字。
        /// 这样加入房间不用跳网页，在主页里填联机码就行。
        /// </summary>
        private static void HookInput(FrameworkElement fe, string tag)
        {
            TextBox tb = fe as TextBox;
            if (tb == null) return;
            lock (Inputs) Inputs[tag.Substring(3)] = tb;
        }

        private static readonly Dictionary<string, TextBox> Inputs = new Dictionary<string, TextBox>();

        private static string ReadInput(string key)
        {
            lock (Inputs)
            {
                TextBox tb;
                if (!Inputs.TryGetValue(key, out tb) || tb == null) return null;
                return tb.Text == null ? null : tb.Text.Trim();
            }
        }

        private static void RunAction(FrameworkElement fe, string action)
        {
            State st = FindState(fe);
            string baseUrl = (st != null && st.LiveBase != null) ? st.LiveBase : "http://127.0.0.1:8848/";
            string url = baseUrl + "tc?do=" + action;
            if (action == "tc-join")
            {
                string code = ReadInput("code");
                if (string.IsNullOrEmpty(code))
                {
                    Log(st, "action: tc-join 没有填联机码");
                    return;
                }
                url += "&code=" + Uri.EscapeDataString(code);
            }
            else if (action == "tc-copy")
            {
                // 复制联机码：直接从实时数据里拿，不用问服务
                if (st != null && st.LiveLast != null)
                {
                    string c = ExtractJsonString(st.LiveLast, "tccode");
                    if (!string.IsNullOrEmpty(c))
                    {
                        try { Clipboard.SetText(c); } catch { }
                        Log(st, "action: 已复制联机码 " + c);
                        return;
                    }
                }
                Log(st, "action: 还没有联机码可复制");
                return;
            }
            Log(st, "action: " + action + (url.IndexOf("code=") >= 0 ? " " + url.Substring(url.IndexOf("code=")) : ""));
            // 网络请求放到后台线程，别把 UI 卡住（创建房间要等陶瓦启动，可能好几秒）
            ThreadPool.QueueUserWorkItem(delegate
            {
                string resp = null;
                try { resp = Fetch(url); }
                catch (Exception ex) { Log(st, "action fetch error: " + ex.Message); }
                Log(st, "action result: " + (resp == null ? "(null)" : resp));
                string panel = ExtractJsonString(resp, "panel");
                string code = ExtractJsonString(resp, "code");
                if (st != null) st.LiveLast = null;   // 让下一次 tick 立刻回填新状态
                if (st == null)
                {
                    if (!string.IsNullOrEmpty(panel)) { try { System.Diagnostics.Process.Start(panel); } catch { } }
                    return;
                }
                ((DispatcherObject)st.Page).Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(delegate
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(code)) Clipboard.SetText(code);
                            if (action == "tc-panel" && !string.IsNullOrEmpty(panel))
                                System.Diagnostics.Process.Start(panel);
                        }
                        catch (Exception ex) { Log(st, "action ui error: " + ex.Message); }
                    }));
            });
        }

        private static State FindState(FrameworkElement fe)
        {
            foreach (KeyValuePair<object, State> kv in States)
            {
                Panel p = kv.Value.PanCustom == null ? null : kv.Value.PanCustom.GetValue(kv.Value.Page) as Panel;
                if (p == null) continue;
                if (IsDescendant(p, fe)) return kv.Value;
            }
            return null;
        }

        private static bool IsDescendant(DependencyObject root, DependencyObject target)
        {
            if (root == null || target == null) return false;
            DependencyObject cur = target;
            for (int guard = 0; guard < 200 && cur != null; guard++)
            {
                if (ReferenceEquals(cur, root)) return true;
                cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Dictionary<string, string> d = ParseFlatJson(json);
            string v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        private static void FetchApply(State st, string url)
        {
            try
            {
                string text = Fetch(url);
                if (string.IsNullOrEmpty(text)) return;
                if (text == st.Last) return;
                st.Last = text;
                ((DispatcherObject)st.Page).Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(delegate { Apply(st, text); }));
            }
            catch (Exception ex)
            {
                Log(st, "fetch error: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref st.Busy, 0);
            }
        }

        private static void Apply(State st, string text)
        {
            try
            {
                if (st.Page == null || !((UIElement)st.Page).IsVisible) return;

                // 每次都尝试原地合并：合并成功时控件实例保持不变，
                // 折叠起来的卡片（MyCard.IsSwapped）就不会被刷新重置。
                // 只有结构真的变了（比如服务器离线/上线、装了新版本）才整页重建，
                // 重建后下一次刷新继续尝试合并，不会像以前那样一次失败就永久放弃。
                bool merged = TryMerge(st, text);
                if (!merged && st.Rebuilt < 3)
                    Log(st, "本次结构变化，整页重建一次（下次仍尝试原地合并）");

                if (merged)
                {
                    st.Merged++;
                }
                else
                {
                    st.Rebuilt++;
                    st.LoadContent.Invoke(st.Page, new object[] { text });
                }

                st.Styled = StyleGlassCards(st);
                st.Applied++;
                if (st.Applied <= 10 || st.Applied % 60 == 0)
                    Log(st, "refresh #" + st.Applied + " len=" + text.Length + " merged=" + st.Merged + " rebuilt=" + st.Rebuilt);
            }
            catch (Exception ex)
            {
                try { st.LoadContent.Invoke(st.Page, new object[] { text }); } catch { }
                Log(st, "apply error: " + ex.Message);
            }
        }

        /// <summary>
        /// 瑙ｆ瀽鏂板唴瀹瑰苟涓庣幇鏈夌晫闈㈤€愯妭鐐瑰姣旓紝鍙洿鏂板彉鍖栫殑鏂囨湰锛屼繚鎸佹帶浠跺疄渚嬩笉鍙樸€?        /// </summary>
        private static bool TryMerge(State st, string raw)
        {
            if (st.PanCustom == null || st.ArgumentReplace == null || st.GetObjectFromXML == null) return false;

            Panel host = st.PanCustom.GetValue(st.Page) as Panel;
            if (host == null) { Log(st, "merge: PanCustom is not Panel"); return false; }
            if (host.Children.Count != 1) { Log(st, "merge: host children = " + host.Children.Count); return false; }

            object newRoot = BuildTree(st, raw);
            if (newRoot == null) { Log(st, "merge: BuildTree returned null"); return false; }

            string why;
            if (!MergeElement(host.Children[0], newRoot, out why))
            {
                if (st.Merged == 0) Log(st, "merge failed: " + why);
                return false;
            }
            return true;
        }

        private static object BuildTree(State st, string raw)
        {
            try
            {
                string content = raw;
                Func<string, string> escape = XmlEscape;
                content = (string)st.ArgumentReplace.Invoke(null, new object[] { content, escape, true });

                int guard = 0;
                while (content.Contains("xmlns") && guard++ < 20)
                {
                    content = Regex.Replace(content, "xmlns[^\"']*(\"|')[^\"']*(\"|')", "")
                                   .Replace("xmlns", "");
                }
                content = XamlPrefix + content + "</StackPanel>";
                return st.GetObjectFromXML.Invoke(null, new object[] { content });
            }
            catch (Exception ex)
            {
                Log(st, "build tree error: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return null;
            }
        }

        private static string XmlEscape(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static bool MergeElement(object oldEl, object newEl, out string why)
        {
            why = "";
            if (oldEl == null || newEl == null) { why = "null element"; return false; }
            if (oldEl.GetType() != newEl.GetType())
            {
                why = "type " + oldEl.GetType().Name + " vs " + newEl.GetType().Name;
                return false;
            }

            if (!SameStringProperty(oldEl, newEl, "Text", out why)) return false;
            if (!SameStringProperty(oldEl, newEl, "Title", out why)) return false;

            // 折叠卡片（PCL.MyCard）保留原来的实例，只同步标题：
            // 这样用户点开的折叠 / 展开状态不会被每秒刷新重置。
            // （新建的元素还没进可视化树，子元素是空的，没法逐节点比对，所以直接跳过内部）
            if (oldEl.GetType().FullName == "PCL.MyCard") return true;

            Panel op = oldEl as Panel;
            Panel np = newEl as Panel;
            if (op != null || np != null)
            {
                if (op == null || np == null) { why = "panel mismatch"; return false; }
                if (op.Children.Count != np.Children.Count)
                {
                    why = op.GetType().Name + " children " + op.Children.Count + " vs " + np.Children.Count;
                    return false;
                }
                for (int i = 0; i < op.Children.Count; i++)
                    if (!MergeElement(op.Children[i], np.Children[i], out why)) return false;
            }

            ContentControl oc = oldEl as ContentControl;
            ContentControl nc = newEl as ContentControl;
            if (oc != null || nc != null)
            {
                if (oc == null || nc == null) { why = "contentcontrol mismatch"; return false; }
                if ((oc.Content == null) != (nc.Content == null)) { why = "content null mismatch"; return false; }
                if (oc.Content != null && !MergeElement(oc.Content, nc.Content, out why)) return false;
            }

            Decorator od = oldEl as Decorator;
            Decorator nd = newEl as Decorator;
            if (od != null || nd != null)
            {
                if (od == null || nd == null) { why = "decorator mismatch"; return false; }
                if ((od.Child == null) != (nd.Child == null)) { why = "decorator child null mismatch"; return false; }
                if (od.Child != null && !MergeElement(od.Child, nd.Child, out why)) return false;
            }

            return true;
        }

        /// <summary>
        /// 把带 Tag="glass" 的原生折叠卡（MyCard）的白底去掉，让它露出外层毛玻璃。
        /// MyCard 的白底画在内部 MainBorder 上，直接把它清空即可。
        /// </summary>
        private static bool StyleGlassCards(State st)
        {
            try
            {
                if (st.PanCustom == null) return false;
                Panel host = st.PanCustom.GetValue(st.Page) as Panel;
                if (host == null) return false;
                bool touched = false;
                foreach (object child in host.Children)
                    StripCardBackground(child as DependencyObject, ref touched);
                return touched;
            }
            catch { return false; }
        }

        private static void StripCardBackground(DependencyObject el, ref bool touched)
        {
            if (el == null) return;
            try
            {
                object obj = el;
                if (obj.GetType().FullName == "PCL.MyCard")
                {
                    FrameworkElement fe = obj as FrameworkElement;
                    if (fe != null && (fe.Tag as string) == "glass")
                    {
                        FieldInfo fi = obj.GetType().GetField("MainBorder",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        Border border = fi == null ? null : fi.GetValue(obj) as Border;
                        if (border != null && border.Background != null)
                        {
                            border.Background = null;
                            touched = true;
                        }
                        // 关掉悬停动画：否则鼠标移上去标题和箭头会变蓝、阴影也会被染成蓝色，
                        // 透过半透明卡片看就是整块蓝
                        PropertyInfo anim = obj.GetType().GetProperty("HasMouseAnimation");
                        if (anim != null && anim.CanWrite && (bool)anim.GetValue(obj, null))
                        {
                            anim.SetValue(obj, false, null);
                            touched = true;
                        }
                        // 藏掉卡片的阴影层：白底去掉后这层阴影会直接盖在毛玻璃上
                        FieldInfo cf = obj.GetType().GetField("_MainChrome",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        UIElement chrome = cf == null ? null : cf.GetValue(obj) as UIElement;
                        if (chrome != null && chrome.Visibility != Visibility.Collapsed)
                        {
                            chrome.Visibility = Visibility.Collapsed;
                            touched = true;
                        }
                    }
                }
                int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(el);
                for (int i = 0; i < count; i++)
                    StripCardBackground(System.Windows.Media.VisualTreeHelper.GetChild(el, i), ref touched);
            }
            catch { }
        }

        private static bool SameStringProperty(object oldEl, object newEl, string name, out string why)
        {
            why = "";
            try
            {
                PropertyInfo pi = oldEl.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (pi == null || pi.PropertyType != typeof(string) || !pi.CanWrite) return true;
                string a = pi.GetValue(oldEl, null) as string;
                string b = pi.GetValue(newEl, null) as string;
                if (a != b) pi.SetValue(oldEl, b, null);
                return true;
            }
            catch (Exception ex)
            {
                why = "set " + name + " failed: " + ex.Message;
                return false;
            }
        }

        private static string Fetch(string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 5000;
            req.ReadWriteTimeout = 5000;
            req.Proxy = null;
            req.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            req.Headers.Add("Cache-Control", "no-cache");
            req.UserAgent = "PCL2-Patch/1.0";
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream stream = resp.GetResponseStream())
            {
                Encoding enc = Encoding.UTF8;
                try
                {
                    if (!string.IsNullOrEmpty(resp.CharacterSet)) enc = Encoding.GetEncoding(resp.CharacterSet);
                }
                catch { }
                using (StreamReader reader = new StreamReader(stream, enc))
                    return reader.ReadToEnd();
            }
        }

        private static string AppDir()
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private static int ReadIntervalMs()
        {
            const int DefaultMs = 1000;
            try
            {
                string path = Path.Combine(AppDir(), "PCL", "HomepageRefresh.txt");
                if (!File.Exists(path)) return DefaultMs;
                string raw = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (raw.Length == 0 || raw.StartsWith("#") || raw.StartsWith("//")) return DefaultMs;
                int i = 0;
                while (i < raw.Length && (char.IsDigit(raw[i]) || raw[i] == '.')) i++;
                if (i == 0) return DefaultMs;
                double num = double.Parse(raw.Substring(0, i), System.Globalization.CultureInfo.InvariantCulture);
                string unit = raw.Substring(i).Trim().ToLowerInvariant();
                double ms;
                if (unit.StartsWith("ms")) ms = num;
                else if (unit.StartsWith("s")) ms = num * 1000.0;
                else if (unit.StartsWith("m")) ms = num * 60000.0;
                else ms = num >= 100 ? num : num * 1000.0;
                if (ms < 100) ms = 100;
                if (ms > 3600000) ms = 3600000;
                return (int)ms;
            }
            catch { return DefaultMs; }
        }

        private static void Log(State st, string msg)
        {
            if (st == null || st.LogPath == null) return;
            try
            {
                FileInfo fi = new FileInfo(st.LogPath);
                if (fi.Exists && fi.Length > 262144)
                {
                    string old = st.LogPath + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    fi.MoveTo(old);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(st.LogPath));
                File.AppendAllText(st.LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        private static void TryLog(string msg)
        {
            try
            {
                string path = Path.Combine(AppDir(), "PCL", "PCLPatch.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n", Encoding.UTF8);
            }
            catch { }
        }
    }
}



