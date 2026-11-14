using System;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

/// <summary>
/// 合并后的 exe 里已经有 PCLPatch.Bootstrap 类型，这里直接在 PageLaunchRight.Init 里插入对它的调用。
/// </summary>
class SelfLink
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: SelfLink <exe> <out.exe>");
                return 2;
            }
            ModuleDefMD mod = ModuleDefMD.Load(args[0]);

            TypeDef bs = mod.Find("PCLPatch.Bootstrap", false);
            if (bs == null) { Console.WriteLine("Bootstrap type missing"); return 3; }
            MethodDef install = bs.FindMethod("Install");
            if (install == null) { Console.WriteLine("Install missing"); return 3; }

            TypeDef plr = mod.Find("PCL.PageLaunchRight", false);
            MethodDef init = plr == null ? null : plr.FindMethod("Init");
            if (init == null || !init.HasBody) { Console.WriteLine("Init missing"); return 4; }

            CilBody body = init.Body;
            var instrs = body.Instructions;
            // 已经有调用就不重复插
            foreach (Instruction i in instrs)
            {
                if ((i.OpCode == OpCodes.Call) && i.Operand is MethodDef && ((MethodDef)i.Operand).Name == "Install")
                {
                    Console.WriteLine("already linked");
                    mod.Write(args[1]);
                    return 0;
                }
            }

            int retIdx = -1;
            for (int i = instrs.Count - 1; i >= 0; i--) { if (instrs[i].OpCode == OpCodes.Ret) { retIdx = i; break; } }
            if (retIdx < 0) { Console.WriteLine("ret missing"); return 5; }

            instrs.Insert(retIdx, Instruction.Create(OpCodes.Ldarg_0));
            instrs.Insert(retIdx + 1, Instruction.Create(OpCodes.Call, install));
            body.MaxStack = (ushort)(body.MaxStack + 1);
            body.UpdateInstructionOffsets();
            Console.WriteLine("linked Install at index " + retIdx + " (total " + instrs.Count + ")");

            mod.Write(args[1]);
            Console.WriteLine("written: " + args[1]);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED: " + ex.ToString());
            return 1;
        }
    }
}
