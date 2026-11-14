using System;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

/// <summary>
/// 把 PCLPatch.dll 作为资源嵌进 exe，并在 PageLaunchRight.Init 开头插入 ImcLoader.Boot(this)。
/// 这样运行时不需要 exe 旁边再放一个 PCLPatch.dll。
/// </summary>
class LoaderPatch
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length < 3)
            {
                Console.WriteLine("usage: LoaderPatch <exe-with-loader> <PCLPatch.dll> <out.exe>");
                return 2;
            }
            string src = args[0], patchDll = args[1], dst = args[2];

            ModuleDefMD mod = ModuleDefMD.Load(src);
            TypeDef loader = mod.Find("ImcLoader", false);
            if (loader == null) { Console.WriteLine("ImcLoader not merged"); return 3; }
            MethodDef boot = loader.FindMethod("Boot");
            if (boot == null) { Console.WriteLine("Boot not found"); return 3; }

            // 1) 嵌入 PCLPatch.dll 资源
            byte[] bytes = System.IO.File.ReadAllBytes(patchDll);
            mod.Resources.Add(new EmbeddedResource("PCLPatch.dll", bytes, ManifestResourceAttributes.Public));
            Console.WriteLine("embedded resource: " + bytes.Length + " bytes");

            // 2) Init 开头插入 Boot(this)
            TypeDef plr = mod.Find("PCL.PageLaunchRight", false);
            if (plr == null) { Console.WriteLine("PageLaunchRight not found"); return 4; }
            MethodDef init = plr.FindMethod("Init");
            if (init == null || !init.HasBody) { Console.WriteLine("Init not found"); return 4; }

            CilBody body = init.Body;
            body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldarg_0));
            body.Instructions.Insert(1, Instruction.Create(OpCodes.Call, boot));
            body.MaxStack = (ushort)(body.MaxStack + 1);
            body.UpdateInstructionOffsets();
            Console.WriteLine("inserted Boot at init start");

            mod.Write(dst);
            Console.WriteLine("written: " + dst);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED: " + ex.Message);
            return 1;
        }
    }
}
