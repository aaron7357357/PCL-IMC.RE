using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

class Installer
{
    const string ExeName = "Plain Craft Launcher 2.exe";
    const string DllName = "PCLPatch.dll";
    const string BackupName = "Plain Craft Launcher 2.original.exe";
    const string HomeUrl = "http://127.0.0.1:8848/motd.xaml";

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        bool silent = args.Length > 0 && args[0].Equals("/silent", StringComparison.OrdinalIgnoreCase);
        try
        {
            string dir = (silent && args.Length > 1) ? args[1] : FindPclDir();
            if (dir == null) return 1;
            if (silent && !File.Exists(Path.Combine(dir, ExeName))) return 2;

            string target = Path.Combine(dir, ExeName);
            string dllTarget = Path.Combine(dir, DllName);
            string backup = Path.Combine(dir, BackupName);

            if (!silent && MessageBox.Show(
                "即将把补丁安装到：\n" + dir + "\n\n" +
                "· 原版程序会备份为 " + BackupName + "\n" +
                "· 写入新的 " + ExeName + " 和 " + DllName + "\n" +
                "· 把 PCL 主页设置为本地服务：" + HomeUrl + "\n\n" +
                "如果 PCL 正在运行，请先完全退出，否则文件会被占用。\n\n继续吗？",
                "PCL2 主页自动刷新补丁 · 安装", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return 0;

            if (IsRunning(target))
            {
                if (!silent) MessageBox.Show("检测到 PCL 正在运行，请先完全退出 PCL（任务栏图标也退出）再安装。",
                    "无法安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 3;
            }

            if (File.Exists(target) && !File.Exists(backup))
                File.Copy(target, backup, false);

            WriteResource("PCL2.Patch.Exe", target);
            WriteResource("PCL2.Patch.Dll", dllTarget);
            ConfigureHomepage(dir);

            if (!silent) MessageBox.Show("安装完成。\n\n主页已设置为：" + HomeUrl +
                "\n\n现在可以启动 PCL，进入主页就会自动刷新。",
                "PCL2 主页自动刷新补丁", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "installer-error.log"), ex.ToString());
            }
            catch { }
            if (!silent) MessageBox.Show("安装失败：" + ex.Message + "\n\n请确认 PCL 已完全退出后重试。",
                "PCL2 主页自动刷新补丁", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static void ConfigureHomepage(string dir)
    {
        string pclDir = Path.Combine(dir, "PCL");
        Directory.CreateDirectory(pclDir);
        string ini = Path.Combine(pclDir, "Setup.ini");

        var lines = new List<string>();
        if (File.Exists(ini))
        {
            try { File.Copy(ini, ini + ".bak", true); } catch { }
            lines.AddRange(File.ReadAllLines(ini, Encoding.UTF8));
        }

        SetKey(lines, "UiCustomType", "2");
        SetKey(lines, "UiCustomNet", HomeUrl);

        File.WriteAllLines(ini, lines.ToArray(), new UTF8Encoding(false));
    }

    static void SetKey(List<string> lines, string key, string value)
    {
        string prefix = key + ":";
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = prefix + value;
                return;
            }
        }
        lines.Add(prefix + value);
    }

    static bool IsRunning(string exePath)
    {
        try
        {
            foreach (Process p in Process.GetProcessesByName("Plain Craft Launcher 2"))
            {
                try
                {
                    if (string.Equals(p.MainModule.FileName, exePath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { return true; }
            }
        }
        catch { }
        return false;
    }

    static string FindPclDir()
    {
        string self = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (File.Exists(Path.Combine(self, ExeName))) return self;

        string[] guesses = { @"D:\awa", @"C:\PCL", @"D:\PCL" };
        foreach (string g in guesses)
            if (File.Exists(Path.Combine(g, ExeName))) return g;

        using (var dlg = new FolderBrowserDialog())
        {
            dlg.Description = "请选择 PCL2 所在的文件夹（里面有 Plain Craft Launcher 2.exe）";
            if (dlg.ShowDialog() != DialogResult.OK) return null;
            if (!File.Exists(Path.Combine(dlg.SelectedPath, ExeName)))
            {
                MessageBox.Show("这个文件夹里没有找到 " + ExeName + "。", "未找到 PCL",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            return dlg.SelectedPath;
        }
    }

    static void WriteResource(string name, string path)
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (s == null) throw new Exception("安装包缺少资源：" + name);
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                s.CopyTo(fs);
        }
    }
}

