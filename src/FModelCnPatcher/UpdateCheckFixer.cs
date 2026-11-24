using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace FModelCnPatcher;

public static class UpdateCheckFixer
{
    public static int Apply(ModuleDefMD fmodelModule, out List<string> actions)
    {
        actions = [];
        int changes = 0;

        changes += SilenceStartupUpdateDialog(fmodelModule, actions);
        changes += DisableCoAuthorFlood(fmodelModule, actions);
        changes += InjectQaApiFallback(fmodelModule, actions);

        return changes;
    }

    private static int SilenceStartupUpdateDialog(ModuleDefMD mod, List<string> actions)
    {
        var apiEndpoint = mod.Find("FModel.ViewModels.ApiEndpoints.FModelApiEndpoint", false);
        if (apiEndpoint == null) return 0;

        var checkEvent = apiEndpoint.Methods.FirstOrDefault(m => m.Name == "CheckForUpdateEvent" && m.HasBody);
        if (checkEvent == null) return 0;

        int edits = 0;
        var instrs = checkEvent.Body.Instructions;

        // 1. Suppress modal UpdateView popup on startup while keeping IsUpdateAvailable = true
        for (int i = 0; i < instrs.Count; i++)
        {
            if ((instrs[i].OpCode == OpCodes.Call || instrs[i].OpCode == OpCodes.Callvirt)
                && instrs[i].Operand is IMethod m
                && m.Name == "OpenWindow")
            {
                int argCount = m.MethodSig?.Params.Count ?? 2;
                instrs[i].OpCode = OpCodes.Pop;
                instrs[i].Operand = null;
                for (int p = 1; p < argCount; p++)
                    instrs.Insert(i + p, Instruction.Create(OpCodes.Pop));
                edits++;
                actions.Add("已静音启动时的更新弹窗（改为仅右下角状态栏提示）");
                break;
            }
        }

        // 2. Suppress MessageBox.Show("Update Check Failed") when offline or update server unreachable
        for (int i = 0; i < instrs.Count; i++)
        {
            if ((instrs[i].OpCode == OpCodes.Call || instrs[i].OpCode == OpCodes.Callvirt)
                && instrs[i].Operand is IMethod m
                && m.DeclaringType?.Name == "MessageBox"
                && m.Name == "Show")
            {
                int argCount = m.MethodSig?.Params.Count ?? 0;
                bool returnsVal = m.MethodSig?.RetType.FullName != "System.Void";

                if (argCount > 0)
                {
                    instrs[i].OpCode = OpCodes.Pop;
                    instrs[i].Operand = null;
                    int insertAt = i + 1;
                    for (int p = 1; p < argCount; p++)
                        instrs.Insert(insertAt++, Instruction.Create(OpCodes.Pop));
                    if (returnsVal)
                        instrs.Insert(insertAt, Instruction.CreateLdcI4(0));
                }
                else
                {
                    instrs[i].OpCode = returnsVal ? OpCodes.Ldc_I4_0 : OpCodes.Nop;
                    instrs[i].Operand = null;
                }
                edits++;
                actions.Add("已抑制启动断网/限流时的更新失败报错弹窗");
                break;
            }
        }

        if (edits > 0)
        {
            checkEvent.Body.SimplifyBranches();
            checkEvent.Body.OptimizeBranches();
        }
        return edits;
    }

    private static int DisableCoAuthorFlood(ModuleDefMD mod, List<string> actions)
    {
        var uvm = mod.Find("FModel.ViewModels.UpdateViewModel", false);
        var loadCo = uvm?.Methods.FirstOrDefault(m => m.Name == "LoadCoAuthors" && m.HasBody);
        if (loadCo == null) return 0;

        var taskTypeRef = new TypeRefUser(mod, "System.Threading.Tasks", "Task", mod.CorLibTypes.AssemblyRef);
        var completedGetter = new MemberRefUser(mod, "get_CompletedTask",
            MethodSig.CreateStatic(taskTypeRef.ToTypeSig()), taskTypeRef);

        loadCo.Body.ExceptionHandlers.Clear();
        loadCo.Body.Variables.Clear();
        loadCo.Body.Instructions.Clear();
        loadCo.Body.Instructions.Add(Instruction.Create(OpCodes.Call, completedGetter));
        loadCo.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        actions.Add("已禁用 LoadCoAuthors 对 GitHub API 的高频请求（防止触发 403 限流）");
        return 1;
    }

