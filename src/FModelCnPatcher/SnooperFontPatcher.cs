using System.Runtime.InteropServices;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace FModelCnPatcher;

public static class SnooperFontPatcher
{
    public static bool PatchImGuiFonts(ModuleDefMD editorModule)
    {
        var imguiManager = editorModule.Find("Editor.Managers.ImGuiManager", false)
                           ?? editorModule.Find("FModel.Framework.ImGuiController", false);
        if (imguiManager == null)
            return false;

        if (imguiManager.Methods.Any(m => m.Name == "AddCnFontWithGlyphs"))
            return true;

        var imguiAsmRef = editorModule.GetAssemblyRefs().FirstOrDefault(r =>
            r.Name.String.Contains("ImGui", StringComparison.OrdinalIgnoreCase));
        if (imguiAsmRef == null)
            return false;

        var atlasTypeRef = new TypeRefUser(editorModule, "ImGuiNET", "ImFontAtlasPtr", imguiAsmRef);
        var fontPtrTypeRef = new TypeRefUser(editorModule, "ImGuiNET", "ImFontPtr", imguiAsmRef);
        var fontCfgTypeRef = new TypeRefUser(editorModule, "ImGuiNET", "ImFontConfigPtr", imguiAsmRef);
        var atlasSig = new ValueTypeSig(atlasTypeRef);
        var fontPtrSig = new ValueTypeSig(fontPtrTypeRef);
        var fontCfgSig = new ValueTypeSig(fontCfgTypeRef);

        var addFont4Sig = MethodSig.CreateInstance(
            fontPtrSig,
            editorModule.CorLibTypes.String,
            editorModule.CorLibTypes.Single,
            fontCfgSig,
            editorModule.CorLibTypes.IntPtr);
        var addFont4Ref = new MemberRefUser(editorModule, "AddFontFromFileTTF", addFont4Sig, atlasTypeRef);

        var cfgCtorSig = MethodSig.CreateInstance(editorModule.CorLibTypes.Void, editorModule.CorLibTypes.IntPtr);
        var cfgCtorRef = new MemberRefUser(editorModule, ".ctor", cfgCtorSig, fontCfgTypeRef);

        var sysRuntimeRef = editorModule.CorLibTypes.AssemblyRef;
        var fileTypeRef = new TypeRefUser(editorModule, "System.IO", "File", sysRuntimeRef);
        var fileExistsRef = new MemberRefUser(editorModule, "Exists",
            MethodSig.CreateStatic(editorModule.CorLibTypes.Boolean, editorModule.CorLibTypes.String), fileTypeRef);

        var strEndsWithRef = new MemberRefUser(editorModule, "EndsWith",
            MethodSig.CreateInstance(editorModule.CorLibTypes.Boolean, editorModule.CorLibTypes.String,
                new ValueTypeSig(new TypeRefUser(editorModule, "System", "StringComparison", sysRuntimeRef))),
            editorModule.CorLibTypes.String.TypeDefOrRef);

        var gcHandleTypeRef = new TypeRefUser(editorModule, "System.Runtime.InteropServices", "GCHandle", sysRuntimeRef);
        var gcHandleSig = new ValueTypeSig(gcHandleTypeRef);
        var gcHandleTypeEnumRef = new TypeRefUser(editorModule, "System.Runtime.InteropServices", "GCHandleType", sysRuntimeRef);
        var gcAllocRef = new MemberRefUser(editorModule, "Alloc",
            MethodSig.CreateStatic(gcHandleSig, editorModule.CorLibTypes.Object, new ValueTypeSig(gcHandleTypeEnumRef)),
            gcHandleTypeRef);
        var gcAddrRef = new MemberRefUser(editorModule, "AddrOfPinnedObject",
            MethodSig.CreateInstance(editorModule.CorLibTypes.IntPtr), gcHandleTypeRef);

        // Add static field: private static IntPtr _cnGlyphRangesPtr;
        var glyphField = new FieldDefUser("_cnGlyphRangesPtr",
            new FieldSig(editorModule.CorLibTypes.IntPtr),
            FieldAttributes.Private | FieldAttributes.Static);
        imguiManager.Fields.Add(glyphField);

        // Build: private static IntPtr GetCnGlyphRangesPtr()
        var getGlyphsMethod = new MethodDefUser(
            "GetCnGlyphRangesPtr",
            MethodSig.CreateStatic(editorModule.CorLibTypes.IntPtr),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig);

        var gBody = new CilBody();
        var arrLocal = new Local(new SZArraySig(editorModule.CorLibTypes.UInt16));
        var hLocal = new Local(gcHandleSig);
        gBody.Variables.Add(arrLocal);
        gBody.Variables.Add(hLocal);
        gBody.InitLocals = true;
        getGlyphsMethod.Body = gBody;

        var alreadyInitLabel = Instruction.Create(OpCodes.Ldsfld, glyphField);
        var intPtrZero = new MemberRefUser(editorModule, "Zero", new FieldSig(editorModule.CorLibTypes.IntPtr),
            new TypeRefUser(editorModule, "System", "IntPtr", sysRuntimeRef));
        var intPtrInequality = new MemberRefUser(editorModule, "op_Inequality",
            MethodSig.CreateStatic(editorModule.CorLibTypes.Boolean, editorModule.CorLibTypes.IntPtr, editorModule.CorLibTypes.IntPtr),
            new TypeRefUser(editorModule, "System", "IntPtr", sysRuntimeRef));

        gBody.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, glyphField));
        gBody.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, intPtrZero));
        gBody.Instructions.Add(Instruction.Create(OpCodes.Call, intPtrInequality));
        gBody.Instructions.Add(Instruction.Create(OpCodes.Brtrue, alreadyInitLabel));

        ushort[] ranges =
        [
            0x0020, 0x00FF, // Basic Latin + Latin Supplement
            0x2000, 0x206F, // General Punctuation
            0x3000, 0x30FF, // CJK Symbols + Hiragana/Katakana
            0x31F0, 0x31FF, // Katakana Phonetic Extensions
            0xFF00, 0xFFEF, // Fullwidth Forms
            0x4E00, 0x9FAF, // CJK Unified Ideographs
            0x0000
        ];
        gBody.Instructions.Add(Instruction.CreateLdcI4(ranges.Length));
        gBody.Instructions.Add(Instruction.Create(OpCodes.Newarr, editorModule.CorLibTypes.UInt16.ToTypeDefOrRef()));
        gBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, arrLocal));
        for (int idx = 0; idx < ranges.Length; idx++)
        {
            gBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, arrLocal));
            gBody.Instructions.Add(Instruction.CreateLdcI4(idx));
            gBody.Instructions.Add(Instruction.CreateLdcI4(ranges[idx]));
            gBody.Instructions.Add(Instruction.Create(OpCodes.Stelem_I2));
        }

        // GCHandle.Alloc(arr, GCHandleType.Pinned) => GCHandleType.Pinned == 3
        gBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, arrLocal));
        gBody.Instructions.Add(Instruction.CreateLdcI4(3));
        gBody.Instructions.Add(Instruction.Create(OpCodes.Call, gcAllocRef));
        gBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, hLocal));
        gBody.Instructions.Add(Instruction.Create(OpCodes.Ldloca, hLocal));
        gBody.Instructions.Add(Instruction.Create(OpCodes.Call, gcAddrRef));
        gBody.Instructions.Add(Instruction.Create(OpCodes.Stsfld, glyphField));
        gBody.Instructions.Add(alreadyInitLabel);
        gBody.Instructions.Add(Instruction.Create(OpCodes.Ret));
        imguiManager.Methods.Add(getGlyphsMethod);

        // Build: private static ImFontPtr AddCnFontWithGlyphs(ref ImFontAtlasPtr atlas, string origPath, float size)
        var helper = new MethodDefUser(
            "AddCnFontWithGlyphs",
            MethodSig.CreateStatic(fontPtrSig,
                new ByRefSig(atlasSig),
                editorModule.CorLibTypes.String,
                editorModule.CorLibTypes.Single),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig);

        var hBody = new CilBody();
        var chosenLocal = new Local(editorModule.CorLibTypes.String);
        hBody.Variables.Add(chosenLocal);
        hBody.InitLocals = true;
        helper.Body = hBody;

        var callAddLabel = Instruction.Create(OpCodes.Ldarg_0);
        var checkRegularLabel = Instruction.Create(OpCodes.Ldstr, @"C:\Windows\Fonts\msyh.ttc");
        var checkSimHeiLabel = Instruction.Create(OpCodes.Ldstr, @"C:\Windows\Fonts\simhei.ttf");
        var fallbackOrigLabel = Instruction.Create(OpCodes.Ldarg_1);

        // If origPath ends with "segoeuib.ttf" or "seguisb.ttf" (OrdinalIgnoreCase = 5), check msyhbd.ttc first
        hBody.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "segoeuib.ttf"));
        hBody.Instructions.Add(Instruction.CreateLdcI4(5));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, strEndsWithRef));
        var tryBoldLabel = Instruction.Create(OpCodes.Ldstr, @"C:\Windows\Fonts\msyhbd.ttc");
        hBody.Instructions.Add(Instruction.Create(OpCodes.Brtrue, tryBoldLabel));

        hBody.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "seguisb.ttf"));
        hBody.Instructions.Add(Instruction.CreateLdcI4(5));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, strEndsWithRef));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Brfalse, checkRegularLabel));

        hBody.Instructions.Add(tryBoldLabel);
        hBody.Instructions.Add(Instruction.Create(OpCodes.Call, fileExistsRef));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Brfalse, checkRegularLabel));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, @"C:\Windows\Fonts\msyhbd.ttc"));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, chosenLocal));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Br, callAddLabel));

        hBody.Instructions.Add(checkRegularLabel);
        hBody.Instructions.Add(Instruction.Create(OpCodes.Call, fileExistsRef));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Brfalse, checkSimHeiLabel));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, @"C:\Windows\Fonts\msyh.ttc"));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, chosenLocal));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Br, callAddLabel));

        hBody.Instructions.Add(checkSimHeiLabel);
        hBody.Instructions.Add(Instruction.Create(OpCodes.Call, fileExistsRef));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Brfalse, fallbackOrigLabel));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, @"C:\Windows\Fonts\simhei.ttf"));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, chosenLocal));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Br, callAddLabel));

        hBody.Instructions.Add(fallbackOrigLabel);
        hBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, chosenLocal));

        hBody.Instructions.Add(callAddLabel);
        hBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, chosenLocal));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Ldarg_2));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, intPtrZero));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Newobj, cfgCtorRef));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Call, getGlyphsMethod));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Call, addFont4Ref));
        hBody.Instructions.Add(Instruction.Create(OpCodes.Ret));
        imguiManager.Methods.Add(helper);

        // Replace calls to ImFontAtlasPtr::AddFontFromFileTTF(string, float) inside ImGuiManager methods
        int replacedCalls = 0;
        foreach (var m in imguiManager.Methods.Where(m => m.HasBody && m != helper && m != getGlyphsMethod))
        {
            foreach (var ins in m.Body.Instructions)
            {
                if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt)
                    && ins.Operand is IMethod target
                    && target.Name == "AddFontFromFileTTF"
                    && target.MethodSig is MethodSig ms
                    && ms.Params.Count == 2
                    && ms.Params[0].FullName == "System.String")
                {
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = helper;
                    replacedCalls++;
                }
            }
        }

        return replacedCalls > 0;
    }
}
