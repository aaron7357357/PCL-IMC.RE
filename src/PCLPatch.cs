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
using System.Windows.Media.Animation;
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
                    TcNative.LogHook = delegate(string m) { TryLog(m); };
                    AutoTerracottaNative();
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
        private static int _nativeHookDone;
        private static void AutoTerracottaNative()
        {
            if (Interlocked.CompareExchange(ref _nativeHookDone, 1, 0) != 0) return;
            try
            {
                TcNative.LoadSettings();
                if (TcNative.AutoStart && TcNative.Enable)
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Thread.Sleep(1500);
                        try { if (TcNative.ExePath() != null) TcNative.Start(); } catch { }
                    });
                }
                AppDomain.CurrentDomain.ProcessExit += delegate
                {
                    try { if (TcNative.AutoStop && TcNative.Running()) TcNative.Stop(); } catch { }
                };
            }
            catch { }
        }

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
                EnsureLinkNav(st);
                EnsureLinkPage(st);
                HookLeftPage(st);
                GlassTick++;
                if (GlassTick == 10 && File.Exists(Path.Combine(AppDir(), "PCL", "link-autoshow.txt")))
                {
                    Window wa = Application.Current == null ? null : Application.Current.MainWindow;
                    FrameworkElement ra = wa == null ? null : FindByName(wa, "BtnTitleSelect2");
                    MethodInfo sa = ra == null ? null : ra.GetType().GetMethod("SetChecked", new Type[] { typeof(bool), typeof(bool), typeof(bool) });
                    if (sa != null) { sa.Invoke(ra, new object[] { true, true, false }); Log(st, "link: autoshow at tick10"); }
                }
                if (IsGlassAll()) ApplyGlassAll(st);   // imc-glass.txt=1 时才动   // 每个 tick 都套一遍，切页/启动时几乎立刻就是毛玻璃
                // EnsureGlassSwitch(st);  // 内测：个性化里的毛玻璃开关先不插
                // 左侧栏注入已关闭：PCL 左边栏是它自己的 Grid，硬插会把「版本选择」挤出可视区，
                if (st.Page == null) return;
                bool pageVisible = ((UIElement)st.Page).IsVisible;

                // 毛玻璃处理要在任何模式（联网 / 本地文件 / 预设）下都跑一遍，
                // 否则切到预设模式时卡片会露出 PCL 自带的白色底。
                // 注意不能只做一次：按 F5 刷新主页后 PCL 会重建控件，新实例又带回白底，
                // 所以每次 tick 都过一遍（函数内部只对“还没处理过”的控件赋值，开销很小）。
                if (pageVisible) st.Styled = StyleGlassCards(st);

                // 实时数据：三种模式都跑。页面本身恒定，只有带 Tag="live:*" 的控件改文字。
                StartLive(st);
                ApplyPresetOnce(st);
                EnsurePresetItem(st);
                EnsureImcPreset(st);
                EnsureVersionRows(st);
                if (!pageVisible) return;

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
            if (IsNativePreset(st))
            {
                if (Interlocked.CompareExchange(ref st.LiveBusy, 1, 0) != 0) return;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        if (TcNative.AutoFill && TcNative.Running())
                        {
                            string tcUrlNow = TcNative.Url();
                            if (!string.IsNullOrEmpty(tcUrlNow) && tcUrlNow != _lastFilledUrl)
                            {
                                int n = TcNative.PutServer("陶瓦联机", tcUrlNow);
                                if (n > 0) { _lastFilledUrl = tcUrlNow; Log(st, "tc autofill: " + n + " files"); }
                            }
                        }
                        string json = ImcNative.BuildLiveJson();
                        ((DispatcherObject)st.Page).Dispatcher.BeginInvoke(DispatcherPriority.Background,
                            new Action(delegate { ApplyLive(st, json); }));
                    }
                    catch { }
                    finally { Interlocked.Exchange(ref st.LiveBusy, 0); }
                });
                return;
            }
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
                // 联机页显示时自定义主页是隐藏的，PanCustom 可能拿不到，不能在这里直接 return
                string status;
                if (!d.TryGetValue("status", out status)) status = "";
                int touched = 0;
                if (host != null) touched += WalkLive(host, d, status);
                if (TeraPage != null) touched += WalkLive(TeraPage, d, status);
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

        // ---------------- 「联机」页：陶瓦面板 ----------------
        // 官方 exe 里本来就有 PCL.PageLinkMain（第 3 个导航项 BtnTitleSelect2），只是默认隐藏。
        // 这里把那个真页面放出来，再把它的内容换成我们的陶瓦面板。

        private static FrameworkElement TeraPage;
        private static FrameworkElement TeraPrev;
        private static FrameworkElement TeraWrap;
        private static bool LinkNavDone;
        private static string PendingSection;
        private static string LastSection = "create";
        private static FrameworkElement LinkNav;


        private static bool LeftHookDone;
        private static bool LinkAnimDone;
        private static bool WasOnLink;
        private static Panel SidebarPanel;

        private static void HookLeftPage(State st)
        {
            if (LeftHookDone) return;
            Window w = Application.Current == null ? null : Application.Current.MainWindow;
            if (w == null) return;
            Border left = FindByName(w, "PanMainLeft") as Border;
            if (left == null) return;
            LeftHookDone = true;
            try
            {
                // PCL 换页时会重新 New MyPageLeft 并塞进 PanMainLeft.Child，
                // 布局一更新就把侧栏补进去，赶在它 30ms 后触发动画之前，动画才是官方的滑入效果
                left.LayoutUpdated += delegate
                {
                    try
                    {
                        FrameworkElement c = left.Child as FrameworkElement;
                        if (c == null || c.GetType().Name != "MyPageLeft") return;
                        Panel host = c as Panel;
                        if (host == null || host.Children.Count > 0) return;
                        Border r = FindByName(w, "PanMainRight") as Border;
                        FrameworkElement rc = r == null ? null : r.Child as FrameworkElement;
                        if (rc == null || rc.GetType().Name != "PageLinkMain") return;
                        BuildLinkSidebar(st);
                    }
                    catch { }
                };
                Log(st, "link: left page layout hook installed");
            }
            catch (Exception ex) { Log(st, "link hook error: " + ex.Message); }
        }

        // ---------------- 「imc.re主页」预设 ----------------
        // 在个性化 → 主页 → 主页预设 里加一项「imc.re主页」，选中后由我们加载本机服务的主页。

        private static bool PresetItemAdded;
        private static bool ImcPresetLoaded;

        // ---------------- 首次启动自动套用我们的主页预设 ----------------

        private static bool _presetOnce;
        private static void ApplyPresetOnce(State st)
        {
            if (_presetOnce) return;
            _presetOnce = true;
            try
            {
                string marker = Path.Combine(AppDir(), "PCL", "imc-preset-once.txt");
                if (File.Exists(marker)) return;
                object preset = GetSetting(st, "UiCustomPreset");
                int pv = preset is int ? (int)preset : -1;
                if (pv != 99)
                {
                    SetSetting(st, "UiCustomType", 3);
                    SetSetting(st, "UiCustomPreset", 99);
                    WriteIniPreset();
                    // 立刻让主页按新设置重载，不用等下一次启动
                    try
                    {
                        MethodInfo refresh = st.Page.GetType().GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (refresh != null) refresh.Invoke(st.Page, null);
                    }
                    catch { }
                    Log(st, "preset: 首次启动自动套用 imc.re主页 (was " + pv + ")");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(marker));
                File.WriteAllText(marker, "1");
            }
            catch (Exception ex) { Log(st, "preset once error: " + ex.Message); }
        }

        private static void WriteIniPreset()
        {
            try
            {
                string ini = Path.Combine(AppDir(), "PCL", "Setup.ini");
                Directory.CreateDirectory(Path.GetDirectoryName(ini));
                List<string> lines = File.Exists(ini) ? new List<string>(File.ReadAllLines(ini, Encoding.UTF8)) : new List<string>();
                bool hasType = false, hasPreset = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].StartsWith("UiCustomType:")) { lines[i] = "UiCustomType:3"; hasType = true; }
                    else if (lines[i].StartsWith("UiCustomPreset:")) { lines[i] = "UiCustomPreset:99"; hasPreset = true; }
                }
                if (!hasType) lines.Add("UiCustomType:3");
                if (!hasPreset) lines.Add("UiCustomPreset:99");
                File.WriteAllLines(ini, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex) { TryLog("write ini failed: " + ex.Message); }
        }

        private static void SetSetting(State st, string key, int value)
        {
            try
            {
                Type settings = st.Page.GetType().Assembly.GetType("PCL.Settings");
                if (settings == null) { TryLog("Settings type missing"); return; }
                MethodInfo safe = null;
                foreach (MethodInfo m in settings.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "SetSafe") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 3 && ps[0].ParameterType == typeof(string)) { safe = m; break; }
                }
                if (safe == null) { TryLog("SetSafe missing"); return; }
                safe.Invoke(null, new object[] { key, value, null });
                TryLog("setting applied: " + key + "=" + value);
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                TryLog("set setting failed: " + inner.Message);
            }
        }

        private static void EnsurePresetItem(State st)
        {
            if (PresetItemAdded) return;
            Window w = Application.Current == null ? null : Application.Current.MainWindow;
            if (w == null) return;
            ComboBox combo = FindPresetCombo(w);
            if (combo == null) return;
            PresetItemAdded = true;
            try
            {
                Assembly asm = w.GetType().Assembly;
                Type itemType = asm.GetType("PCL.MyComboBoxItem");
                if (itemType == null) { Log(st, "preset: MyComboBoxItem missing"); return; }
                object item = Activator.CreateInstance(itemType);
                PropertyInfo cp = itemType.GetProperty("Content");
                if (cp != null) cp.SetValue(item, "imc.re主页", null);
                Type ss = asm.GetType("PCL.SettingService");
                if (ss != null)
                {
                    FieldInfo vp = ss.GetField("ValueProperty", BindingFlags.Public | BindingFlags.Static);
                    if (vp != null)
                    {
                        object dp = vp.GetValue(null);
                        ((DependencyObject)item).SetValue((DependencyProperty)dp, "99");
                    }
                }
                combo.Items.Add(item);
                Log(st, "preset: imc.re主页 added");
            }
            catch (Exception ex) { Log(st, "preset add error: " + ex.Message); }
        }

        private static ComboBox FindPresetCombo(DependencyObject root)
        {
            try
            {
                int n = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < n; i++)
                {
                    DependencyObject c = VisualTreeHelper.GetChild(root, i);
                    ComboBox cb = c as ComboBox;
                    if (cb != null && cb.GetType().FullName == "PCL.MyComboBox")
                    {
                        Type ss = cb.GetType().Assembly.GetType("PCL.SettingService");
                        if (ss != null)
                        {
                            FieldInfo kp = ss.GetField("KeyProperty", BindingFlags.Public | BindingFlags.Static);
                            if (kp != null)
                            {
                                object key = cb.GetValue((DependencyProperty)kp.GetValue(null));
                                if (key is string && (string)key == "UiCustomPreset") return cb;
                            }
                        }
                    }
                    ComboBox found = FindPresetCombo(c);
                    if (found != null) return found;
                }
            }
            catch { }
            return null;
        }

        private static string GetEmbeddedText(string name)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                        return r.ReadToEnd();
                }
            }
            catch { return null; }
        }

        private static string GetEmbeddedPage()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("IMCHome.xaml"))
                {
                    if (s == null) return null;
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                        return r.ReadToEnd();
                }
            }
            catch { return null; }
        }

        private static bool IsNativePreset(State st)
        {
            object t = GetSetting(st, "UiCustomType");
            object p = GetSetting(st, "UiCustomPreset");
            return t is int && (int)t == 3 && p is int && (int)p == 99;
        }

        // ---------------- 「按版本进服」动态行 ----------------

        private static string _verSig;
        private static DateTime _verScanTime = DateTime.MinValue;
        private static List<string> _verCache = new List<string>();

        private static readonly List<FileSystemWatcher> _verWatchers = new List<FileSystemWatcher>();

        /// <summary>盯着 versions 目录，有增删改名就立刻让缓存失效（下一 tick 重新扫描）。</summary>
        private static void EnsureVersionWatchers(List<string> roots)
        {
            if (_verWatchers.Count > 0) return;
            foreach (string dir in roots)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    FileSystemWatcher w = new FileSystemWatcher(dir);
                    w.IncludeSubdirectories = false;
                    w.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName;
                    FileSystemEventHandler h = delegate { _verScanTime = DateTime.MinValue; };
                    w.Created += h;
                    w.Deleted += h;
                    w.Renamed += delegate { _verScanTime = DateTime.MinValue; };
                    w.EnableRaisingEvents = true;
                    _verWatchers.Add(w);
                    Log(null, "versions watcher: " + dir);
                }
                catch { }
            }
        }

        /// <summary>排序：数字版本在上，英文名其次，中文名最后；数字版本按版本号从新到旧。</summary>
        private static int CompareVersionNames(string a, string b)
        {
            int ga = VersionGroup(a), gb = VersionGroup(b);
            if (ga != gb) return ga.CompareTo(gb);
            if (ga == 0)
            {
                int[] va = VersionNumbers(a), vb = VersionNumbers(b);
                for (int i = 0; i < 3; i++)
                {
                    int x = i < va.Length ? va[i] : 0;
                    int y = i < vb.Length ? vb[i] : 0;
                    if (x != y) return y.CompareTo(x);   // 新的在前
                }
            }
            return string.CompareOrdinal(a, b);
        }

        private static int VersionGroup(string name)
        {
            if (string.IsNullOrEmpty(name)) return 2;
            char c = name[0];
            if (c >= '0' && c <= '9') return 0;          // 数字版本 / 数字开头
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return 1;  // 英文整合包
            return 2;                                     // 中文等
        }

        private static int[] VersionNumbers(string name)
        {
            Match m = Regex.Match(name, "(\\d+)\\.(\\d+)(?:\\.(\\d+))?");
            if (!m.Success) return new int[] { 0, 0, 0 };
            int a = int.Parse(m.Groups[1].Value);
            int b = int.Parse(m.Groups[2].Value);
            int c = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            return new int[] { a, b, c };
        }

        private static List<string> ScanVersions()
        {
            if ((DateTime.Now - _verScanTime).TotalSeconds < 1.5) return _verCache;
            _verScanTime = DateTime.Now;
            List<string> names = new List<string>();
            List<string> roots = new List<string>();
            try { roots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".minecraft", "versions")); } catch { }
            try { roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "versions")); } catch { }
            EnsureVersionWatchers(roots);
            foreach (string dir in roots)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string sub in Directory.GetDirectories(dir))
                    {
                        string name = Path.GetFileName(sub);
                        string jsonPath = Path.Combine(sub, name + ".json");
                        if (!File.Exists(jsonPath)) continue;
                        // 优先用 JSON 里的 clientVersion（整合包的名字里往往没有版本号）
                        string mcVersion = "";
                        try
                        {
                            string text = File.ReadAllText(jsonPath, Encoding.UTF8);
                            Match m = Regex.Match(text, "\"clientVersion\"\\s*:\\s*\"([^\"]+)\"");
                            if (m.Success) mcVersion = m.Groups[1].Value;
                        }
                        catch { }
                        string probe = string.IsNullOrEmpty(mcVersion) ? name : mcVersion;
                        if (InRange(probe) && !names.Contains(name)) names.Add(name);
                    }
                }
                catch { }
            }
            names.Sort(CompareVersionNames);
            _verCache = names;
            return names;
        }

        /// <summary>把版本名里的数字提出来比较，判断是否落在 1.8 ~ 1.21.11。</summary>
        private static bool InRange(string name)
        {
            Match m = Regex.Match(name, "(\\d+)\\.(\\d+)(?:\\.(\\d+))?");
            if (!m.Success) return false;
            int major = int.Parse(m.Groups[1].Value);
            int minor = int.Parse(m.Groups[2].Value);
            int patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            if (major != 1) return false;
            if (minor < 8) return false;
            if (minor > 21) return true;
            if (minor < 21) return true;
            return patch <= 11;
        }

        private static FrameworkElement FindByTagName(DependencyObject root, string tag)
        {
            FrameworkElement fe = root as FrameworkElement;
            if (fe != null && (fe.Tag as string) == tag) return fe;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                FrameworkElement hit = FindByTagName(VisualTreeHelper.GetChild(root, i), tag);
                if (hit != null) return hit;
            }
            return null;
        }

        private static void EnsureVersionRows(State st)
        {
            if (!IsNativePreset(st)) return;
            Panel host = st.PanCustom == null ? null : st.PanCustom.GetValue(st.Page) as Panel;
            if (host == null) return;
            FrameworkElement holder = FindByTagName(host, "verrows");
            Panel rowsPanel = holder as Panel;
            if (rowsPanel == null) return;
            List<string> vers = ScanVersions();
            List<string> all = vers;
            string sig = string.Join("|", vers.ToArray());
            if (sig == _verSig && rowsPanel.Children.Count > 0) return;
            _verSig = sig;
            try
            {
                TextBlock hint = FindByTagName(host, "verhint") as TextBlock;
                if (hint != null)
                {
                    if (all.Count == 0) hint.Text = "没扫描到本机已安装的游戏版本，可用左侧的「启动游戏」进入。";
                    else if (vers.Count == 0) hint.Text = "已装版本里没有 1.8~1.21.11 的版本，可用左侧的「启动游戏」进入。";
                    else hint.Text = "正版点「正版」，离线点「离线」；只列 1.8~1.21.11 的版本（" + vers.Count + " 个）。";
                }
                rowsPanel.Children.Clear();
                if (vers.Count == 0) return;
                StringBuilder sb = new StringBuilder();
                sb.Append("<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" ");
                sb.Append("xmlns:local=\"clr-namespace:PCL;assembly=Plain Craft Launcher 2\" ");
                sb.Append("xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">");
                foreach (string v in vers) sb.Append(VersionRowXaml(v));
                sb.Append("</StackPanel>");
                object obj = st.GetObjectFromXML.Invoke(null, new object[] { sb.ToString() });
                Panel built = obj as Panel;
                if (built == null) return;
                rowsPanel.Children.Add(built);
                Log(st, "versions: " + vers.Count + " rows");
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Log(st, "versions build error: " + inner.Message);
            }
        }

        private static string XmlEsc(string v)
        {
            return (v ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static string VersionRowXaml(string name)
        {
            string shortName = name.Length <= 16 ? name : name.Substring(0, 15) + "…";
            string esc = XmlEsc(name);
            string eshort = XmlEsc(shortName);
            return "<Grid Margin=\"0,0,0,6\">" +
                "<Grid.ColumnDefinitions><ColumnDefinition Width=\"150\" /><ColumnDefinition Width=\"*\" /><ColumnDefinition Width=\"*\" /></Grid.ColumnDefinitions>" +
                "<TextBlock Grid.Column=\"0\" VerticalAlignment=\"Center\" Text=\"" + eshort + "\" ToolTip=\"" + esc + "\" Foreground=\"#FF233347\" />" +
                "<local:MyButton Grid.Column=\"1\" HorizontalAlignment=\"Stretch\" Height=\"32\" Margin=\"0,0,4,0\" Padding=\"10,0,10,0\" ColorType=\"Highlight\" Text=\"正版\" EventType=\"启动游戏\" EventData=\"" + esc + "|on.imc.cab\" ToolTip=\"用 " + esc + " 进入 on.imc.cab（正版账号）\" />" +
                "<local:MyButton Grid.Column=\"2\" HorizontalAlignment=\"Stretch\" Height=\"32\" Margin=\"4,0,0,0\" Padding=\"10,0,10,0\" ColorType=\"Highlight\" Text=\"离线\" EventType=\"启动游戏\" EventData=\"" + esc + "|off.imc.cab\" ToolTip=\"用 " + esc + " 进入 off.imc.cab（离线账号）\" />" +
                "</Grid>";
        }

        /// <summary>把内嵌的赞赏码塞进图片控件，摆脱对本地服务的依赖。</summary>
        /// <summary>PCL 内放大查看图片：半透明黑底 + 居中大图，点一下 / Esc 关闭。</summary>
        private static void ShowImageOverlay(ImageSource source)
        {
            try
            {
                TryLog("zan overlay open");
                Window owner = Application.Current == null ? null : Application.Current.MainWindow;
                Window win = new Window();
                win.WindowStyle = WindowStyle.None;
                win.AllowsTransparency = true;
                win.Background = new SolidColorBrush(Color.FromArgb(0xD8, 0, 0, 0));
                win.ResizeMode = ResizeMode.NoResize;
                win.ShowInTaskbar = false;
                if (owner != null)
                {
                    win.Owner = owner;
                    win.WindowStartupLocation = WindowStartupLocation.Manual;
                    win.Left = owner.Left;
                    win.Top = owner.Top;
                    win.Width = owner.ActualWidth;
                    win.Height = owner.ActualHeight;
                }
                else
                {
                    win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    win.Width = 900;
                    win.Height = 700;
                }
                StackPanel panel = new StackPanel();
                panel.VerticalAlignment = VerticalAlignment.Center;
                panel.HorizontalAlignment = HorizontalAlignment.Center;
                System.Windows.Controls.Image img = new System.Windows.Controls.Image();
                img.Source = source;
                img.Stretch = Stretch.Uniform;
                img.MaxHeight = win.Height * 0.72;
                img.MaxWidth = win.Width * 0.78;
                img.Margin = new Thickness(0, 0, 0, 12);
                TextBlock tip = new TextBlock();
                tip.Text = "点击任意处关闭";
                tip.Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255));
                tip.HorizontalAlignment = HorizontalAlignment.Center;
                tip.FontSize = 13;
                panel.Children.Add(img);
                panel.Children.Add(tip);
                win.Content = panel;
                win.MouseDown += delegate { win.Close(); };
                win.KeyDown += delegate(object s3, System.Windows.Input.KeyEventArgs e3)
                {
                    if (e3.Key == System.Windows.Input.Key.Escape) win.Close();
                };
                win.Loaded += delegate { win.Activate(); win.Focus(); };
                win.ShowDialog();
            }
            catch (Exception ex) { TryLog("zan overlay error: " + ex.Message); }
        }

        private static void ApplyZanImage(State st, DependencyObject root)
        {
            try
            {
                FrameworkElement img = FindByTagName(root, "act:zan-open");
                System.Windows.Controls.Image image = img as System.Windows.Controls.Image;
                if (image == null) return;
                byte[] data = ReadEmbeddedBytes("zan.jpg");
                if (data == null) return;
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(data);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                image.Source = bmp;
                image.Cursor = System.Windows.Input.Cursors.Hand;
                // 点图片在 PCL 里放大（不再跳浏览器、不再依赖本地服务）
                image.MouseLeftButtonUp += delegate(object sender2, System.Windows.Input.MouseButtonEventArgs e2)
                {
                    e2.Handled = true;
                    ShowImageOverlay(bmp);
                };
                Log(st, "zan image: embedded");
            }
            catch (Exception ex) { Log(st, "zan image error: " + ex.Message); }
        }

        private static byte[] ReadEmbeddedBytes(string name)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    using (MemoryStream ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch { return null; }
        }

        private static void EnsureImcPreset(State st)
        {
            object typeValue = GetSetting(st, "UiCustomType");
            object presetValue = GetSetting(st, "UiCustomPreset");
            bool on = typeValue is int && (int)typeValue == 3 && presetValue is int && (int)presetValue == 99;
            if (!on) { ImcPresetLoaded = false; return; }
            Panel host = st.PanCustom == null ? null : st.PanCustom.GetValue(st.Page) as Panel;
            if (host == null) return;
            if (ImcPresetLoaded && host.Children.Count > 0) return;
            try
            {
                string content = GetEmbeddedPage();
                if (string.IsNullOrEmpty(content)) return;
                string xaml = "<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                              "xmlns:sys=\"clr-namespace:System;assembly=mscorlib\" " +
                              "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
                              "xmlns:local=\"clr-namespace:PCL;assembly=Plain Craft Launcher 2\" " +
                              "xmlns:core=\"clr-namespace:MeloongCore;assembly=MeloongCore\" " +
                              "xmlns:corewpf=\"clr-namespace:MeloongCore.Wpf;assembly=MeloongCore.Wpf\">" + content + "</StackPanel>";
                object obj = st.GetObjectFromXML.Invoke(null, new object[] { xaml });
                Panel page = obj as Panel;
                if (page == null) return;
                host.Children.Clear();
                host.Children.Add(page);
                ApplyZanImage(st, page);
                ImcPresetLoaded = true;
                _verSig = null;
                Log(st, "preset: imc.re主页 loaded");
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Log(st, "preset load error: " + inner.Message);
            }
        }

        private static void EnsureLinkNav(State st)
        {
            if (LinkNavDone) return;
            Window w = Application.Current == null ? null : Application.Current.MainWindow;
            if (w == null) return;
            FrameworkElement sel = FindByName(w, "PanTitleSelect");
            Panel sp = sel as Panel;
            FrameworkElement real = FindByName(w, "BtnTitleSelect2");
            if (sp == null || real == null) return;
            LinkNavDone = true;
            try
            {
                // 自己插一个看得见的「联机」；点它去勾 PCL 自带的（隐藏的）联机按钮，走 PCL 真正的切页流程
                object nav = Activator.CreateInstance(real.GetType());
                FrameworkElement fe = nav as FrameworkElement;
                if (fe == null) { Log(st, "link: cannot create nav"); return; }
                SetProp(nav, "Text", "联机");
                SetProp(nav, "Tag", "2");
                SetProp(nav, "Logo", "M512 817c-48.601 0-88-39.399-88-88s39.399-88 88-88 88 39.399 88 88-39.399 88-88 88zM237.671 565.74C308.335 474.58 397.369 429 504.774 429c118.433 0 214.225 55.421 287.377 166.264l-53.407 30.369c-13.84 7.87-31.362 4.367-41.114-8.219-50.291-64.911-114.577-97.367-192.856-97.367-86.851 0-156.835 40.318-209.95 120.953l-47.995-28.02c-15.263-8.91-20.412-28.507-11.502-43.77a32 32 0 0 1 2.344-3.47zM107.691 419.47C205.24 278.491 337.805 208 505.379 208c178.77 0 317.694 80.224 416.772 240.672l-56.54 31.73c-13.686 7.68-30.922 4.303-40.697-7.975C735.581 360.213 629.07 303.009 505.38 300.815 373.997 298.485 261.637 362.88 168.3 494l-50.116-28.505c-15.362-8.738-20.732-28.275-11.994-43.637a32 32 0 0 1 1.5-2.387z");
                SetPropDouble(nav, "LogoScale", 1.05);
                fe.Margin = new Thickness(5, 0, 5, 0);
                fe.Height = 27;
                fe.VerticalAlignment = VerticalAlignment.Center;
                fe.AddHandler(UIElement.MouseLeftButtonUpEvent,
                    new MouseButtonEventHandler(delegate(object s2, MouseButtonEventArgs e2)
                    {
                        try
                        {
                            Window mw = Application.Current == null ? null : Application.Current.MainWindow;
                            FrameworkElement r2 = mw == null ? null : FindByName(mw, "BtnTitleSelect2");
                            MethodInfo sc = r2 == null ? null : r2.GetType().GetMethod("SetChecked", new Type[] { typeof(bool), typeof(bool), typeof(bool) });
                            if (sc != null) sc.Invoke(r2, new object[] { true, true, false });
                            MethodInfo sc3 = fe.GetType().GetMethod("SetChecked", new Type[] { typeof(bool), typeof(bool), typeof(bool) });
                            if (sc3 != null) sc3.Invoke(fe, new object[] { true, false, false });
                            ShowSection(st, LastSection);   // 再次点联机时回到上次看的分栏
                        }
                        catch (Exception ex) { Log(st, "link nav click error: " + ex.Message); }
                    }), true);
                int idx = Math.Min(2, sp.Children.Count);
                sp.Children.Insert(idx, fe);
                LinkNav = fe;
                Log(st, "link: custom nav inserted at " + idx);

            }
            catch (Exception ex) { Log(st, "link nav error: " + ex.Message); }
        }

        private static void EnsureLinkPage(State st)
        {
            if (TeraWrap != null)
            {
                Window w0 = Application.Current == null ? null : Application.Current.MainWindow;
                Border r0 = w0 == null ? null : FindByName(w0, "PanMainRight") as Border;
                FrameworkElement c0 = r0 == null ? null : r0.Child as FrameworkElement;
                bool onLink = c0 != null && c0.GetType().Name == "PageLinkMain";
                if (!onLink) LinkAnimDone = false;
                if (onLink)
                {
                    BuildLinkSidebar(st);
                    // 只在刚进入联机页那一下勾选一次；之后不要再动，否则会跟 PCL 自己的单选框打架
                    if (!WasOnLink && LinkNav != null) SetNavChecked(LinkNav, true);
                }
                WasOnLink = onLink;
                if (onLink && PendingSection != null && TeraPage != null)
                {
                    ShowSection(st, PendingSection);
                    PendingSection = null;
                }
                return;
            }
            Window w = Application.Current == null ? null : Application.Current.MainWindow;
            if (w == null) return;
            try
            {
                Assembly asm = w.GetType().Assembly;
                Type modMain = asm.GetType("PCL.ModMain");
                FieldInfo fi = modMain == null ? null : modMain.GetField("FrmLinkMain", BindingFlags.Public | BindingFlags.Static);
                if (fi == null) { Log(st, "link: FrmLinkMain field not found"); return; }
                object pageObj = fi.GetValue(null);
                if (pageObj == null)
                {
                    // PCL 第一次点「联机」才 New PageLinkMain，这里提前建好并包装，
                    // 免得点进去先闪一下它自己的页面再换成我们的
                    Type pt = asm.GetType("PCL.PageLinkMain");
                    if (pt == null) return;
                    pageObj = Activator.CreateInstance(pt);
                    fi.SetValue(null, pageObj);
                    Log(st, "link: page pre-created");
                }
                Decorator page = pageObj as Decorator;
                if (page == null) return;
                if (TeraPage == null)
                {
                    TeraPage = BuildTeraPage(st);
                    if (TeraPage == null) return;
                }
                FrameworkElement orig = page.Child as FrameworkElement;
                if (orig == null) return;
                page.Child = null;                 // 断开 PCL 自己那套页面内容
                page.Child = TeraPage;             // 直接把我们的面板塞进去
                orig.Visibility = Visibility.Collapsed;
                TeraPage.Visibility = Visibility.Visible;
                TeraPrev = orig;
                TeraWrap = page;
                Log(st, "link: page pre-wrapped");
                if (!string.IsNullOrEmpty(st.LiveLast)) ApplyLive(st, st.LiveLast);
            }
            catch (Exception ex) { Log(st, "link page error: " + ex.Message); }
        }

        private static void SetNavChecked(FrameworkElement nav, bool on)
        {
            try
            {
                MethodInfo sc = nav.GetType().GetMethod("SetChecked", new Type[] { typeof(bool), typeof(bool), typeof(bool) });
                if (sc != null) sc.Invoke(nav, new object[] { on, false, false });
            }
            catch { }
        }

        private static Panel listForAnim(Panel root)
        {
            if (root.Children.Count == 1 && root.Children[0] is Panel) return (Panel)root.Children[0];
            return root;
        }

        private static bool OtherNavChecked(Window w)
        {
            try
            {
                FrameworkElement sel = FindByName(w, "PanTitleSelect");
                Panel sp = sel as Panel;
                if (sp == null) return false;
                foreach (UIElement c in sp.Children)
                {
                    FrameworkElement fe = c as FrameworkElement;
                    if (fe == null || fe == LinkNav) continue;
                    PropertyInfo cp = fe.GetType().GetProperty("Checked");
                    if (cp != null)
                    {
                        object v = cp.GetValue(fe, null);
                        if (v is bool && (bool)v) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static void PlaySidebarAnimOnce(State st)
        {
            if (LinkAnimDone) return;
            if (SidebarPanel == null) return;
            LinkAnimDone = true;
            Panel listA = SidebarPanel;
            Dispatcher dsp = ((DispatcherObject)listA).Dispatcher;
            // PCL 会先把左侧页 Opacity 设 0，30ms 后才设 1 并播它自己的动画。
            // 我们等它真正可见（Opacity≈1）再播条目动画，否则动画在不可见的页面里就跑完了。
            DispatcherTimer timer = null;
            int tries = 0;
            timer = new DispatcherTimer(DispatcherPriority.Background, dsp);
            timer.Interval = TimeSpan.FromMilliseconds(30);
            timer.Tick += delegate
            {
                tries++;
                FrameworkElement page = null;
                try
                {
                    Window w = Application.Current == null ? null : Application.Current.MainWindow;
                    Border left = w == null ? null : FindByName(w, "PanMainLeft") as Border;
                    page = left == null ? null : left.Child as FrameworkElement;
                }
                catch { }
                bool visible = page != null && page.Opacity > 0.85;
                if (!visible && tries < 12) return;
                timer.Stop();
                try { RunSidebarAnim(st, listA); } catch (Exception ex) { Log(st, "link anim error: " + ex.Message); }
            };
            timer.Start();
        }

        private static void RunSidebarAnim(State st, Panel root)
        {
            Panel listB = root;
            if (listB.Children.Count == 1 && listB.Children[0] is Panel) listB = (Panel)listB.Children[0];
            int idA = 0, delayA = 0;
            foreach (UIElement c1 in listB.Children)
            {
                FrameworkElement ea = c1 as FrameworkElement;
                if (ea == null) continue;
                ea.Opacity = 0;
                TranslateTransform tta = new TranslateTransform(-25, 0);
                ea.RenderTransform = tta;
                double target = ea is TextBlock ? 0.6 : 1;
                DoubleAnimation fa = new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(100));
                fa.BeginTime = TimeSpan.FromMilliseconds(delayA);
                fa.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                ea.BeginAnimation(UIElement.OpacityProperty, fa);
                DoubleAnimation ma = new DoubleAnimation(-25, -20, TimeSpan.FromMilliseconds(200));
                ma.BeginTime = TimeSpan.FromMilliseconds(delayA);
                ma.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                tta.BeginAnimation(TranslateTransform.XProperty, ma);
                DoubleAnimation mb = new DoubleAnimation(-20, 0, TimeSpan.FromMilliseconds(300));
                mb.BeginTime = TimeSpan.FromMilliseconds(delayA + 200);
                mb.EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
                tta.BeginAnimation(TranslateTransform.XProperty, mb);
                delayA += Math.Max(15 - idA, 7) * 2;
                idA++;
            }
            Log(st, "link: sidebar animation played");
        }

        private static void BuildLinkSidebar(State st)
        {
            try
            {
                Window w = Application.Current == null ? null : Application.Current.MainWindow;
                if (w == null) return;
                FrameworkElement leftPage = null;
                Border left = FindByName(w, "PanMainLeft") as Border;
                if (left != null) leftPage = left.Child as FrameworkElement;
                if (leftPage == null) return;
                Panel host = leftPage as Panel;
                if (host == null) return;
                if (host.Children.Count > 0) return;   // 已经建过

                string head = "<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                              "xmlns:sys=\"clr-namespace:System;assembly=mscorlib\" " +
                              "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
                              "xmlns:local=\"clr-namespace:PCL;assembly=Plain Craft Launcher 2\">";
                string xaml = head + "<StackPanel Margin=\"0,12,0,0\" Width=\"145\">" +
                    "<local:MyListItem Title=\"创建房间\" Type=\"RadioBox\" Height=\"36\" MinPaddingRight=\"35\" IsScaleAnimationEnabled=\"False\" LogoScale=\"0.9\" Logo=\"M52.1,164.5c-1.4,0-3.1-0.5-4.2-1.3c-2.6-1.7-4-4.2-4-7V43.8c0-2.9,1.6-5.8,4.1-7c1.2-0.8,2.7-1.2,4.1-1.2c1.5,0,2.9,0.4,4.2,1.2L153.1,93c0,0,0.1,0,0.1,0.1c2.6,1.7,4,4.2,4,7c0,3-1.7,5.8-4.2,7.1l-96.8,56.2C55.1,164,53.5,164.5,52.1,164.5z M60.4,142.1l72.1-42.1L60.4,58.2V142.1z\" />" +
                    "<local:MyListItem Title=\"加入房间\" Type=\"RadioBox\" Height=\"36\" MinPaddingRight=\"35\" IsScaleAnimationEnabled=\"False\" LogoScale=\"0.9\" Logo=\"M955 610h-59c-15 0-29 13-29 29v196c0 15-13 29-29 29h-649c-15 0-29-13-29-29v-196c0-15-13-29-29-29h-59c-15 0-29 13-29 29V905c0 43 35 78 78 78h787c43 0 78-35 78-78V640c0-15-13-29-29-29zM492 740c11 11 29 11 41 0l265-265c11-11 11-29 0-41l-41-41c-11-11-29-11-41 0l-110 110c-11 11-33 3-33-13V68C571 53 555 39 541 39h-59c-15 0-29 13-29 29v417c0 17-21 25-33 13l-110-110c-11-11-29-11-41 0L226 433c-11 11-11 29 0 41L492 740z\" />" +
                    "<local:MyListItem Title=\"设置\" Type=\"RadioBox\" Height=\"36\" MinPaddingRight=\"35\" IsScaleAnimationEnabled=\"False\" LogoScale=\"0.85\" Logo=\"M940.4 463.7L773.3 174.2c-17.3-30-49.2-48.4-83.8-48.4H340.2c-34.6 0-66.5 18.5-83.8 48.4L89.2 463.7c-17.3 30-17.3 66.9 0 96.8L256.4 850c17.3 30 49.2 48.4 83.8 48.4h349.2c34.6 0 66.5-18.5 83.8-48.4l167.2-289.5c17.3-29.9 17.3-66.8 0-96.8z m-94.6 96.8L725.9 768.1c-17.3 30-49.2 48.4-83.8 48.4H387.5c-34.6 0-66.5-18.5-83.8-48.4L183.9 560.5c-17.3-30-17.3-66.9 0-96.8l119.8-207.5c17.3-30 49.2-48.4 83.8-48.4h254.6c34.6 0 66.5 18.5 83.8 48.4l119.8 207.5c17.3 30 17.3 66.9 0.1 96.8z M522.3 321.2c-2.5-0.1-5-0.2-7.5-0.2-119.9 0-214 110.3-186.3 235 15.8 70.9 71.5 126.6 142.4 142.4 17.5 3.9 34.7 5.4 51.4 4.7 102.1-3.9 183.6-87.9 183.6-191 0.1-103-81.5-187-183.6-190.9z m68.6 269.1c-18.5 18-43 28.9-68.6 30.7l-6 0.3c-30.2 0.4-58.6-11.4-79.7-33-19.5-20.1-30.7-47-30.9-75-0.3-29.6 11.1-57.4 32-78.3 20.6-20.6 48-32 77.2-32 2.5 0 5 0.1 7.5 0.3 26.7 1.8 51.5 13.2 70.5 32.5 19.6 20 30.8 46.9 31.2 74.9 0.2 30.2-11.5 58.6-33.2 79.6z\" />" +
                    "</StackPanel></StackPanel>";
                object obj = st.GetObjectFromXML.Invoke(null, new object[] { xaml });
                Panel panel = obj as Panel;
                if (panel == null) { Log(st, "link: sidebar build failed"); return; }
                panel.Name = "PanItem";
                host.Children.Add(panel);
                SidebarPanel = panel;
                // 只注册一次：PCL 会复用同一个 MyPageLeft，重复注册会抛“重复名称”
                try
                {
                    if (leftPage.FindName("PanItem") == null)
                    {
                        leftPage.RegisterName("PanItem", panel);
                        PropertyInfo ap = leftPage.GetType().GetProperty("AnimatedControl");
                        if (ap != null && ap.CanWrite) ap.SetValue(leftPage, "PanItem", null);
                        Log(st, "link: sidebar registered for native animation");
                    }
                }
                catch (Exception ex3) { Log(st, "link register error: " + ex3.Message); }
                Panel list = panel;
                if (panel.Children.Count == 1 && panel.Children[0] is Panel) list = (Panel)panel.Children[0];
                string[] secNames = new string[] { "create", "join", "settings" };
                // 默认选中第一项（创建房间）
                try
                {
                    FrameworkElement first = list.Children.Count > 0 ? list.Children[0] as FrameworkElement : null;
                    if (first != null)
                    {
                        PropertyInfo cp = first.GetType().GetProperty("Checked");
                        if (cp != null && cp.CanWrite) cp.SetValue(first, true, null);
                    }
                }
                catch { }
                for (int si = 0; si < list.Children.Count && si < secNames.Length; si++)
                {
                    FrameworkElement item = list.Children[si] as FrameworkElement;
                    if (item == null) continue;
                    string sec = secNames[si];
                    item.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(delegate(object s2, MouseButtonEventArgs e2)
                    {
                        try { LastSection = sec; ShowSection(st, sec); } catch (Exception ex) { Log(st, "link section error: " + ex.Message); }
                    }), true);
                }
                SidebarPanel = panel;
                // 只注册一次：PCL 会复用同一个 MyPageLeft，重复注册会抛“重复名称”
                try
                {
                    if (leftPage.FindName("PanItem") == null)
                    {
                        leftPage.RegisterName("PanItem", panel);
                        PropertyInfo ap = leftPage.GetType().GetProperty("AnimatedControl");
                        if (ap != null && ap.CanWrite) ap.SetValue(leftPage, "PanItem", null);
                        Log(st, "link: sidebar registered for native animation");
                    }
                }
                catch (Exception ex3) { Log(st, "link register error: " + ex3.Message); }
                Log(st, "link: sidebar built");
            }
            catch (Exception ex) { Log(st, "link sidebar error: " + ex.Message); }
        }

        /// <summary>分栏切换时来一下 PCL 那样的下沉淡入动画</summary>
        private static void AnimateIn(FrameworkElement fe)
        {
            try
            {
                TranslateTransform tt = fe.RenderTransform as TranslateTransform;
                if (tt == null)
                {
                    tt = new TranslateTransform(0, -16);
                    fe.RenderTransform = tt;
                }
                fe.Opacity = 0;
                tt.Y = -16;
                DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
                fade.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                fe.BeginAnimation(UIElement.OpacityProperty, fade);
                DoubleAnimation slide = new DoubleAnimation(-16, 0, TimeSpan.FromMilliseconds(260));
                slide.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                tt.BeginAnimation(TranslateTransform.YProperty, slide);
            }
            catch { }
        }

        private static void ShowSection(State st, string section)
        {
            if (TeraPage == null) return;
            bool touched = false;
            WalkSections(st, TeraPage, section, ref touched);
            if (!touched) Log(st, "link: show " + section + " (not found)"
                );
        }

        private static void WalkSections(State st, DependencyObject el, string section, ref bool touched)
        {
            FrameworkElement fe = el as FrameworkElement;
            if (fe != null)
            {
                string tag = fe.Tag as string;
                if (tag != null && tag.StartsWith("sec:"))
                {
                    string want = tag.Substring(4);
                    bool show = want == section;
                    Visibility v = show ? Visibility.Visible : Visibility.Collapsed;
                    if (fe.Visibility != v)
                    {
                        fe.Visibility = v;
                        if (show) AnimateIn(fe);
                    }
                    touched = true;
                }
            }
            int n = VisualTreeHelper.GetChildrenCount(el);
            for (int i = 0; i < n; i++) WalkSections(st, VisualTreeHelper.GetChild(el, i), section, ref touched);
        }

        private static FrameworkElement BuildTeraPage(State st)
        {
            try
            {
                string inner = GetEmbeddedText("TCHome.xaml");
                if (string.IsNullOrEmpty(inner)) inner = Fetch(HomeBase + "tcpage.xaml");
                if (string.IsNullOrEmpty(inner)) { Log(st, "link: tcpage empty"); return null; }
                string head = "<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                              "xmlns:sys=\"clr-namespace:System;assembly=mscorlib\" " +
                              "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
                              "xmlns:local=\"clr-namespace:PCL;assembly=Plain Craft Launcher 2\" " +
                              "xmlns:core=\"clr-namespace:MeloongCore;assembly=MeloongCore\" " +
                              "xmlns:corewpf=\"clr-namespace:MeloongCore.Wpf;assembly=MeloongCore.Wpf\">";
                string xaml = head + "<StackPanel Margin=\"25,20,25,20\">" + inner + "</StackPanel></StackPanel>";
                object obj = st.GetObjectFromXML.Invoke(null, new object[] { xaml });
                FrameworkElement page = obj as FrameworkElement;
                if (page != null)
                {
                    Log(st, "link: terracotta panel built");
                    TeraPage = page;
                    PendingSection = LastSection;
                }
                return page;
            }
            catch (Exception ex)
            {
                Exception inner2 = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Log(st, "link build failed: " + inner2.GetType().Name + " " + inner2.Message);
                return null;
            }
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
        private static void SetPropDouble(object obj, string name, double value)
        {
            try
            {
                PropertyInfo p = obj.GetType().GetProperty(name);
                if (p != null && p.CanWrite) p.SetValue(obj, value, null);
            }
            catch { }
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
                    if (action == "zan-open")
                    {
                        // 内嵌了赞赏码就交给 ApplyZanImage 里的「PCL 内放大」，不跳浏览器
                        if (ReadEmbeddedBytes("zan.jpg") != null) return;
                        OpenZan(fe);
                        return;
                    }
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

        private static string ReadInputText(string key)
        {
            return "";
        }

        private static bool NativeTcAction(State st, FrameworkElement fe, string action)
        {
            try
            {
                LoadTcInputs(fe);
                if (action.StartsWith("tc-tog:", StringComparison.Ordinal))
                {
                    TcNative.Toggle(action.Substring(7));
                    return true;
                }
                switch (action)
                {
                    case "tc-host": EnsureTcInstalled(st); if (!TcNative.Host()) return true; return true;
                    case "tc-join":
                        {
                            string code = ReadTcInput("code").Replace(" ", "");
                            if (string.IsNullOrEmpty(code)) { Log(st, "tc: 没有填联机码"); return true; }
                            EnsureTcInstalled(st);
                            TcNative.Join(code);
                            return true;
                        }
                    case "tc-detach": TcNative.Detach(); return true;
                    case "tc-start": EnsureTcInstalled(st); TcNative.Start(); return true;
                    case "tc-stop": TcNative.Stop(); return true;
                    case "tc-copy":
                        {
                            string c = TcNative.Code();
                            if (!string.IsNullOrEmpty(c)) System.Windows.Clipboard.SetText(c);
                            return true;
                        }
                    case "tc-url":
                        {
                            string u = TcNative.Url();
                            if (!string.IsNullOrEmpty(u)) System.Windows.Clipboard.SetText(u);
                            return true;
                        }
                    case "tc-pick":
                        {
                            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
                            dlg.Title = "选择 terracotta.exe";
                            dlg.Filter = "陶瓦联机 (terracotta.exe)|terracotta.exe|可执行文件 (*.exe)|*.exe";
                            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FileName))
                            {
                                TcNative.CustomPath = dlg.FileName;
                                TcNative.SaveSettings();
                            }
                            return true;
                        }
                    case "tc-detect":
                        TcNative.CustomPath = "";
                        TcNative.SaveSettings();
                        return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log(st, "tc action error: " + ex.Message);
                return true;
            }
        }

        private static readonly Dictionary<string, string> TcInputs = new Dictionary<string, string>();

        private static void LoadTcInputs(FrameworkElement fe)
        {
            try
            {
                Window w = Application.Current == null ? null : Application.Current.MainWindow;
                if (w == null) return;
                WalkInputs(w);
            }
            catch { }
        }

        private static void WalkInputs(DependencyObject el)
        {
            FrameworkElement fe = el as FrameworkElement;
            if (fe != null)
            {
                string tag = fe.Tag as string;
                if (tag != null && tag.StartsWith("in:"))
                {
                    TextBox tb = fe as TextBox;
                    if (tb != null) TcInputs[tag.Substring(3)] = tb.Text;
                }
            }
            int n = VisualTreeHelper.GetChildrenCount(el);
            for (int i = 0; i < n; i++) WalkInputs(VisualTreeHelper.GetChild(el, i));
        }

        private static string ReadTcInput(string key)
        {
            string v;
            return TcInputs.TryGetValue(key, out v) ? v : "";
        }

        private static int _installBusy;
        private static string _lastFilledUrl;
        private static void EnsureTcInstalled(State st)
        {
            if (TcNative.ExePath() != null) return;
            if (Interlocked.CompareExchange(ref _installBusy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    bool ok = TcNative.EnsureInstalled();
                    Log(st, "tc install: " + (ok ? "ok" : "failed"));
                }
                finally { Interlocked.Exchange(ref _installBusy, 0); }
            });
        }

        private static void RunAction(FrameworkElement fe, string action)
        {
            State st = FindState(fe);
            if (st != null && action == "zan-open" && IsNativePreset(st))
            {
                try
                {
                    byte[] data = ReadEmbeddedBytes("zan.jpg");
                    if (data != null)
                    {
                        string tmp = Path.Combine(Path.GetTempPath(), "PCL-IMC-zan.jpg");
                        File.WriteAllBytes(tmp, data);
                        System.Diagnostics.Process.Start(tmp);
                    }
                }
                catch (Exception ex) { Log(st, "zan open error: " + ex.Message); }
                return;
            }
            if (st != null && action.StartsWith("tc-", StringComparison.Ordinal))
            {
                if (NativeTcAction(st, fe, action)) return;
            }
            if (action == "memopt" && st != null && IsNativePreset(st))
            {
                ImcNative.MemOptimize(delegate(string text) { Log(st, "memopt: " + text); });
                return;
            }
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
                if (st == null) return;   // 没有状态就不弹任何网页（面板我们自己画了）
                ((DispatcherObject)st.Page).Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(delegate
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(code)) Clipboard.SetText(code);
                            // 不再打开陶瓦网页面板（我们已经有内置的联机页了）
                        }
                        catch (Exception ex) { Log(st, "action ui error: " + ex.Message); }
                    }));
            });
        }

        private static State FindState(FrameworkElement fe)
        {
            State only = null;
            foreach (KeyValuePair<object, State> kv in States) only = kv.Value;
            foreach (KeyValuePair<object, State> kv in States)
            {
                Panel p = kv.Value.PanCustom == null ? null : kv.Value.PanCustom.GetValue(kv.Value.Page) as Panel;
                if (p == null) continue;
                if (IsDescendant(p, fe)) return kv.Value;
            }
            return only;   // 联机页不在 PanCustom 里，只有一个状态时直接用它
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
                if (st.Page == null) return;
                bool pageVisible = ((UIElement)st.Page).IsVisible;

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

                if (pageVisible) st.Styled = StyleGlassCards(st);
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
        // ---------------- 全局毛玻璃（个性化里的开关） ----------------
        // 开关状态存在 PCL\imc-glass.txt；打开后把 PCL 每个 MyCard 的白底换成半透明，
        // 背景图就能透出来（自定义主页那套也是同一个思路）。

        private static bool GlassAll;
        private static bool GlassLoaded;
        private static readonly Dictionary<DependencyObject, Brush> GlassOrigin = new Dictionary<DependencyObject, Brush>();
        private static readonly Dictionary<DependencyObject, bool> GlassAnimOrigin = new Dictionary<DependencyObject, bool>();
        private static readonly Dictionary<DependencyObject, Visibility> GlassChromeOrigin = new Dictionary<DependencyObject, Visibility>();
        private static bool GlassSwitchDone;
        private static WeakReference GlassCardRef;
        private static int GlassTick;

        private static bool IsGlassAll()
        {
            if (!GlassLoaded)
            {
                GlassLoaded = true;
                try
                {
                    string path = Path.Combine(AppDir(), "PCL", "imc-glass.txt");
                    if (File.Exists(path)) GlassAll = File.ReadAllText(path).Trim() == "1";
                }
                catch { }
            }
            return GlassAll;
        }

        private static void SetGlassAll(State st, bool on)
        {
            GlassAll = on;
            GlassLoaded = true;
            try
            {
                string path = Path.Combine(AppDir(), "PCL", "imc-glass.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, on ? "1" : "0");
            }
            catch { }
            Log(st, "glass: " + (on ? "on" : "off"));
            ApplyGlassAll(st);
        }

        private static void ApplyGlassAll(State st)
        {
            try
            {
                Window w = Application.Current == null ? null : Application.Current.MainWindow;
                if (w == null) return;
                GlassWalk(w);
                // 左边栏（整块白色 Border）也做成半透明
                FrameworkElement side = FindByName(w, "PanMainLeft");
                if (side is Border)
                {
                    GlassBorder(side, (Border)side);
                    int sc = System.Windows.Media.VisualTreeHelper.GetChildrenCount(side);
                    for (int si = 0; si < sc; si++)
                    {
                        Border inner = System.Windows.Media.VisualTreeHelper.GetChild(side, si) as Border;
                        if (inner != null) GlassBorder(inner, inner);
                    }
                }
            }
            catch (Exception ex) { Log(st, "glass error: " + ex.Message); }
        }

        private static FrameworkElement FindByName(DependencyObject root, string name)
        {
            FrameworkElement fe = root as FrameworkElement;
            if (fe != null && fe.Name == name) return fe;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                FrameworkElement hit = FindByName(VisualTreeHelper.GetChild(root, i), name);
                if (hit != null) return hit;
            }
            return null;
        }
        private static void GlassClear(DependencyObject key, Border b)
        {
            try
            {
                if (GlassAll)
                {
                    if (!GlassOrigin.ContainsKey(key)) GlassOrigin[key] = b.Background;
                    b.Background = null;   // 左边栏整块底板直接抠掉，背景图才透得出来
                }
                else if (GlassOrigin.ContainsKey(key))
                {
                    b.Background = GlassOrigin[key];
                    GlassOrigin.Remove(key);
                }
            }
            catch { }
        }

        private static void GlassBorder(DependencyObject key, Border b)
        {
            try
            {
                if (GlassAll)
                {
                    if (!GlassOrigin.ContainsKey(key)) GlassOrigin[key] = b.Background;
                    {
                        b.Background = new SolidColorBrush(Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF));
                        TryLog("glass border: " + (b.Name ?? "") + " " + (int)b.ActualWidth + "x" + (int)b.ActualHeight);
                    }
                }
                else if (GlassOrigin.ContainsKey(key))
                {
                    b.Background = GlassOrigin[key];
                    GlassOrigin.Remove(key);
                }
            }
            catch { }
        }
        private static void MaybeGlassWhite(Border b)
        {
            try
            {
                SolidColorBrush sb = b.Background as SolidColorBrush;
                if (sb == null) return;
                Color c = sb.Color;
                if (c.A != 255) return;                       // 已经半透明，别重复处理
                if (c.R < 235 || c.G < 235 || c.B < 235) return; // 只处理接近白色的底板
                if (b.ActualWidth * b.ActualHeight < 4000) return; // 按钮之类的小块不动
                GlassBorder(b, b);
            }
            catch { }
        }

        private static void GlassWalk(DependencyObject el)
        {
            if (el == null) return;
            try
            {
                object obj = el;
                Border anyBorder = el as Border;
                FrameworkElement anyFe = el as FrameworkElement;
                if (anyBorder != null && anyFe != null && anyFe.Name == "PanMainLeft")
                {
                    // 左栏有好几层叫 PanMainLeft 的 Border，全部抠成透明
                    GlassClear(el, anyBorder);
                    int cc = System.Windows.Media.VisualTreeHelper.GetChildrenCount(el);
                    for (int ci = 0; ci < cc; ci++)
                    {
                        Border cb = System.Windows.Media.VisualTreeHelper.GetChild(el, ci) as Border;
                        if (cb != null) GlassClear(cb, cb);
                    }
                }
                // 任何带 Background 的控件，只要是接近纯白的大块，都换成半透明
                try
                {
                    PropertyInfo bgp = el.GetType().GetProperty("Background");
                    if (bgp != null && bgp.CanWrite && bgp.PropertyType == typeof(Brush))
                    {
                        SolidColorBrush gsb = bgp.GetValue(el, null) as SolidColorBrush;
                        FrameworkElement gfe = el as FrameworkElement;
                        if (gsb != null && gfe != null && gsb.Color.A == 255
                            && gsb.Color.R >= 235 && gsb.Color.G >= 235 && gsb.Color.B >= 235
                            && gfe.ActualWidth * gfe.ActualHeight > 4000)
                        {
                            if (GlassAll)
                            {
                                if (!GlassOrigin.ContainsKey(el)) GlassOrigin[el] = gsb;
                                bgp.SetValue(el, new SolidColorBrush(Color.FromArgb(0x9C, 0xFF, 0xFF, 0xFF)), null);
                            }
                            else if (GlassOrigin.ContainsKey(el))
                            {
                                bgp.SetValue(el, GlassOrigin[el], null);
                                GlassOrigin.Remove(el);
                            }
                        }
                    }
                }
                catch { }
                Panel anyPanel = el as Panel;
                if (anyPanel != null && !(anyPanel is VirtualizingPanel))
                {
                    SolidColorBrush pb = anyPanel.Background as SolidColorBrush;
                    if (pb != null && pb.Color.A == 255 && pb.Color.R >= 235 && pb.Color.G >= 235 && pb.Color.B >= 235
                        && anyPanel.ActualWidth * anyPanel.ActualHeight > 4000)
                    {
                        if (GlassAll)
                        {
                            if (!GlassOrigin.ContainsKey(el)) GlassOrigin[el] = pb;
                            anyPanel.Background = new SolidColorBrush(Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF));
                        }
                        else if (GlassOrigin.ContainsKey(el))
                        {
                            anyPanel.Background = GlassOrigin[el];
                            GlassOrigin.Remove(el);
                        }
                    }
                }
                if (anyBorder != null)
                {
                    if (GlassAll) MaybeGlassWhite(anyBorder);
                    else if (GlassOrigin.ContainsKey(el))
                    {
                        anyBorder.Background = GlassOrigin[el];
                        GlassOrigin.Remove(el);
                    }
                }
                // 左边栏不是 MyCard，是一整块 Border，单独处理

                if (obj.GetType().FullName == "PCL.MyCard")
                {
                    FieldInfo fi = obj.GetType().GetField("MainBorder", BindingFlags.Instance | BindingFlags.NonPublic);
                    Border border = fi == null ? null : fi.GetValue(obj) as Border;
                    if (border != null)
                    {
                        if (GlassAll)
                        {
                            if (!GlassOrigin.ContainsKey(el))
                            {
                                GlassOrigin[el] = border.Background;
                                border.Background = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
                                // 关掉悬停动画：鼠标移上去卡片会被染成蓝色，透过半透明看很明显；
                                // 阴影层也藏掉，否则它会盖在毛玻璃上面
                                PropertyInfo anim = el.GetType().GetProperty("HasMouseAnimation");
                                if (anim != null && anim.CanWrite)
                                {
                                    object av = anim.GetValue(el, null);
                                    GlassAnimOrigin[el] = av is bool && (bool)av;
                                    if (av is bool && (bool)av) anim.SetValue(el, false, null);
                                }
                                FieldInfo cf = el.GetType().GetField("_MainChrome", BindingFlags.Instance | BindingFlags.NonPublic);
                                UIElement chrome = cf == null ? null : cf.GetValue(el) as UIElement;
                                if (chrome != null)
                                {
                                    GlassChromeOrigin[el] = chrome.Visibility;
                                    chrome.Visibility = Visibility.Collapsed;
                                }
                            }
                        }
                        else
                        {
                            if (GlassOrigin.ContainsKey(el))
                            {
                                border.Background = GlassOrigin[el];
                                GlassOrigin.Remove(el);
                            }
                            PropertyInfo anim2 = el.GetType().GetProperty("HasMouseAnimation");
                            if (anim2 != null && anim2.CanWrite && GlassAnimOrigin.ContainsKey(el))
                            {
                                anim2.SetValue(el, GlassAnimOrigin[el], null);
                                GlassAnimOrigin.Remove(el);
                            }
                            if (GlassChromeOrigin.ContainsKey(el))
                            {
                                FieldInfo cf2 = el.GetType().GetField("_MainChrome", BindingFlags.Instance | BindingFlags.NonPublic);
                                UIElement chrome2 = cf2 == null ? null : cf2.GetValue(el) as UIElement;
                                if (chrome2 != null) chrome2.Visibility = GlassChromeOrigin[el];
                                GlassChromeOrigin.Remove(el);
                            }
                        }
                    }
                }
                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(el);
                for (int i = 0; i < n; i++) GlassWalk(System.Windows.Media.VisualTreeHelper.GetChild(el, i));
            }
            catch { }
        }

        /// <summary>个性化页顶部插一个「全局毛玻璃」勾选框（PCL 自己的页面，只加得进去一整个卡片）</summary>
        private static void EnsureGlassSwitch(State st)
        {
            if (GlassSwitchDone)
            {
                FrameworkElement old = GlassCardRef == null ? null : GlassCardRef.Target as FrameworkElement;
                if (old != null && old.IsLoaded && VisualTreeHelper.GetParent(old) != null) return;
                GlassSwitchDone = false;   // 页面被 PCL 重建了，重插一次
            }
            Window w = Application.Current == null ? null : Application.Current.MainWindow;
            if (w == null) return;
            FrameworkElement anchor = FindByText(w, "背景音乐");
            if (anchor == null) return;              // 不在个性化页
            try
            {
                // 往上找卡片所在的 StackPanel
                DependencyObject child = anchor;
                DependencyObject parent = VisualTreeHelper.GetParent(child);
                Panel host = null;
                int guard = 0;
                while (parent != null && guard++ < 12)
                {
                    StackPanel sp = parent as StackPanel;
                    if (sp != null) { host = sp; break; }
                    child = parent;
                    parent = VisualTreeHelper.GetParent(parent);
                }
                if (host == null) { Log(st, "glass: settings host not found"); return; }

                // 已经插过就别再插（PCL 重排时 IsLoaded 会短暂为 false，容易插重复）
                foreach (UIElement old in host.Children)
                {
                    FrameworkElement ofe = old as FrameworkElement;
                    if (ofe != null && (ofe.Tag as string) == "imc-glass-card")
                    {
                        GlassSwitchDone = true;
                        GlassCardRef = new WeakReference(ofe);
                        return;
                    }
                }

                Border card = new Border();
                card.Tag = "imc-glass-card";
                card.CornerRadius = new CornerRadius(8);
                card.Background = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
                card.Margin = new Thickness(0, 0, 0, 12);
                card.Padding = new Thickness(16, 12, 16, 12);
                StackPanel inner = new StackPanel();
                card.Child = inner;

                Type ckType = null;
                try
                {
                    Assembly asm = w.GetType().Assembly;
                    ckType = asm.GetType("PCL.MyCheckBox");
                }
                catch { }
                if (ckType == null) { Log(st, "glass: MyCheckBox type missing"); return; }
                object ck = Activator.CreateInstance(ckType);
                FrameworkElement ckFe = ck as FrameworkElement;
                SetProp(ck, "Text", "所有页面使用毛玻璃（半透明卡片）");
                try
                {
                    PropertyInfo cp = ckType.GetProperty("Checked");
                    if (cp != null && cp.CanWrite) cp.SetValue(ck, IsGlassAll(), null);
                }
                catch { }
                inner.Children.Add(ckFe);
                TextBlock tip = new TextBlock();
                tip.Text = "打开后 PCL 所有页面的卡片都会变成半透明，背景图能透出来；改完立即生效。";
                tip.FontSize = 11;
                tip.Opacity = 0.7;
                tip.TextWrapping = TextWrapping.Wrap;
                tip.Margin = new Thickness(0, 6, 0, 0);
                inner.Children.Add(tip);

                ckFe.AddHandler(UIElement.MouseLeftButtonUpEvent,
                    new MouseButtonEventHandler(delegate(object s, MouseButtonEventArgs e)
                    {
                        try
                        {
                            bool now = !IsGlassAll();
                            SetProp(ck, "Checked", now);
                            SetGlassAll(st, now);
                        }
                        catch { }
                    }), true);

                host.Children.Insert(0, card);
                GlassSwitchDone = true;
                GlassCardRef = new WeakReference(card);
                Log(st, "glass: switch inserted into settings, host=" + host.Name + " count=" + host.Children.Count);
            }
            catch (Exception ex) { Log(st, "glass switch error: " + ex.Message); }
        }
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