    private static int InjectQaApiFallback(ModuleDefMD mod, List<string> actions)
    {
        var uvm = mod.Find("FModel.ViewModels.UpdateViewModel", false);
        if (uvm == null) return 0;

        if (uvm.Methods.Any(m => m.Name == "WrapLoadAsyncCn"))
            return 0;

        var loadAsync = uvm.Methods.FirstOrDefault(m => m.Name == "LoadAsync" && m.HasBody);
        if (loadAsync == null) return 0;

        var infoType = mod.Find("FModel.ViewModels.ApiEndpoints.Models.Info", false);
        var ghCommitType = mod.Find("FModel.ViewModels.ApiEndpoints.Models.GitHubCommit", false);
        var ghAssetType = mod.Find("FModel.ViewModels.ApiEndpoints.Models.GitHubAsset", false);
        var commitType = mod.Find("FModel.ViewModels.ApiEndpoints.Models.Commit", false);
        var authorType = mod.Find("FModel.ViewModels.ApiEndpoints.Models.Author", false);
        if (infoType == null || ghCommitType == null || ghAssetType == null || commitType == null || authorType == null)
            return 0;

        // Discover actual info URL from FModelApiEndpoint.CheckForUpdates (defaulting to Qa)
        string infoUrl = "https://api.fmodel.app/v1/infos/Qa";
        var apiEndpoint = mod.Find("FModel.ViewModels.ApiEndpoints.FModelApiEndpoint", false);
        var checkForUpdates = apiEndpoint?.Methods.FirstOrDefault(m => m.Name == "CheckForUpdates" && m.HasBody);
        if (checkForUpdates != null)
        {
            var foundUrl = checkForUpdates.Body.Instructions
                .Where(ins => ins.OpCode == OpCodes.Ldstr && ins.Operand is string s && s.Contains("api.fmodel.app/v1/infos/", StringComparison.OrdinalIgnoreCase))
                .Select(ins => (string)ins.Operand)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(foundUrl))
                infoUrl = foundUrl;
        }

        // Resolve AssemblyRefs already present in FModel.dll
        var sysRuntime = mod.CorLibTypes.AssemblyRef;
        var netHttpRef = mod.GetAssemblyRef("System.Net.Http") ?? new AssemblyRefUser("System.Net.Http", sysRuntime.Version, sysRuntime.PublicKeyOrToken);
        var jsonRef = mod.GetAssemblyRef("Newtonsoft.Json") ?? new AssemblyRefUser("Newtonsoft.Json", new Version(13, 0, 0, 0), new PublicKeyToken("30ad4fe6b2a6aeed"));
        var pfRef = mod.GetAssemblyRef("PresentationFramework") ?? new AssemblyRefUser("PresentationFramework", sysRuntime.Version, new PublicKeyToken("31bf3856ad364e35"));
        var wbRef = mod.GetAssemblyRef("WindowsBase") ?? new AssemblyRefUser("WindowsBase", sysRuntime.Version, new PublicKeyToken("31bf3856ad364e35"));

        // Core TypeRefs
        var taskTypeRef = new TypeRefUser(mod, "System.Threading.Tasks", "Task", sysRuntime);
        var taskOfStrRef = new TypeRefUser(mod, "System.Threading.Tasks", "Task`1", sysRuntime);
        var taskAwaiterOfStrRef = new TypeRefUser(mod, "System.Runtime.CompilerServices", "TaskAwaiter`1", sysRuntime);
        var taskSchedulerRef = new TypeRefUser(mod, "System.Threading.Tasks", "TaskScheduler", sysRuntime);
        var actionRef = new TypeRefUser(mod, "System", "Action", sysRuntime);
        var action2Ref = new TypeRefUser(mod, "System", "Action`2", sysRuntime);
        var timeSpanRef = new TypeRefUser(mod, "System", "TimeSpan", sysRuntime);
        var dateTimeRef = new TypeRefUser(mod, "System", "DateTime", sysRuntime);
        var strCmpRef = new TypeRefUser(mod, "System", "StringComparison", sysRuntime);
        var iListRef = new TypeRefUser(mod, "System.Collections", "IList", sysRuntime);
        var iCollRef = new TypeRefUser(mod, "System.Collections", "ICollection", sysRuntime);
        var httpClientRef = new TypeRefUser(mod, "System.Net.Http", "HttpClient", netHttpRef);
        var jsonConvertRef = new TypeRefUser(mod, "Newtonsoft.Json", "JsonConvert", jsonRef);
        var appTypeRef = new TypeRefUser(mod, "System.Windows", "Application", pfRef);
        var dispatcherTypeRef = new TypeRefUser(mod, "System.Windows.Threading", "Dispatcher", wbRef);

