using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PCLPatch
{
    /// <summary>
    /// v1.3 的小彩蛋集合：
    ///  1. 连点 banner 5 次 → 屏幕飘落 IMC 方块
    ///  2. 深夜（23:00–5:00）状态卡的提示语变化（在 ImcNative 里）
    ///  3. 在线人数 ≥100/200 → 状态卡描金边 + 撒花
    ///  4. 连点「关于」标题 3 次 → 展开隐藏卡片
    ///  5. 键盘依次按 I M C → 弹出彩蛋窗
    ///  6. 联机创建成功后邀请码下方随机一句梗（在联机页处理）
    ///  7. 周年日：读 PCL\imc-egg.txt 的 anniversary=YYYY-MM-DD（在 ImcNative 里）
    ///  8. 连点窗口标题 3 次 → 标题临时变成「PCL IMC.RE · 来玩呀」
    /// </summary>
    public static class Egg
    {
        public static Action<string> LogHook;
        private static readonly Random Rnd = new Random();

        private static void Log(string msg)
        {
            try { if (LogHook != null) LogHook("egg: " + msg); } catch { }
        }

        // ---------------- 通用查找 ----------------

        public static FrameworkElement FindTag(DependencyObject root, string tag)
        {
            FrameworkElement fe = root as FrameworkElement;
            if (fe != null && (fe.Tag as string) == tag) return fe;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                FrameworkElement hit = FindTag(VisualTreeHelper.GetChild(root, i), tag);
                if (hit != null) return hit;
            }
            return null;
        }

        private static FrameworkElement FindName(DependencyObject root, string name)
        {
            FrameworkElement fe = root as FrameworkElement;
            if (fe != null && fe.Name == name) return fe;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                FrameworkElement hit = FindName(VisualTreeHelper.GetChild(root, i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        public static byte[] ReadRes(string name)
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

        // ---------------- banner ----------------

        public static void ApplyBanner(FrameworkElement page)
        {
            try
            {
                FrameworkElement holder = FindTag(page, "banner");
                System.Windows.Controls.Image img = holder as System.Windows.Controls.Image;
                if (img == null) return;
                byte[] data = ReadRes("banner.png");
                if (data == null) return;
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(data);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                img.Source = bmp;
                Log("banner embedded");
            }
            catch (Exception ex) { Log("banner error: " + ex.Message); }
        }

        // ---------------- 点击计数 ----------------

        private static int _bannerClicks;
        private static DateTime _bannerFirst = DateTime.MinValue;
        private static int _logoClicks;
        private static DateTime _logoFirst = DateTime.MinValue;

        public static void HookPage(Window win, FrameworkElement page)
        {
            try
            {
                HookBanner(win, page);
                HookAbout(win, page);
                HookTitle(win);
                HookKeys(win, page);
            }
            catch (Exception ex) { Log("hook error: " + ex.Message); }
        }

        private static void HookBanner(Window win, FrameworkElement page)
        {
            FrameworkElement banner = FindTag(page, "banner");
            if (banner == null) return;
            banner.Cursor = Cursors.Hand;
            banner.MouseLeftButtonUp += delegate
            {
                if ((DateTime.Now - _bannerFirst).TotalSeconds > 3) { _bannerClicks = 0; _bannerFirst = DateTime.Now; }
                _bannerClicks++;
                if (_bannerClicks >= 5)
                {
                    _bannerClicks = 0;
                    Log("banner 5 clicks");
                    Confetti(win, 26, true);
                }
            };
        }

        private static void HookAbout(Window win, FrameworkElement page)
        {
            FrameworkElement logo = FindTag(page, "egglogo");
            if (logo == null) return;
            logo.Cursor = Cursors.Hand;
            logo.MouseLeftButtonUp += delegate
            {
                if ((DateTime.Now - _logoFirst).TotalSeconds > 3) { _logoClicks = 0; _logoFirst = DateTime.Now; }
                _logoClicks++;
                if (_logoClicks >= 3)
                {
                    _logoClicks = 0;
                    FrameworkElement card = FindTag(page, "eggcard");
                    if (card != null)
                    {
                        card.Visibility = Visibility.Visible;
                        card.BringIntoView();
                        Log("egg card shown");
                    }
                }
            };
        }

        private static void HookTitle(Window win)
        {
            // 窗口级监听：PCL 的标题栏要负责拖动窗口，挂在 PanTitle 上会被拖拽逻辑吞掉
            int count = 0;
            DateTime first = DateTime.MinValue;
            win.PreviewMouseLeftButtonDown += new MouseButtonEventHandler(delegate(object s2, MouseButtonEventArgs e2)
            {
                try
                {
                    Point p = e2.GetPosition(win);
                    if (p.Y > 50 || p.X > 300) return;
                    if ((DateTime.Now - first).TotalSeconds > 4) { count = 0; first = DateTime.Now; }
                    count++;
                    Log("title click " + count + " at " + (int)p.X + "," + (int)p.Y);
                    if (count < 3) return;
                    count = 0;
                    FrameworkElement lab = FindName(win, "LabTitleLogo");
                    TextBlock tb = lab as TextBlock;
                    if (tb == null || string.IsNullOrEmpty(tb.Text)) { Log("title text not found"); return; }
                    string old = tb.Text;
                    tb.Text = "PCL IMC.RE · 来玩呀";
                    Log("title easter egg");
                    DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                    t.Tick += delegate { t.Stop(); try { tb.Text = old; } catch { } };
                    t.Start();
                }
                catch (Exception ex) { Log("title error: " + ex.Message); }
            });
        }

        private static string _keys = "";
        private static DateTime _keysTime = DateTime.MinValue;

        private static void HookKeys(Window win, FrameworkElement page)
        {
            win.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if ((DateTime.Now - _keysTime).TotalSeconds > 2) _keys = "";
                _keysTime = DateTime.Now;
                string ch = "";
                if (e.Key == Key.I) ch = "i";
                else if (e.Key == Key.M) ch = "m";
                else if (e.Key == Key.C) ch = "c";
                else { _keys = ""; return; }
                _keys += ch;
                if (_keys.Length > 3) _keys = _keys.Substring(_keys.Length - 3);
                if (_keys == "imc")
                {
                    _keys = "";
                    Log("keyboard imc");
                    ShowEggWindow(win);
                }
            };
        }

        // ---------------- 彩蛋窗口 ----------------

        private static readonly string[] Lines = new string[]
        {
            "IMC.RE —— 欢迎回来。",
            "这个彩蛋是给愿意乱按键盘的人准备的。",
            "服务器地址：play.imc.re",
            "今天也要玩得开心。"
        };

        public static void ShowEggWindow(Window owner)
        {
            try
            {
                Window win = new Window();
                win.WindowStyle = WindowStyle.None;
                win.AllowsTransparency = true;
                win.Background = new SolidColorBrush(Color.FromArgb(0xE0, 12, 22, 34));
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
                    win.Width = 720;
                    win.Height = 420;
                }
                StackPanel panel = new StackPanel();
                panel.VerticalAlignment = VerticalAlignment.Center;
                panel.HorizontalAlignment = HorizontalAlignment.Center;
                TextBlock big = new TextBlock();
                big.Text = "IMC.RE";
                big.FontSize = 46;
                big.FontWeight = FontWeights.Bold;
                big.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xB3, 0x1C));
                big.HorizontalAlignment = HorizontalAlignment.Center;
                big.Margin = new Thickness(0, 0, 0, 18);
                panel.Children.Add(big);
                foreach (string line in Lines)
                {
                    TextBlock tb = new TextBlock();
                    tb.Text = line;
                    tb.FontSize = 15;
                    tb.Margin = new Thickness(0, 0, 0, 8);
                    tb.HorizontalAlignment = HorizontalAlignment.Center;
                    tb.Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
                    panel.Children.Add(tb);
                }
                TextBlock tip = new TextBlock();
                tip.Text = "点击任意处关闭";
                tip.FontSize = 12;
                tip.Margin = new Thickness(0, 18, 0, 0);
                tip.HorizontalAlignment = HorizontalAlignment.Center;
                tip.Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
                panel.Children.Add(tip);
                win.Content = panel;
                win.MouseDown += delegate { win.Close(); };
                win.KeyDown += delegate(object s3, KeyEventArgs e3) { if (e3.Key == Key.Escape) win.Close(); };
                win.Topmost = true;
                win.Loaded += delegate { win.Activate(); win.Focus(); };
                win.Show();
            }
            catch (Exception ex) { Log("egg window error: " + ex.Message); }
        }

        // ---------------- 撒花 / 飘落 ----------------

        public static void Confetti(Window win, int count, bool blocks)
        {
            try
            {
                Panel root = win == null ? null : win.Content as Panel;
                if (root == null) return;
                Canvas canvas = new Canvas();
                canvas.IsHitTestVisible = false;
                canvas.Width = root.ActualWidth;
                canvas.Height = root.ActualHeight;
                Grid.SetRowSpan(canvas, 99);
                root.Children.Add(canvas);
                string[] glyphs = blocks
                    ? new string[] { "IMC", "✦", "▉", "▲" }
                    : new string[] { "✦", "★", "●", "▉" };
                Color[] colors = new Color[]
                {
                    Color.FromRgb(0xF2, 0xB3, 0x1C), Color.FromRgb(0xFF, 0x6B, 0x35),
                    Color.FromRgb(0x3E, 0xC6, 0x8C), Color.FromRgb(0x4A, 0x9B, 0xFF)
                };
                for (int i = 0; i < count; i++)
                {
                    TextBlock tb = new TextBlock();
                    tb.Text = glyphs[Rnd.Next(glyphs.Length)];
                    tb.FontSize = 12 + Rnd.Next(10);
                    tb.FontWeight = FontWeights.Bold;
                    Color c = colors[Rnd.Next(colors.Length)];
                    tb.Foreground = new SolidColorBrush(Color.FromArgb(0xDD, c.R, c.G, c.B));
                    double x = Rnd.NextDouble() * Math.Max(1, canvas.Width - 60);
                    Canvas.SetLeft(tb, x);
                    Canvas.SetTop(tb, -40);
                    canvas.Children.Add(tb);
                    double duration = 1.6 + Rnd.NextDouble() * 1.4;
                    DoubleAnimation fall = new DoubleAnimation(-40, canvas.Height + 60, TimeSpan.FromSeconds(duration));
                    fall.BeginTime = TimeSpan.FromSeconds(Rnd.NextDouble() * 0.8);
                    fall.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn };
                    tb.BeginAnimation(Canvas.TopProperty, fall);
                    DoubleAnimation fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(duration * 0.5));
                    fade.BeginTime = fall.BeginTime + TimeSpan.FromSeconds(duration * 0.5);
                    tb.BeginAnimation(UIElement.OpacityProperty, fade);
                }
                DispatcherTimer clean = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                clean.Tick += delegate
                {
                    clean.Stop();
                    try { root.Children.Remove(canvas); } catch { }
                };
                clean.Start();
            }
            catch (Exception ex) { Log("confetti error: " + ex.Message); }
        }

        // ---------------- 在线人数：金边 + 撒花 ----------------

        private static int _lastThreshold;

        public static void OnLive(Window win, FrameworkElement page, string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json) || !json.Contains("\"status\":\"on\"")) return;
                Match m = Regex.Match(json, "在线：(\\d+)");
                int online = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                int threshold = online >= 200 ? 200 : (online >= 100 ? 100 : 0);
                FrameworkElement holder = FindTag(page, "goldcard");
                Border card = holder as Border;
                if (card != null)
                {
                    if (threshold > 0)
                    {
                        LinearGradientBrush brush = new LinearGradientBrush();
                        brush.StartPoint = new Point(0, 0);
                        brush.EndPoint = new Point(1, 1);
                        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xD7, 0x6E), 0));
                        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0xB3, 0x1C), 0.5));
                        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xB3, 0x4D), 1));
                        card.BorderBrush = brush;
                        card.BorderThickness = new Thickness(2);
                    }
                    else
                    {
                        card.ClearValue(Border.BorderBrushProperty);
                        card.BorderThickness = new Thickness(1);
                    }
                }
                if (threshold > _lastThreshold)
                {
                    _lastThreshold = threshold;
                    Log("online threshold " + threshold);
                    Confetti(win, threshold >= 200 ? 46 : 30, false);
                }
                else if (threshold == 0) _lastThreshold = 0;
            }
            catch (Exception ex) { Log("live egg error: " + ex.Message); }
        }

        // ---------------- 联机彩蛋文案 ----------------

        private static readonly string[] TcLines = new string[]
        {
            "房间开好了，去把朋友拉进来吧。",
            "记得让朋友也用 IMC.RE 启动器，省事。",
            "如果连不上，先看看防火墙。",
            "陶瓦：我尽力了，剩下的看网络。"
        };

        private static string _lastTcLine = "";

        public static void OnTc(FrameworkElement page, string code)
        {
            try
            {
                if (string.IsNullOrEmpty(code)) return;
                FrameworkElement holder = FindTag(page, "egghint");
                TextBlock tb = holder as TextBlock;
                if (tb == null) return;
                if (_lastTcLine == "") _lastTcLine = TcLines[Rnd.Next(TcLines.Length)];
                tb.Text = _lastTcLine;
                tb.Visibility = Visibility.Visible;
            }
            catch { }
        }
    }
}
