using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace FModelCnPatcher;

public static partial class MetadataAndIlLocalizer
{
    private static readonly HashSet<string> GuardedMethodNames = new(StringComparer.Ordinal)
    {
        "op_Equality", "op_Inequality", "Equals", "StartsWith", "EndsWith",
        "Contains", "IndexOf", "LastIndexOf", "Compare", "CompareOrdinal",
        "TryGetValue", "ContainsKey", "Add", "Remove",
        "RaisePropertyChanged", "OnPropertyChanged", "SetProperty",
        "SetUniform", "GetUniformLocation", "GetManifestResourceStream",
        "AddValidationError", "ClearValidationErrors"
    };

    private static readonly HashSet<string> ExcludedMethodNames = new(StringComparer.Ordinal)
    {
        "PrintMembers", "ToString"
    };

    private static readonly HashSet<string> ExcludedTypeNames = new(StringComparer.Ordinal)
    {
        "FModel.Constants",
        "FModel.Settings.UserSettings",
        "FModel.Framework.FLogger"
    };

    [GeneratedRegex(@"^([\uE000-\uF8FF]+\s+)(.+)$", RegexOptions.Singleline)]
    private static partial Regex LeadingIconRegex();

    public static int LocalizeEnumDescriptions(ModuleDefMD module, LangPack lang)
    {
        int count = 0;
        foreach (var type in module.GetTypes())
        {
            if (!type.IsEnum) continue;
            // Skip internal game asset hash enums (e.g. MultiVersus GUID hashes)
            if (type.FullName.Contains(".Creator.Bases.MV.", StringComparison.Ordinal))
                continue;

            foreach (var field in type.Fields)
            {
                if (!field.IsLiteral || !field.HasCustomAttributes) continue;
                foreach (var attr in field.CustomAttributes)
                {
                    if (attr.TypeFullName != "System.ComponentModel.DescriptionAttribute") continue;
                    if (attr.ConstructorArguments.Count != 1) continue;
                    var raw = attr.ConstructorArguments[0].Value?.ToString();
                    if (string.IsNullOrEmpty(raw)) continue;

                    if (lang.EnumDescriptions.TryGetValue(raw, out var zh))
                    {
                        attr.ConstructorArguments[0] = new CAArgument(module.CorLibTypes.String, new UTF8String(zh));
                        count++;
                    }
                }
            }
        }
        return count;
    }