        var timeSpanSig = new ValueTypeSig(timeSpanRef);
        var dateTimeSig = new ValueTypeSig(dateTimeRef);
        var strCmpSig = new ValueTypeSig(strCmpRef);
        var taskOfStrSig = new GenericInstSig(new ClassSig(taskOfStrRef), mod.CorLibTypes.String);
        var awaiterOfStrSig = new GenericInstSig(new ValueTypeSig(taskAwaiterOfStrRef), mod.CorLibTypes.String);
        var actionTaskObjSig = new GenericInstSig(new ClassSig(action2Ref), taskTypeRef.ToTypeSig(), mod.CorLibTypes.Object);

        // Build nested class: UpdateViewModel.CnFallbackClosure
        var closureType = new TypeDefUser(
            "CnFallbackClosure",
            mod.CorLibTypes.Object.TypeDefOrRef)
        {
            Attributes = TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit
        };
        uvm.NestedTypes.Add(closureType);

        var fVm = new FieldDefUser("Vm", new FieldSig(uvm.ToTypeSig()), FieldAttributes.Public);
        var fSha = new FieldDefUser("Sha", new FieldSig(mod.CorLibTypes.String), FieldAttributes.Public);
        var fAsset = new FieldDefUser("Asset", new FieldSig(ghAssetType.ToTypeSig()), FieldAttributes.Public);
        closureType.Fields.Add(fVm);
        closureType.Fields.Add(fSha);
        closureType.Fields.Add(fAsset);

