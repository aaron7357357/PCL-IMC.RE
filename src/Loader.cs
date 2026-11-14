using System;
using System.IO;
using System.Reflection;

/// <summary>
/// 单文件加载器：PCLPatch.dll 以资源形式内嵌在 exe 里，
/// 启动时注册 AssemblyResolve，把内存中的字节交给 CLR，实现「只要一个 exe」。
/// </summary>
public static class ImcLoader
{
    public static void Boot(object app)
    {
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        }
        catch
        {
        }
    }

    private static Assembly OnResolve(object sender, ResolveEventArgs e)
    {
        try
        {
            if (e == null || string.IsNullOrEmpty(e.Name)) return null;
            if (!e.Name.StartsWith("PCLPatch", StringComparison.OrdinalIgnoreCase)) return null;
            byte[] data = ReadPatch();
            if (data == null) return null;
            return Assembly.Load(data);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ReadPatch()
    {
        try
        {
            Assembly self = typeof(ImcLoader).Assembly;
            using (Stream s = self.GetManifestResourceStream("PCLPatch.dll"))
            {
                if (s == null) return null;
                using (MemoryStream ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }
        catch
        {
            return null;
        }
    }
}
