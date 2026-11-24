using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace FModelCnPatcher;

public static class WpfRenderFixer
{
    public static bool Apply(ModuleDefMD fmodelModule)
    {
        var appType = fmodelModule.Find("FModel.App", false);
        var onStartup = appType?.Methods.FirstOrDefault(m => m.Name == "OnStartup" && m.HasBody);
        if (onStartup == null)
            return false;

        var instrs = onStartup.Body.Instructions;
        if (instrs.Any(ins =>
                (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt)
                && ins.Operand is IMethod m
                && m.Name == "set_ProcessRenderMode"))
        {
            return true;
        }

        var pcRef = fmodelModule.GetAssemblyRef("PresentationCore")
                    ?? new AssemblyRefUser("PresentationCore",
                        fmodelModule.CorLibTypes.AssemblyRef.Version,
                        new PublicKeyToken("31bf3856ad364e35"));

        var renderOptionsRef = new TypeRefUser(fmodelModule, "System.Windows.Media", "RenderOptions", pcRef);
        var renderModeRef = new TypeRefUser(fmodelModule, "System.Windows.Interop", "RenderMode", pcRef);
        var setRenderMode = new MemberRefUser(
            fmodelModule,
            "set_ProcessRenderMode",
            MethodSig.CreateStatic(fmodelModule.CorLibTypes.Void, new ValueTypeSig(renderModeRef)),
            renderOptionsRef);

        // RenderMode.SoftwareOnly == 1
        instrs.Insert(0, Instruction.Create(OpCodes.Ldc_I4_1));
        instrs.Insert(1, Instruction.Create(OpCodes.Call, setRenderMode));

        onStartup.Body.SimplifyBranches();
        onStartup.Body.OptimizeBranches();
        return true;
    }
}