        // Closure .ctor()
        var objCtor = new MemberRefUser(mod, ".ctor", MethodSig.CreateInstance(mod.CorLibTypes.Void), mod.CorLibTypes.Object.TypeDefOrRef);
        var closureCtor = new MethodDefUser(".ctor", MethodSig.CreateInstance(mod.CorLibTypes.Void),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        closureCtor.Body = new CilBody();
        closureCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        closureCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, objCtor));
        closureCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        closureType.Methods.Add(closureCtor);

        // MemberRefs needed in ApplyOnUi()
        var getCommits = uvm.Methods.First(m => m.Name == "get_Commits");
        var getSha = ghCommitType.Methods.First(m => m.Name == "get_Sha");
        var setSha = ghCommitType.Methods.First(m => m.Name == "set_Sha");
        var getAsset = ghCommitType.Methods.First(m => m.Name == "get_Asset");
        var setAsset = ghCommitType.Methods.First(m => m.Name == "set_Asset");
        var setAuthorOnGhc = ghCommitType.Methods.First(m => m.Name == "set_Author");
        var setCommitOnGhc = ghCommitType.Methods.First(m => m.Name == "set_Commit");
        var ghcCtor = ghCommitType.Methods.First(m => m.IsConstructor && m.MethodSig.Params.Count == 0);
        var commitCtor = commitType.Methods.First(m => m.IsConstructor && m.MethodSig.Params.Count == 0);
        var setCommitAuthor = commitType.Methods.First(m => m.Name == "set_Author");
        var setCommitMsg = commitType.Methods.First(m => m.Name == "set_Message");
        var authorCtor = authorType.Methods.First(m => m.IsConstructor && m.MethodSig.Params.Count == 0);
        var setAuthorName = authorType.Methods.First(m => m.Name == "set_Name");
        var setAuthorLogin = authorType.Methods.First(m => m.Name == "set_Login");
        var setAuthorDate = authorType.Methods.First(m => m.Name == "set_Date");

        var iCollGetCount = new MemberRefUser(mod, "get_Count", MethodSig.CreateInstance(mod.CorLibTypes.Int32), iCollRef);
        var iListGetItem = new MemberRefUser(mod, "get_Item", MethodSig.CreateInstance(mod.CorLibTypes.Object, mod.CorLibTypes.Int32), iListRef);
        var iListInsert = new MemberRefUser(mod, "Insert", MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.Int32, mod.CorLibTypes.Object), iListRef);
        var strEquals3 = new MemberRefUser(mod, "Equals",
            MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.String, mod.CorLibTypes.String, strCmpSig),
            mod.CorLibTypes.String.TypeDefOrRef);
        var strSub2 = new MemberRefUser(mod, "Substring",
            MethodSig.CreateInstance(mod.CorLibTypes.String, mod.CorLibTypes.Int32, mod.CorLibTypes.Int32),
            mod.CorLibTypes.String.TypeDefOrRef);
        var strConcat3 = new MemberRefUser(mod, "Concat",
            MethodSig.CreateStatic(mod.CorLibTypes.String, mod.CorLibTypes.String, mod.CorLibTypes.String, mod.CorLibTypes.String),
            mod.CorLibTypes.String.TypeDefOrRef);
        var dtGetNow = new MemberRefUser(mod, "get_Now", MethodSig.CreateStatic(dateTimeSig), dateTimeRef);

        // Closure.ApplyOnUi()
        var applyOnUi = new MethodDefUser("ApplyOnUi", MethodSig.CreateInstance(mod.CorLibTypes.Void),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Public | MethodAttributes.HideBySig);
        var uiBody = new CilBody { InitLocals = true };
        var lCommits = new Local(getCommits.ReturnType);
        var lCount = new Local(mod.CorLibTypes.Int32);
        var lIdx = new Local(mod.CorLibTypes.Int32);
        var lCurGhc = new Local(ghCommitType.ToTypeSig());
        var lAuthor = new Local(authorType.ToTypeSig());
        var lCommit = new Local(commitType.ToTypeSig());
        var lGhc = new Local(ghCommitType.ToTypeSig());
        uiBody.Variables.Add(lCommits);
        uiBody.Variables.Add(lCount);
        uiBody.Variables.Add(lIdx);
        uiBody.Variables.Add(lCurGhc);
        uiBody.Variables.Add(lAuthor);
        uiBody.Variables.Add(lCommit);
        uiBody.Variables.Add(lGhc);
        applyOnUi.Body = uiBody;

        var loopCond = Instruction.Create(OpCodes.Ldloc, lIdx);
        var loopBodyStart = Instruction.Create(OpCodes.Ldloc, lCommits);
        var nextIter = Instruction.Create(OpCodes.Ldloc, lIdx);
        var afterLoop = Instruction.Create(OpCodes.Ldloc, lCount);
        var insertFallback = Instruction.Create(OpCodes.Newobj, authorCtor);
        var doneUi = Instruction.Create(OpCodes.Ret);

        // commits = Vm.Commits; count = commits.Count; i = 0;
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldfld, fVm));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getCommits));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lCommits));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lCommits));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, iCollGetCount));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lCount));
        uiBody.Instructions.Add(Instruction.CreateLdcI4(0));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lIdx));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Br, loopCond));

        // loop body: cur = commits[i] as GitHubCommit
        uiBody.Instructions.Add(loopBodyStart);
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lIdx));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, iListGetItem));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Isinst, ghCommitType));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lCurGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lCurGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Brfalse, nextIter));

        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lCurGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getSha));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldfld, fSha));
        uiBody.Instructions.Add(Instruction.CreateLdcI4(5)); // StringComparison.OrdinalIgnoreCase
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Call, strEquals3));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Brfalse, nextIter));

        // Matched commit: if (cur.Asset == null) cur.Asset = Asset; return;
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lCurGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getAsset));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Brtrue, doneUi));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lCurGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldfld, fAsset));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, setAsset));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ret));

        // i++
        uiBody.Instructions.Add(nextIter);
        uiBody.Instructions.Add(Instruction.CreateLdcI4(1));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Add));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lIdx));

        // loopCond: i < count
        uiBody.Instructions.Add(loopCond);
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lCount));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Blt, loopBodyStart));

        // afterLoop: if (count == 0 || ((GitHubCommit)commits[0])?.Asset == null) insert at 0
        uiBody.Instructions.Add(afterLoop);
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Brfalse, insertFallback));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lCommits));
        uiBody.Instructions.Add(Instruction.CreateLdcI4(0));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, iListGetItem));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Isinst, ghCommitType));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Dup));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Brfalse, doneUi)); // pop if null? Wait: if null, drop and insert
        // Keep simple: store in lCurGhc
        uiBody.Instructions.RemoveAt(uiBody.Instructions.Count - 1);
        uiBody.Instructions.RemoveAt(uiBody.Instructions.Count - 1);
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lCurGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lCurGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Brfalse, insertFallback));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lCurGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getAsset));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Brtrue, doneUi));

        // insertFallback:
        uiBody.Instructions.Add(insertFallback); // new Author()
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lAuthor));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lAuthor));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "4sval (api.fmodel.app)"));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, setAuthorName));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lAuthor));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "4sval"));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, setAuthorLogin));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lAuthor));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Call, dtGetNow));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, setAuthorDate));

        uiBody.Instructions.Add(Instruction.Create(OpCodes.Newobj, commitCtor));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lCommit));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lCommit));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lAuthor));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, setCommitAuthor));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lCommit));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "FModel ("));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldfld, fSha));
        uiBody.Instructions.Add(Instruction.CreateLdcI4(0));
        uiBody.Instructions.Add(Instruction.CreateLdcI4(7));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, strSub2));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, ") [官方直连通道]"));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Call, strConcat3));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, setCommitMsg));

        uiBody.Instructions.Add(Instruction.Create(OpCodes.Newobj, ghcCtor));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldfld, fSha));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, setSha));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lAuthor));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, setAuthorOnGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lCommit));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, setCommitOnGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldfld, fAsset));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, setAsset));

        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lCommits));
        uiBody.Instructions.Add(Instruction.CreateLdcI4(0));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lGhc));
        uiBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, iListInsert));
        uiBody.Instructions.Add(doneUi);
        closureType.Methods.Add(applyOnUi);

        // Build UpdateViewModel.OnLoadCompletedCn(Task t, object state)
        var onLoadCompleted = new MethodDefUser(
            "OnLoadCompletedCn",
            MethodSig.CreateStatic(mod.CorLibTypes.Void, taskTypeRef.ToTypeSig(), mod.CorLibTypes.Object),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig);

        var cBody = new CilBody { InitLocals = true };
        var lVm = new Local(uvm.ToTypeSig());
        var lHttp = new Local(httpClientRef.ToTypeSig());
        var lTaskStr = new Local(taskOfStrSig);
        var lAwaiter = new Local(awaiterOfStrSig);
        var lJson = new Local(mod.CorLibTypes.String);
        var lInfo = new Local(infoType.ToTypeSig());
        var lVer = new Local(mod.CorLibTypes.String);
        var lPlus = new Local(mod.CorLibTypes.Int32);
        var lSha = new Local(mod.CorLibTypes.String);
        var lDt = new Local(dateTimeSig);
        var lAssetJson = new Local(mod.CorLibTypes.String);
        var lAsset = new Local(ghAssetType.ToTypeSig());
        var lApp = new Local(appTypeRef.ToTypeSig());
        var lClosure = new Local(closureType.ToTypeSig());
        cBody.Variables.Add(lVm);
        cBody.Variables.Add(lHttp);
        cBody.Variables.Add(lTaskStr);
        cBody.Variables.Add(lAwaiter);
        cBody.Variables.Add(lJson);
        cBody.Variables.Add(lInfo);
        cBody.Variables.Add(lVer);
        cBody.Variables.Add(lPlus);
        cBody.Variables.Add(lSha);
        cBody.Variables.Add(lDt);
        cBody.Variables.Add(lAssetJson);
        cBody.Variables.Add(lAsset);
        cBody.Variables.Add(lApp);
        cBody.Variables.Add(lClosure);
        onLoadCompleted.Body = cBody;

        var iDisposableRef = new TypeRefUser(mod, "System", "IDisposable", sysRuntime);
        var httpCtor = new MemberRefUser(mod, ".ctor", MethodSig.CreateInstance(mod.CorLibTypes.Void), httpClientRef);
        var httpSetTimeout = new MemberRefUser(mod, "set_Timeout", MethodSig.CreateInstance(mod.CorLibTypes.Void, timeSpanSig), httpClientRef);
        var tsFromSeconds = new MemberRefUser(mod, "FromSeconds", MethodSig.CreateStatic(timeSpanSig, mod.CorLibTypes.Double), timeSpanRef);
        var httpGetStrAsync = new MemberRefUser(mod, "GetStringAsync", MethodSig.CreateInstance(taskOfStrSig, mod.CorLibTypes.String), httpClientRef);
        var disposableDispose = new MemberRefUser(mod, "Dispose", MethodSig.CreateInstance(mod.CorLibTypes.Void), iDisposableRef);
        // On open generic TypeRef Task`1<!0>, GetAwaiter returns TaskAwaiter`1<!0>, GetResult returns !0
        var openTaskAwaiterSig = new GenericInstSig(new ValueTypeSig(taskAwaiterOfStrRef), new GenericVar(0));
        var taskOfStrSpec = new TypeSpecUser(taskOfStrSig);
        var awaiterOfStrSpec = new TypeSpecUser(awaiterOfStrSig);
        var getAwaiterRef = new MemberRefUser(mod, "GetAwaiter", MethodSig.CreateInstance(openTaskAwaiterSig), taskOfStrSpec);
        var getResultRef = new MemberRefUser(mod, "GetResult", MethodSig.CreateInstance(new GenericVar(0)), awaiterOfStrSpec);

        var deserializeOpen = new MemberRefUser(mod, "DeserializeObject",
            new MethodSig(CallingConvention.Generic, 1, new GenericMVar(0), mod.CorLibTypes.String),
            jsonConvertRef);
        var deserializeInfo = new MethodSpecUser(deserializeOpen, new GenericInstMethodSig(infoType.ToTypeSig()));
        var deserializeAsset = new MethodSpecUser(deserializeOpen, new GenericInstMethodSig(ghAssetType.ToTypeSig()));

        var getInfoVersion = infoType.Methods.First(m => m.Name == "get_Version");
        var getInfoDownloadUrl = infoType.Methods.First(m => m.Name == "get_DownloadUrl");
        var setAssetIsLatest = ghAssetType.Methods.First(m => m.Name == "set_IsLatest");

        var strIsNullOrEmpty = new MemberRefUser(mod, "IsNullOrEmpty", MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.String), mod.CorLibTypes.String.TypeDefOrRef);
        var strIndexOfChar = new MemberRefUser(mod, "IndexOf", MethodSig.CreateInstance(mod.CorLibTypes.Int32, mod.CorLibTypes.Char), mod.CorLibTypes.String.TypeDefOrRef);
        var strSub1 = new MemberRefUser(mod, "Substring", MethodSig.CreateInstance(mod.CorLibTypes.String, mod.CorLibTypes.Int32), mod.CorLibTypes.String.TypeDefOrRef);
        var strGetLen = new MemberRefUser(mod, "get_Length", MethodSig.CreateInstance(mod.CorLibTypes.Int32), mod.CorLibTypes.String.TypeDefOrRef);
        var strConcatArr = new MemberRefUser(mod, "Concat", MethodSig.CreateStatic(mod.CorLibTypes.String, new SZArraySig(mod.CorLibTypes.String)), mod.CorLibTypes.String.TypeDefOrRef);
        var strReplace = new MemberRefUser(mod, "Replace", MethodSig.CreateInstance(mod.CorLibTypes.String, mod.CorLibTypes.String, mod.CorLibTypes.String), mod.CorLibTypes.String.TypeDefOrRef);
        var dtGetUtcNow = new MemberRefUser(mod, "get_UtcNow", MethodSig.CreateStatic(dateTimeSig), dateTimeRef);
        var dtToStringFmt = new MemberRefUser(mod, "ToString", MethodSig.CreateInstance(mod.CorLibTypes.String, mod.CorLibTypes.String), dateTimeRef);

        var appGetCurrent = new MemberRefUser(mod, "get_Current", MethodSig.CreateStatic(appTypeRef.ToTypeSig()), appTypeRef);
        var appGetDispatcher = new MemberRefUser(mod, "get_Dispatcher", MethodSig.CreateInstance(dispatcherTypeRef.ToTypeSig()), appTypeRef);
        var dispatcherInvoke = new MemberRefUser(mod, "Invoke", MethodSig.CreateInstance(mod.CorLibTypes.Void, actionRef.ToTypeSig()), dispatcherTypeRef);
        var actionCtor = new MemberRefUser(mod, ".ctor", MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.Object, mod.CorLibTypes.IntPtr), actionRef);

        var tryStart = Instruction.Create(OpCodes.Newobj, httpCtor);
        var leaveTry = Instruction.Create(OpCodes.Ret);
        var catchStart = Instruction.Create(OpCodes.Pop);

        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Isinst, uvm));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lVm));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lVm));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Brfalse, leaveTry));

        // try {
        cBody.Instructions.Add(tryStart);
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lHttp));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lHttp));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldc_R8, 10.0));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Call, tsFromSeconds));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, httpSetTimeout));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lHttp));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, infoUrl));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, httpGetStrAsync));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lTaskStr));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lTaskStr));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getAwaiterRef));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lAwaiter));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloca, lAwaiter));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Call, getResultRef));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lJson));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lHttp));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, disposableDispose));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lJson));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Call, deserializeInfo));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lInfo));

        var endLeave = Instruction.Create(OpCodes.Leave, leaveTry);
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lInfo));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Brfalse, endLeave));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lInfo));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getInfoVersion));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lVer));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lVer));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Call, strIsNullOrEmpty));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Brtrue, endLeave));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lInfo));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getInfoDownloadUrl));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Call, strIsNullOrEmpty));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Brtrue, endLeave));

        // int plus = ver.IndexOf('+');
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lVer));
        cBody.Instructions.Add(Instruction.CreateLdcI4('+'));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, strIndexOfChar));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lPlus));

        var useWholeVer = Instruction.Create(OpCodes.Ldloc, lVer);
        var checkShaLen = Instruction.Create(OpCodes.Ldloc, lSha);
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lPlus));
        cBody.Instructions.Add(Instruction.CreateLdcI4(0));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Blt, useWholeVer));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lPlus));
        cBody.Instructions.Add(Instruction.CreateLdcI4(1));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Add));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lVer));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, strGetLen));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Bge, useWholeVer));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lVer));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lPlus));
        cBody.Instructions.Add(Instruction.CreateLdcI4(1));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Add));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, strSub1));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lSha));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Br, checkShaLen));

        cBody.Instructions.Add(useWholeVer);
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lSha));

        cBody.Instructions.Add(checkShaLen);
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, strGetLen));
        cBody.Instructions.Add(Instruction.CreateLdcI4(7));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Blt, endLeave));

        // Build assetJson string[7]
        cBody.Instructions.Add(Instruction.Create(OpCodes.Call, dtGetUtcNow));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lDt));

        cBody.Instructions.Add(Instruction.CreateLdcI4(7));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Newarr, mod.CorLibTypes.String.TypeDefOrRef));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Dup));
        cBody.Instructions.Add(Instruction.CreateLdcI4(0));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "{\"name\":\""));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stelem_Ref));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Dup));
        cBody.Instructions.Add(Instruction.CreateLdcI4(1));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lSha));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stelem_Ref));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Dup));
        cBody.Instructions.Add(Instruction.CreateLdcI4(2));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, ".zip\",\"browser_download_url\":\""));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stelem_Ref));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Dup));
        cBody.Instructions.Add(Instruction.CreateLdcI4(3));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lInfo));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getInfoDownloadUrl));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "\""));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, ""));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, strReplace));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stelem_Ref));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Dup));
        cBody.Instructions.Add(Instruction.CreateLdcI4(4));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "\",\"created_at\":\""));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stelem_Ref));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Dup));
        cBody.Instructions.Add(Instruction.CreateLdcI4(5));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloca, lDt));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "O"));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Call, dtToStringFmt));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stelem_Ref));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Dup));
        cBody.Instructions.Add(Instruction.CreateLdcI4(6));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "\"}"));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stelem_Ref));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Call, strConcatArr));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lAssetJson));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lAssetJson));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Call, deserializeAsset));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lAsset));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lAsset));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Brfalse, endLeave));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lAsset));
        cBody.Instructions.Add(Instruction.CreateLdcI4(1));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, setAssetIsLatest));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Call, appGetCurrent));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lApp));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lApp));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Brfalse, endLeave));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Newobj, closureCtor));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stloc, lClosure));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lClosure));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lVm));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stfld, fVm));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lClosure));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lSha));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stfld, fSha));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lClosure));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lAsset));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Stfld, fAsset));

        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lApp));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, appGetDispatcher));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc, lClosure));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Ldftn, applyOnUi));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Newobj, actionCtor));
        cBody.Instructions.Add(Instruction.Create(OpCodes.Callvirt, dispatcherInvoke));

        cBody.Instructions.Add(endLeave);

        // catch (Exception) { }
        cBody.Instructions.Add(catchStart);
        cBody.Instructions.Add(Instruction.Create(OpCodes.Leave, leaveTry));
        cBody.Instructions.Add(leaveTry);

        cBody.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = catchStart,
            HandlerStart = catchStart,
            HandlerEnd = leaveTry,
            CatchType = new TypeRefUser(mod, "System", "Exception", sysRuntime)
        });
        uvm.Methods.Add(onLoadCompleted);

        // Build UpdateViewModel.WrapLoadAsyncCn(Task origTask, UpdateViewModel vm)
        var action2Ctor = new MemberRefUser(mod, ".ctor",
            MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.Object, mod.CorLibTypes.IntPtr),
            new TypeSpecUser(actionTaskObjSig));
        var getDefaultScheduler = new MemberRefUser(mod, "get_Default",
            MethodSig.CreateStatic(taskSchedulerRef.ToTypeSig()), taskSchedulerRef);
        var continueWithRef = new MemberRefUser(mod, "ContinueWith",
            MethodSig.CreateInstance(taskTypeRef.ToTypeSig(), actionTaskObjSig, mod.CorLibTypes.Object, taskSchedulerRef.ToTypeSig()),
            taskTypeRef);

        var wrapMethod = new MethodDefUser(
            "WrapLoadAsyncCn",
            MethodSig.CreateStatic(taskTypeRef.ToTypeSig(), taskTypeRef.ToTypeSig(), uvm.ToTypeSig()),
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig);
        wrapMethod.Body = new CilBody();
        wrapMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        wrapMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        wrapMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, onLoadCompleted));
        wrapMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, action2Ctor));
        wrapMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        wrapMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Call, getDefaultScheduler));
        wrapMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, continueWithRef));
        wrapMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        uvm.Methods.Add(wrapMethod);

        // Hook LoadAsync() final ret
        var loadInstrs = loadAsync.Body.Instructions;
        for (int i = 0; i < loadInstrs.Count; i++)
        {
            if (loadInstrs[i].OpCode == OpCodes.Ret)
            {
                // Keep existing ret target valid by mutating it to ldarg.0 and inserting call + ret
                loadInstrs[i].OpCode = OpCodes.Ldarg_0;
                loadInstrs[i].Operand = null;
                loadInstrs.Insert(i + 1, Instruction.Create(OpCodes.Call, wrapMethod));
                loadInstrs.Insert(i + 2, Instruction.Create(OpCodes.Ret));
                i += 2;
            }
        }
        loadAsync.Body.SimplifyBranches();
        loadAsync.Body.OptimizeBranches();

        actions.Add($"已接入官方无限制通道直连兜底（{infoUrl}），彻底解决 GitHub API 403 限流导致的 No commits found");
        return 1;
    }
}
