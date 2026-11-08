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
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            public int LiveWalkLog;
            public int TickLog;
            public int ImgLog;
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
                    LoadWindowIcon();   // 先准备好窗口图标，tick 里再套上去
                    AutoTerracotta();   // 开了「启动启动器时自动启动」就把陶瓦拉起来
                    // ShowSplash(st);  // 加载画面已改为直接替换 exe 内嵌图标，这里不再需要
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

        private static Window Splash;

        /// <summary>启动闪屏：主窗口出来之前先显示图标，主窗口可见后自动关掉</summary>
        private static void ShowSplash(State st)
        {
            try
            {
                Application app = Application.Current;
                if (app == null || WindowIcon == null) return;
                app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send,
                    new Action(delegate
                    {
                        try
                        {
                            if (Splash != null) return;
                            Window w = new Window();
                            w.WindowStyle = WindowStyle.None;
                            w.ResizeMode = ResizeMode.NoResize;
                            w.AllowsTransparency = false;
                            w.Background = new SolidColorBrush(Color.FromRgb(30, 42, 56));
                            w.ShowInTaskbar = false;
                            w.Topmost = true;
                            w.Width = 320;
                            w.Height = 210;
                            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

                            Border box = new Border();
                            box.CornerRadius = new CornerRadius(14);
                            box.Background = new SolidColorBrush(Color.FromArgb(240, 30, 42, 56));
                            box.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
                            box.BorderThickness = new Thickness(1);

                            StackPanel sp = new StackPanel();
                            sp.VerticalAlignment = VerticalAlignment.Center;
                            sp.HorizontalAlignment = HorizontalAlignment.Center;

                            Image img = new Image();
                            img.Source = WindowIcon;
                            img.Width = 96;
                            img.Height = 96;
                            img.Margin = new Thickness(0, 0, 0, 10);
                            sp.Children.Add(img);

                            TextBlock t1 = new TextBlock();
                            t1.Text = "PCL IMC.RE";
                            t1.FontSize = 20;
                            t1.FontWeight = FontWeights.Bold;
                            t1.Foreground = Brushes.White;
                            t1.HorizontalAlignment = HorizontalAlignment.Center;
                            sp.Children.Add(t1);

                            TextBlock t2 = new TextBlock();
                            t2.Text = "正在启动…";
                            t2.FontSize = 12;
                            t2.Margin = new Thickness(0, 4, 0, 0);
                            t2.Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255));
                            t2.HorizontalAlignment = HorizontalAlignment.Center;
                            sp.Children.Add(t2);

                            box.Child = sp;
                            w.Content = box;
                            w.Show();
                            w.Activate();
                            Splash = w;
                            SplashStart = DateTime.Now;
                            Log(st, "splash shown");
                        }
                        catch (Exception ex) { Log(st, "splash error: " + ex.Message); }
                    }));
            }
            catch { }
        }

        private static void SwapLogoImage(Window w, string name)
        {
            try
            {
                Image img = FindImageByName(w, name);
                if (img == null) return;
                if (ReferenceEquals(img.Source, WindowIcon)) return;
                img.Source = WindowIcon;
                img.Stretch = Stretch.Uniform;
                Log(null, "logo swapped: " + name);
            }
            catch { }
        }

        private static Image FindImageByName(DependencyObject el, string name)
        {
            if (el == null) return null;
            Image img = el as Image;
            if (img != null && img.Name == name) return img;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(el);
            for (int i = 0; i < n; i++)
            {
                Image r = FindImageByName(System.Windows.Media.VisualTreeHelper.GetChild(el, i), name);
                if (r != null) return r;
            }
            return null;
        }

        private static DateTime SplashStart = DateTime.MinValue;

        /// <summary>主页内容出来后再收掉闪屏（PCL 启动时主窗口会先显示加载图标，
        /// 那时还不能关，否则会闪一下旧图标）</summary>
        private static void HideSplashIfReady(State st)
        {
            try
            {
                if (Splash == null) return;
                bool ready = false;
                try
                {
                    if (st != null && st.Page != null && st.PanCustom != null)
                    {
                        Panel host = st.PanCustom.GetValue(st.Page) as Panel;
                        if (host != null && host.Children.Count > 0) ready = true;
                    }
                }
                catch { }
                double age = (DateTime.Now - SplashStart).TotalSeconds;
                if (age < 1.2) return;
                if (!ready && age < 12) return;
                Splash.Close();
                Splash = null;
                Log(st, "splash closed");
            }
            catch { }
        }

        /// <summary>把窗口左上角 / 任务栏图标换成本项目的图标（PCL 的窗口图标来自 WPF 内嵌资源，
        /// 改 exe 资源不会生效，只能在运行时替换）</summary>
        private const string HomeBase = "http://127.0.0.1:8848/";

        /// <summary>启动器启动时：按设置把陶瓦拉起来（服务端自己判断开关）</summary>
        private static void AutoTerracotta()
        {
            try
            {
                AppDomain.CurrentDomain.ProcessExit += delegate
                {
                    try { Fetch(HomeBase + "tc?do=tc-autostop"); } catch { }
                };
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { Thread.Sleep(1500); Fetch(HomeBase + "tc?do=tc-autostart"); }
                    catch { }
                });
            }
            catch { }
        }

        private static void ApplyWindowIcon()
        {
            try
            {
                if (WindowIcon == null) return;
                Window w = Application.Current == null ? null : Application.Current.MainWindow;
                if (w == null) return;
                if (!ReferenceEquals(w.Icon, WindowIcon)) w.Icon = WindowIcon;
                // PCL 启动加载画面显示的是标题 Logo 图片，一起换掉
                SwapLogoImage(w, "ImgTitle");
                SwapLogoImage(w, "ImageTitleLogo");
            }
            catch { }
        }

        private static System.Windows.Media.ImageSource WindowIcon;

        private static void LoadWindowIcon()
        {
            try
            {
                Stream s = typeof(Bootstrap).Assembly.GetManifestResourceStream("PCLPatch.icon.ico");
                if (s == null) return;
                using (s)
                {
                    System.Drawing.Icon ico = new System.Drawing.Icon(s, 256, 256);
                    WindowIcon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        ico.Handle, Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    ico.Dispose();
                }
            }
            catch { }
        }

        private static void OnTick(State st)
        {
            try
            {
                if (st.TickLog < 3) { st.TickLog++; Log(st, "tick#" + st.TickLog + " page=" + (st.Page != null) + " vis=" + (st.Page != null && ((UIElement)st.Page).IsVisible)); }
                HideSplashIfReady(st);
                if (st.ImgLog < 2) { st.ImgLog++; DumpImages(st); }
                ApplyWindowIcon();
                // 左侧栏注入已关闭：PCL 左边栏是它自己的 Grid，硬插会把「版本选择」挤出可视区，
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
                // live 通道已经在按 live.json 改文字了。如果这里再拿整页 XAML 合并一遍，
                // 会把刚写进去的实时数据又覆盖回占位文字（页面里写死的是“正在连接服务器……”），
                // 表现就是主页一直停在占位状态。有 live 通道时就不跑整页刷新。
                if (st.LiveUrl != null) return;
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
            // 预设模式下 UiCustomNet 可能没被填（或者被 PCL 清空），
            // 这时直接退回本地服务的固定地址，保证 live.json 这条通道永远能连上
            if (string.IsNullOrEmpty(s)) s = "http://127.0.0.1:8848/motd.xaml";
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
                if (st.LiveWalkLog < 2) { st.LiveWalkLog++; Log(st, "live fetch " + url + " len=" + (text == null ? -1 : text.Length)); }
                if (string.IsNullOrEmpty(text)) return;
                st.LiveLast = text;
                // 每次都套用：按 F5 后 PCL 会重建控件，新控件需要重新填一遍实时值
                ((DispatcherObject)st.Page).Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(delegate { ApplyLive(st, text); }));
            }
            catch (Exception ex) { Log(st, "live fetch error: " + ex.Message); }
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
                if (host == null) { Log(st, "live: PanCustom is not Panel"); return; }
                string status;
                if (!d.TryGetValue("status", out status)) status = "";
                int touched = WalkLive(host, d, status);
                // 左侧栏那行小字跟着「内存优化」的实时状态走
                try
                {
                    string memText;
                    if (MemLabel != null && d.TryGetValue("memtext", out memText)) MemLabel.Text = memText;
                }
                catch { }
                if (st.LiveWalkLog < 3)
                {
                    st.LiveWalkLog++;
                    Log(st, "live walk: host=" + (host == null ? "null" : host.GetType().Name)
                        + " nodes=" + touched + " keys=" + d.Count);
                }
            }
            catch (Exception ex)
            {
                Log(st, "live error: " + ex.Message);
            }
        }

        /// <summary>调试用：把可视树里所有 Image 的来源和尺寸打出来，找出 PCL 启动加载画面的那张图</summary>
        private static void DumpImages(State st)
        {
            try
            {
                Window w = Application.Current == null ? null : Application.Current.MainWindow;
                if (w == null) { Log(st, "img: no main window"); return; }
                DumpImagesWalk(st, w);
            }
            catch (Exception ex) { Log(st, "img error: " + ex.Message); }
        }

        private static void DumpImagesWalk(State st, DependencyObject el)
        {
            if (el == null) return;
            Image img = el as Image;
            if (img != null)
            {
                string uri = "null";
                if (img.Source != null)
                {
                    uri = img.Source.GetType().Name;
                    BitmapImage bi = img.Source as BitmapImage;
                    if (bi != null && bi.UriSource != null) uri += " " + bi.UriSource.ToString();
                    BitmapFrame bf = img.Source as BitmapFrame;
                    if (bf != null) uri += " frame " + (bf.Decoder == null ? "" : bf.Decoder.ToString());
                    if (img.Source != null) uri += " w=" + (int)img.Source.Width;
                }
                Log(st, "img: " + uri + "  " + (int)img.ActualWidth + "x" + (int)img.ActualHeight
                     + " name=" + (img.Name == null ? "" : img.Name));
            }
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(el);
            for (int i = 0; i < n; i++)
                DumpImagesWalk(st, System.Windows.Media.VisualTreeHelper.GetChild(el, i));
        }

        private static int WalkLive(DependencyObject el, Dictionary<string, string> d, string status)
        {
            if (el == null) return 0;
            int hit = 0;
            FrameworkElement fe = el as FrameworkElement;
            if (fe != null)
            {
                string tag = fe.Tag as string;
                // Tag 可以用 | 组合多条指令，例如 "act:tc-stop|dis:tcrun"
                string actPart = null, inPart = null, livePart = null, chkPart = null;
                if (tag != null && tag.IndexOf('|') >= 0)
                {
                    foreach (string part in tag.Split('|'))
                    {
                        if (part.StartsWith("act:", StringComparison.Ordinal)) actPart = part;
                        else if (part.StartsWith("in:", StringComparison.Ordinal)) inPart = part;
                        else if (part.StartsWith("live:", StringComparison.Ordinal)) livePart = part;
                        else if (part.StartsWith("chk:", StringComparison.Ordinal)) chkPart = part;
                        else if (part.StartsWith("dis:", StringComparison.Ordinal))
                        {
                            // 按键值把控件置灰：data 里对应项为 "1" 才可用
                            string dk = part.Substring(4);
                            string dv;
                            bool on = d.TryGetValue(dk, out dv) && dv == "1";
                            if (fe.IsEnabled != on) fe.IsEnabled = on;
                        }
                    }
                }
                else
                {
                    if (tag != null && tag.StartsWith("act:", StringComparison.Ordinal)) actPart = tag;
                    else if (tag != null && tag.StartsWith("in:", StringComparison.Ordinal)) inPart = tag;
                    else if (tag != null && tag.StartsWith("live:", StringComparison.Ordinal)) livePart = tag;
                    else if (tag != null && tag.StartsWith("chk:", StringComparison.Ordinal)) chkPart = tag;
                }

                if (chkPart != null)
                {
                    CheckBox cb = fe as CheckBox;
                    if (cb == null)
                    {
                        // PCL.MyCheckBox 不是 WPF 的 CheckBox，用反射设 Checked
                        string ck0 = chkPart.Substring(4);
                        string cv0;
                        bool on0 = d.TryGetValue(ck0, out cv0) && cv0 == "1";
                        System.Reflection.PropertyInfo pc = fe.GetType().GetProperty("Checked");
                        if (pc != null && pc.CanWrite)
                        {
                            object cur0 = pc.GetValue(fe, null);
                            if (!Equals(cur0, on0)) pc.SetValue(fe, on0, null);
                            if (CheckHooked.Add(fe))
                            {
                                // PCL.MyCheckBox 没有 Click 事件，用路由的鼠标抬起事件接管
                                MouseButtonEventHandler mh = delegate(object s2, MouseButtonEventArgs e2)
                                {
                                    try { RunAction(fe, "tc-tog:" + ck0); }
                                    catch (Exception ex) { Log(null, "chk err: " + ex.Message); }
                                };
                                fe.AddHandler(UIElement.MouseLeftButtonUpEvent, mh, true);
                            }
                            hit++;
                        }
                    }
                    if (cb != null)
                    {
                        string ck = chkPart.Substring(4);
                        string cv;
                        bool on = d.TryGetValue(ck, out cv) && cv == "1";
                        if (cb.IsChecked != on) cb.IsChecked = on;
                        if (CheckHooked.Add(cb))
                        {
                            cb.Click += delegate
                            {
                                try { RunAction(cb, "tc-tog:" + ck); }
                                catch (Exception ex) { Log(null, "chk error: " + ex.Message); }
                            };
                        }
                        hit++;
                    }
                    else if (actPart == null && inPart == null) { }
                }
                if (actPart != null)
                {
                    HookAction(fe, actPart);
                }
                else if (inPart != null)
                {
                    HookInput(fe, inPart);
                }
                else if (livePart != null)
                {
                    hit++;
                    string key = livePart.Substring(5);
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
                            if (tb != null) { if (tb.Text != val) tb.Text = val; }
                            else
                            {
                                // 按钮也能被实时数据改文字（用来显示开关状态）
                                ContentControl cc = fe as ContentControl;
                                if (cc != null && !Equals(cc.Content, val)) { cc.Content = val; }
                                else if (cc == null)
                                {
                                    // PCL 的 MyButton 不是 ContentControl，用 Text 属性
                                    System.Reflection.PropertyInfo pi = fe.GetType().GetProperty("Text");
                                    if (pi != null && pi.CanWrite)
                                    {
                                        object cur = pi.GetValue(fe, null);
                                        if (!Equals(cur, val)) pi.SetValue(fe, val, null);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(el);
            for (int i = 0; i < n; i++)
                hit += WalkLive(System.Windows.Media.VisualTreeHelper.GetChild(el, i), d, status);
            return hit;
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
        // ---------------- 左侧栏「内存优化」按钮 ----------------
        // 主页卡片已经很多，所以把内存优化塞到 PCL 左侧栏的空白处：
        // 找到「启动游戏」按钮，在它正上方插一个按钮 + 一行结果文字。

        private static TextBlock MemLabel;
        private static FrameworkElement MemButton;
        private static bool MemTried;

        private static void EnsureSidebarMem(State st)
        {
            if (MemTried) return;
            Window w = Application.Current == null ? null : Application.Current.MainWindow;
            if (w == null) return;
            // PCL 的「启动游戏」不是标准 ButtonBase，只是带 Text 属性的自定义控件，
            // 所以按文字找元素，再往上找到真正的按钮控件和它所在的 StackPanel。
            FrameworkElement hit = FindByText(w, "启动游戏");
            if (hit == null) return;             // 页面还没建好，下一个 tick 再试
            MemTried = true;
            try
            {
                Log(st, "sidebar: hit = " + hit.GetType().FullName);
                DumpChain(st, hit);

                object btnObj = null;
                DependencyObject cur = hit;
                for (int i = 0; i < 8 && cur != null; i++)
                {
                    PropertyInfo p = cur.GetType().GetProperty("Text");
                    if (p != null && p.CanWrite && cur is Control) { btnObj = cur; break; }
                    cur = VisualTreeHelper.GetParent(cur);
                }
                if (btnObj == null) btnObj = hit;
                Log(st, "sidebar: button type = " + btnObj.GetType().FullName);

                FrameworkElement btn = MakeMemButton((FrameworkElement)btnObj);
                if (btn == null) { Log(st, "sidebar: cannot create button"); return; }
                MemButton = btn;
                StackPanel box = new StackPanel();
                box.Width = 258;
                box.Margin = new Thickness(0, 0, 0, 8);
                box.Children.Add(btn);
                MemLabel = new TextBlock();
                MemLabel.Text = "点一下释放内存";
                MemLabel.FontSize = 11;
                MemLabel.Margin = new Thickness(0, 6, 0, 0);
                MemLabel.TextAlignment = TextAlignment.Center;
                MemLabel.TextWrapping = TextWrapping.Wrap;
                MemLabel.Opacity = 0.75;
                box.Children.Add(MemLabel);

                DependencyObject child = hit;
                DependencyObject parent = VisualTreeHelper.GetParent(child);
                int guard = 0;
                while (parent != null && guard++ < 14)
                {
                    Panel panel = parent as Panel;
                    if (panel != null)
                    {
                        StackPanel sp = panel as StackPanel;
                        if (sp != null)
                        {
                            int idx = sp.Children.IndexOf((UIElement)child);
                            if (idx >= 0)
                            {
                                sp.Children.Insert(idx, box);
                                Log(st, "sidebar: inserted into StackPanel before " + child.GetType().Name + " at " + idx);
                                return;
                            }
                        }
                        Grid g = panel as Grid;
                        if (g != null && g.RowDefinitions.Count > 0)
                        {
                            if (TryInsertIntoEmptyRow(st, g, box)) return;
                            if (TryInsertIntoBigRow(st, g, box)) return;
                            if (TryInsertIntoContainer(st, g, box)) return;
                        }
                    }
                    child = parent;
                    parent = VisualTreeHelper.GetParent(parent);
                }
                Log(st, "sidebar: no usable container");
            }
            catch (Exception ex) { Log(st, "sidebar error: " + ex); }
        }

        /// <summary>把「启动游戏」往上几层的类型 / 名字 / 尺寸打出来，方便确认插到了哪一层</summary>
        private static void DumpChain(State st, DependencyObject el)
        {
            DependencyObject cur = el;
            for (int i = 0; i < 8 && cur != null; i++)
            {
                FrameworkElement fe = cur as FrameworkElement;
                string info = i + ":" + cur.GetType().Name;
                if (fe != null) info += "[" + fe.Name + " " + (int)fe.ActualWidth + "x" + (int)fe.ActualHeight + "]";
                Panel p = cur as Panel;
                if (p != null) info += " children=" + p.Children.Count;
                Log(st, "  " + info);
                cur = VisualTreeHelper.GetParent(cur);
            }
        }

        private static FrameworkElement FindByText(DependencyObject root, string text)
        {
            FrameworkElement fe = root as FrameworkElement;
            if (fe != null && GetText(fe) == text) return fe;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                FrameworkElement hit = FindByText(VisualTreeHelper.GetChild(root, i), text);
                if (hit != null) return hit;
            }
            return null;
        }

        private static string GetText(object obj)
        {
            try
            {
                PropertyInfo p = obj.GetType().GetProperty("Text");
                if (p != null)
                {
                    object v = p.GetValue(obj, null);
                    if (v is string) return (string)v;
                }
            }
            catch { }
            try
            {
                ContentControl cc = obj as ContentControl;
                if (cc != null && cc.Content is string) return (string)cc.Content;
            }
            catch { }
            return null;
        }

        /// <summary>照着「启动游戏」按钮的类做一份一样的，样式才会跟左边栏一致</summary>
        private static FrameworkElement MakeMemButton(FrameworkElement launch)
        {
            FrameworkElement btn = null;
            object o = null;
            try
            {
                o = Activator.CreateInstance(launch.GetType());
                btn = o as FrameworkElement;
            }
            catch (Exception ex) { TryLog("sidebar ctor: " + ex.Message); }
            if (btn == null) return null;
            SetProp(o, "Text", "内存优化");
            // 颜色类型跟着「启动游戏」走，才会是左边栏那种蓝色按钮
            try
            {
                PropertyInfo cp = o.GetType().GetProperty("ColorType");
                PropertyInfo lp = launch.GetType().GetProperty("ColorType");
                if (cp != null && lp != null && cp.CanWrite)
                {
                    object v = lp.GetValue(launch, null);
                    if (v != null) cp.SetValue(o, v, null);
                }
            }
            catch { }
            btn.Height = 40;
            btn.Margin = new Thickness(0, 0, 0, 0);
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            btn.Tag = "act:memopt";
            btn.ToolTip = "清工作集 / 刷已修改页 / 清待命列表，和百宝箱里的「内存优化」一个效果";
            btn.AddHandler(UIElement.MouseLeftButtonUpEvent,
                new MouseButtonEventHandler(delegate(object s, MouseButtonEventArgs e)
                {
                    try { RunAction(btn, "memopt"); } catch { }
                }), true);
            return btn;
        }

        /// <summary>左侧栏是 Grid：找一行空着的、又够高的行，把按钮放进去（就是那块空白）</summary>
        private static bool TryInsertIntoEmptyRow(State st, Grid g, FrameworkElement box)
        {
            try
            {
                int rows = g.RowDefinitions.Count;
                StringBuilder sb = new StringBuilder();
                sb.Append("sidebar grid " + g.Name + " rows=" + rows + " children=" + g.Children.Count + " :");
                bool[] used = new bool[rows];
                foreach (UIElement c in g.Children)
                {
                    int r = Grid.GetRow(c);
                    if (r >= 0 && r < rows) used[r] = true;
                }
                for (int i = 0; i < rows; i++)
                {
                    sb.Append(" [" + i + (used[i] ? " used " : " free ") + (int)g.RowDefinitions[i].ActualHeight + "]");
                }
                Log(st, sb.ToString());
                int best = -1;
                double bestH = 0;
                for (int i = 0; i < rows; i++)
                {
                    if (used[i]) continue;
                    double h = g.RowDefinitions[i].ActualHeight;
                    if (h > bestH) { bestH = h; best = i; }
                }
                if (best < 0 || bestH < 40) return false;
                box.SetValue(Grid.RowProperty, best);
                box.HorizontalAlignment = HorizontalAlignment.Stretch;
                box.VerticalAlignment = VerticalAlignment.Center;
                box.Margin = new Thickness(0, 6, 0, 6);
                g.Children.Add(box);
                Log(st, "sidebar: inserted into grid row " + best + " h=" + (int)bestH);
                return true;
            }
            catch (Exception ex) { Log(st, "sidebar row error: " + ex.Message); return false; }
        }

        /// <summary>空白就在最高那一行的下半截：放进那一行、贴底，正好落在「启动游戏」上面</summary>
        private static bool TryInsertIntoBigRow(State st, Grid g, FrameworkElement box)
        {
            try
            {
                int rows = g.RowDefinitions.Count;
                if (rows == 0) return false;
                int best = -1;
                double bestH = 0;
                for (int i = 0; i < rows; i++)
                {
                    double h = g.RowDefinitions[i].ActualHeight;
                    if (h > bestH) { bestH = h; best = i; }
                }
                if (best < 0 || bestH < 80) return false;
                box.SetValue(Grid.RowProperty, best);
                box.HorizontalAlignment = HorizontalAlignment.Stretch;
                box.VerticalAlignment = VerticalAlignment.Bottom;
                box.Margin = new Thickness(0, 0, 0, 8);
                g.Children.Add(box);
                Log(st, "sidebar: added to " + g.Name + " row " + best + " h=" + (int)bestH + " (bottom)");
                return true;
            }
            catch (Exception ex) { Log(st, "sidebar bigrow error: " + ex.Message); return false; }
        }
        /// <summary>左栏那块空白在最高的那一行里面：钻进去，放到最下面的空位</summary>
        private static bool TryInsertIntoContainer(State st, Grid host, FrameworkElement box)
        {
            try
            {
                UIElement big = null;
                double bh = 0;
                foreach (UIElement c in host.Children)
                {
                    FrameworkElement fe = c as FrameworkElement;
                    if (fe == null) continue;
                    if (fe.ActualHeight > bh) { bh = fe.ActualHeight; big = c; }
                }
                if (big == null) return false;
                DependencyObject node = big;
                for (int depth = 0; depth < 5 && node != null; depth++)
                {
                    FrameworkElement fe = node as FrameworkElement;
                    Log(st, "  big[" + depth + "]=" + node.GetType().Name + "[" + (fe == null ? "?" : fe.Name) + " "
                        + (fe == null ? 0 : (int)fe.ActualWidth) + "x" + (fe == null ? 0 : (int)fe.ActualHeight) + "]");
                    Grid g = node as Grid;
                    if (g != null && g.RowDefinitions.Count > 0)
                    {
                        int last = g.RowDefinitions.Count - 1;
                        box.SetValue(Grid.RowProperty, last);
                        box.HorizontalAlignment = HorizontalAlignment.Stretch;
                        box.VerticalAlignment = VerticalAlignment.Center;
                        box.Margin = new Thickness(0, 6, 0, 6);
                        g.Children.Add(box);
                        Log(st, "sidebar: added to " + g.Name + " row " + last + "/" + g.RowDefinitions.Count);
                        return true;
                    }
                    StackPanel sp = node as StackPanel;
                    if (sp != null)
                    {
                        box.Margin = new Thickness(0, 8, 0, 8);
                        sp.Children.Add(box);
                        Log(st, "sidebar: appended to " + sp.Name);
                        return true;
                    }
                    Border bd = node as Border;
                    if (bd != null) { node = bd.Child; continue; }
                    ContentControl cc = node as ContentControl;
                    if (cc != null) { node = cc.Content as DependencyObject; continue; }
                    break;
                }
                return false;
            }
            catch (Exception ex) { Log(st, "sidebar container error: " + ex.Message); return false; }
        }
        private static void SetProp(object obj, string name, object value)
        {
            if (obj == null || value == null) return;
            try
            {
                PropertyInfo p = obj.GetType().GetProperty(name);
                if (p != null && p.CanWrite) p.SetValue(obj, value, null);
            }
            catch { }
        }

        /// 主页上的动作按钮（Tag="act:xxx"）：PCL 的自定义主页事件表达不了“驱动本机陶瓦”，
        /// 所以这里自己接管点击，请求本地服务的 /tc 接口。
        /// </summary>
        private static void HookAction(FrameworkElement fe, string tag)
        {
            if (fe is ButtonBase) return;
            if (!ActionHooked.Add(fe)) return;
            string action = tag.Substring(4);
            // 赞赏码默认收起：MyImage 的 Visibility 会被 PCL 在加载时改回 Visible，
            // 所以由补丁在首次挂接时强制折叠一次，之后再交给「展开赞赏码」按钮切换。
            if (action == "zan-open") fe.Visibility = Visibility.Collapsed;
            MouseButtonEventHandler mb = delegate(object s, MouseButtonEventArgs e)
            {
                try
                {
                    if (action == "zan-toggle") { ToggleZan(fe); return; }
                    if (action == "tc-pick") { PickTerracotta(fe); return; }
                    if (action == "zan-open") { OpenZan(fe); return; }
                    if (action == "tc-stop")
                    {
                        // 关闭联机 = 直接杀掉陶瓦进程，会断掉房间里所有人，先确认一次
                        MessageBoxResult mr = MessageBox.Show(
                            "停止陶瓦联机会断开当前房间内的所有连接。\n如果还有其他启动器正在使用陶瓦，也会一并停止。",
                            "停止陶瓦联机", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                        if (mr != MessageBoxResult.OK) return;
                    }
                    RunAction(fe, action);
                }
                catch (Exception ex) { Log(null, "action error: " + ex.Message); }
            };
            fe.AddHandler(UIElement.MouseLeftButtonUpEvent, mb, true);
        }

        /// <summary>赞赏码折叠：点一下按钮，切换那张图片的显示 / 隐藏</summary>
        /// <summary>浏览按钮：选 terracotta.exe 并把路径告诉服务端</summary>
        private static void PickTerracotta(FrameworkElement fe)
        {
            try
            {
                Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
                dlg.Title = "选择 terracotta.exe";
                dlg.Filter = "陶瓦联机 (terracotta.exe)|terracotta.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                dlg.CheckFileExists = true;
                bool? ok = dlg.ShowDialog();
                if (ok != true || string.IsNullOrEmpty(dlg.FileName)) return;
                State st = FindState(fe);
                string baseUrl = (st != null && st.LiveBase != null) ? st.LiveBase : "http://127.0.0.1:8848/";
                string url = baseUrl + "tc?do=tc-setpath&path=" + Uri.EscapeDataString(dlg.FileName);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { string r = Fetch(url); Log(st, "setpath: " + r); } catch { }
                });
                if (st != null) st.LiveLast = null;
            }
            catch (Exception ex) { Log(null, "pick error: " + ex.Message); }
        }

        private static void ToggleZan(FrameworkElement fe)
        {
            State st = FindState(fe);
            if (st == null) return;
            Panel host = st.PanCustom.GetValue(st.Page) as Panel;
            if (host == null) return;
            FrameworkElement img = FindByTag(host, "act:zan-open");
            if (img == null) return;
            img.Visibility = img.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>点赞赏码：在浏览器里打开原图（等于放大查看）</summary>
        private static void OpenZan(FrameworkElement fe)
        {
            State st = FindState(fe);
            string baseUrl = (st != null && st.LiveBase != null) ? st.LiveBase : "http://127.0.0.1:8848/";
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(baseUrl + "zan/zan.jpg") { UseShellExecute = true }); }
            catch (Exception ex) { Log(st, "open zan error: " + ex.Message); }
        }

        private static FrameworkElement FindByTag(DependencyObject root, string tag)
        {
            if (root == null) return null;
            FrameworkElement fe = root as FrameworkElement;
            if (fe != null)
            {
                string t = fe.Tag as string;
                if (t != null && t == tag) return fe;
            }
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                FrameworkElement r = FindByTag(System.Windows.Media.VisualTreeHelper.GetChild(root, i), tag);
                if (r != null) return r;
            }
            return null;
        }

        private static readonly HashSet<object> ActionHooked = new HashSet<object>();
        private static readonly HashSet<object> CheckHooked = new HashSet<object>();

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
            else if (action == "tc-url")
            {
                // 复制连接地址：加入房间成功后陶瓦会给出备用地址，直接填进游戏服务器地址
                if (st != null && st.LiveLast != null)
                {
                    string u = ExtractJsonString(st.LiveLast, "tcurl");
                    if (!string.IsNullOrEmpty(u))
                    {
                        try { Clipboard.SetText(u); } catch { }
                        Log(st, "action: 已复制连接地址 " + u);
                        return;
                    }
                }
                Log(st, "action: 还没有连接地址（先加入一个房间）");
                return;
            }
            if (action == "tc-host" || action == "tc-join" || action == "tc-detach")
            {
                string nick = ReadInput("nick");
                if (!string.IsNullOrEmpty(nick)) url += "&nick=" + Uri.EscapeDataString(nick);
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
                    content = Regex.Replace(content,
                        "xmlns[^ >]*=\"[^\"]*\"|xmlns[^ >]*='[^']*'", "").Replace("xmlns", "");
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
                // 左边栏不是 MyCard，是一整块 Border，单独处理

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