    public static int LocalizeStatusLabels(ModuleDefMD fmodelModule, LangPack lang)
    {
        if (lang.StatusLabels.Count == 0) return 0;

        var fStatus = fmodelModule.Find("FModel.Framework.FStatus", false);
        var eStatusKind = fmodelModule.Find("FModel.EStatusKind", false);
        if (fStatus == null || eStatusKind == null) return 0;

        var updateMethod = fStatus.Methods.FirstOrDefault(m => m.Name == "UpdateStatusLabel" && m.HasBody);
        if (updateMethod == null) return 0;

        // Avoid double injection
        if (fStatus.Methods.Any(m => m.Name == "TranslateStatusKindCn"))
            return 0;

        // Build: private static string TranslateStatusKindCn(EStatusKind kind)
        var corString = fmodelModule.CorLibTypes.String;
        var helper = new MethodDefUser(
            "TranslateStatusKindCn",
            MethodSig.CreateStatic(corString, eStatusKind.ToTypeSig()),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig);

        var body = new CilBody();
        helper.Body = body;

        var literals = eStatusKind.Fields
            .Where(f => f.IsLiteral && f.HasConstant)
            .OrderBy(f => Convert.ToInt32(f.Constant.Value))
            .ToList();

        var retEnd = Instruction.Create(OpCodes.Ret);
        foreach (var lit in literals)
        {
            int val = Convert.ToInt32(lit.Constant.Value);
            string label = lang.StatusLabels.TryGetValue(lit.Name, out var zh) ? zh : lit.Name.String;
            var nextCheck = Instruction.Create(OpCodes.Nop);

            body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            body.Instructions.Add(Instruction.CreateLdcI4(val));
            body.Instructions.Add(Instruction.Create(OpCodes.Bne_Un, nextCheck));
            body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, label));
            body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            body.Instructions.Add(nextCheck);
        }

        // Fallback: kind.ToString()
        var objToString = fmodelModule.Import(typeof(object).GetMethod("ToString", Type.EmptyTypes)!);
        body.Instructions.Add(Instruction.Create(OpCodes.Ldarga_S, helper.Parameters[0]));
        body.Instructions.Add(Instruction.Create(OpCodes.Constrained, eStatusKind));
        body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, objToString));
        body.Instructions.Add(retEnd);
        fStatus.Methods.Add(helper);

        // Patch UpdateStatusLabel: replace [ldloca.s; constrained. EStatusKind; callvirt Object::ToString()] with [ldloc.0; call TranslateStatusKindCn]
        var instrs = updateMethod.Body.Instructions;
        int patched = 0;
        for (int i = 0; i <= instrs.Count - 3; i++)
        {
            if ((instrs[i].OpCode == OpCodes.Ldloca || instrs[i].OpCode == OpCodes.Ldloca_S)
                && instrs[i + 1].OpCode == OpCodes.Constrained
                && instrs[i + 1].Operand is ITypeDefOrRef td && td.FullName == eStatusKind.FullName
                && instrs[i + 2].OpCode == OpCodes.Callvirt
                && instrs[i + 2].Operand is IMethod mref && mref.Name == "ToString")
            {
                var localVar = instrs[i].Operand as Local;
                if (localVar != null)
                {
                    instrs[i].OpCode = OpCodes.Ldloc;
                    instrs[i].Operand = localVar;
                    instrs[i + 1].OpCode = OpCodes.Nop;
                    instrs[i + 1].Operand = null;
                    instrs[i + 2].OpCode = OpCodes.Call;
                    instrs[i + 2].Operand = helper;
                    patched++;
                }
            }
        }
        if (patched > 0)
        {
            updateMethod.Body.SimplifyBranches();
            updateMethod.Body.OptimizeBranches();
        }
        return patched;
    }

    public static int LocalizeAssemblyStrings(ModuleDefMD module, LangPack lang, bool restrictToFModelUiNamespaces, bool includeSnooperStrings = false)
    {
        int count = 0;
        foreach (var type in module.GetTypes())
        {
            if (ShouldSkipType(type, restrictToFModelUiNamespaces))
                continue;

            foreach (var method in type.Methods)
            {
                if (!method.HasBody || ExcludedMethodNames.Contains(method.Name.String)) continue;
                var instrs = method.Body.Instructions;
                for (int i = 0; i < instrs.Count; i++)
                {
                    var ins = instrs[i];
                    if (ins.OpCode != OpCodes.Ldstr || ins.Operand is not string raw || string.IsNullOrEmpty(raw))
                        continue;

                    if (IsComparisonGuardTriggered(instrs, i))
                        continue;

                    if (TryTranslateCodeString(raw, lang.CodeStrings, out var translated)
                        || (includeSnooperStrings && TryTranslateCodeString(raw, lang.SnooperStrings, out translated)))
                    {
                        ins.Operand = translated;
                        count++;
                    }
                }
            }
        }
        return count;
    }

    private static bool ShouldSkipType(TypeDef type, bool restrictToFModelUiNamespaces)
    {
        var full = type.FullName;
        if (ExcludedTypeNames.Contains(full))
            return true;

        var rootFull = type.DeclaringType?.FullName ?? full;
        if (ExcludedTypeNames.Contains(rootFull))
            return true;

        if (full.Contains("ApiEndpoints.Models.", StringComparison.Ordinal) && !full.EndsWith("GitHubCommit", StringComparison.Ordinal))
            return true;
        if (full.Contains(".Creator.Bases.", StringComparison.Ordinal))
            return true;
        if (full.Contains(".Shaders", StringComparison.Ordinal) || full.Contains("EmbeddedShader", StringComparison.Ordinal))
            return true;

        if (restrictToFModelUiNamespaces)
        {
            var ns = (type.DeclaringType ?? type).Namespace.String;
            bool allowed = ns == "FModel"
                || ns.StartsWith("FModel.Views", StringComparison.Ordinal)
                || ns.StartsWith("FModel.ViewModels", StringComparison.Ordinal)
                || ns.StartsWith("FModel.Framework", StringComparison.Ordinal)
                || ns.StartsWith("FModel.Services", StringComparison.Ordinal);
            if (!allowed)
                return true;
        }
        return false;
    }

    private static bool IsComparisonGuardTriggered(IList<Instruction> instrs, int idx)
    {
        // Look ahead up to 2 instructions (handles e.g. ldstr; ldc.i4.5 [StringComparison]; callvirt String::Equals)
        int limit = Math.Min(instrs.Count - 1, idx + 3);
        for (int j = idx + 1; j <= limit; j++)
        {
            var op = instrs[j].OpCode;
            if (op == OpCodes.Call || op == OpCodes.Callvirt)
            {
                if (instrs[j].Operand is IMethod m)
                {
                    var mName = m.Name.String;
                    var decl = m.DeclaringType?.FullName ?? string.Empty;
                    if (GuardedMethodNames.Contains(mName))
                    {
                        return true;
                    }
                    if (decl.Contains("Log", StringComparison.Ordinal) || mName == "ForContext" || mName == "GetMethod")
                        return true;
                }
                break;
            }
            if (op == OpCodes.Newobj)
            {
                if (instrs[j].Operand is IMethod ctor && ctor.DeclaringType?.Name == "PropertyChangedEventArgs")
                    return true;
                break;
            }
            if (op.FlowControl != FlowControl.Next && op.FlowControl != FlowControl.Meta)
                break;
            // Allow loading another string / int / StringComparison before comparison call
            if (op != OpCodes.Ldstr && op != OpCodes.Ldc_I4 && op != OpCodes.Ldc_I4_S &&
                op != OpCodes.Ldc_I4_0 && op != OpCodes.Ldc_I4_1 && op != OpCodes.Ldc_I4_2 &&
                op != OpCodes.Ldc_I4_3 && op != OpCodes.Ldc_I4_4 && op != OpCodes.Ldc_I4_5 &&
                op != OpCodes.Ldloc && op != OpCodes.Ldloc_0 && op != OpCodes.Ldloc_1 && op != OpCodes.Ldloc_2 && op != OpCodes.Ldloc_3 && op != OpCodes.Ldloc_S)
            {
                break;
            }
        }
        return false;
    }

    private static bool TryTranslateCodeString(string raw, Dictionary<string, string> dict, out string result)
    {
        result = raw;

        // Exact match first
        if (dict.TryGetValue(raw, out var exact))
        {
            result = exact;
            return true;
        }

        // Preserve ImGui ##ID suffix (e.g. "Parameters##ParametersTab" -> "参数##ParametersTab")
        int hashIdx = raw.IndexOf("##", StringComparison.Ordinal);
        if (hashIdx > 0 && !raw.StartsWith("##", StringComparison.Ordinal))
        {
            var visible = raw[..hashIdx];
            var suffix = raw[hashIdx..];
            if (TryTranslateVisibleSegment(visible, dict, out var visTranslated))
            {
                result = visTranslated + suffix;
                return true;
            }
        }

        return TryTranslateVisibleSegment(raw, dict, out result);
    }

    private static bool TryTranslateVisibleSegment(string raw, Dictionary<string, string> dict, out string result)
    {
        result = raw;
        if (dict.TryGetValue(raw, out var exact))
        {
            result = exact;
            return true;
        }

        // Preserve leading FontAwesome icon prefix (e.g. "\uf56e  Export")
        var m = LeadingIconRegex().Match(raw);
        if (m.Success)
        {
            var iconPrefix = m.Groups[1].Value;
            var textPart = m.Groups[2].Value;
            if (dict.TryGetValue(textPart, out var textTr))
            {
                result = iconPrefix + textTr;
                return true;
            }
        }

        return false;
    }
}
