using System.Diagnostics;
using System.Text;
using dnlib.DotNet;

namespace FModelCnPatcher;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        if (args.Length < 2)
        {
            PrintUsage();
            return 1;
        }

        string cmd = args[0].ToLowerInvariant();
        string targetExe = Path.GetFullPath(args[1]);
        string? langPath = null;
        bool noUpdateFix = false;
        bool keepWpfHw = false;

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--lang" && i + 1 < args.Length)
            {
                langPath = Path.GetFullPath(args[++i]);
            }
            else if (args[i] == "--no-update-fix")
            {
                noUpdateFix = true;
            }
            else if (args[i] == "--keep-wpf-hw")
            {
                keepWpfHw = true;
            }
        }

        if (!File.Exists(targetExe))
        {
            Console.Error.WriteLine($"[错误] 找不到目标文件: {targetExe}");
            return 2;
        }

        try
        {
            return cmd switch
            {
                "patch" => RunPatch(targetExe, langPath, noUpdateFix, keepWpfHw),
                "restore" => RunRestore(targetExe),
                "inspect" => RunInspect(targetExe),
                _ => InvalidCmd(cmd)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[失败] {ex.GetType().Name}: {ex.Message}");
            return 99;
        }
    }

    private static int InvalidCmd(string cmd)
    {
        Console.Error.WriteLine($"[错误] 未知命令: {cmd}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("FModel 通用全汉化与更新检测修复工具 v1.0.2");
        Console.WriteLine("用法:");
        Console.WriteLine("  FModelCnPatcher patch   <FModel.exe路径> [--lang <zh-CN.json路径>] [--no-update-fix] [--keep-wpf-hw]");
        Console.WriteLine("  FModelCnPatcher restore <FModel.exe路径>");
        Console.WriteLine("  FModelCnPatcher inspect <FModel.exe路径>");
    }

    private static int RunInspect(string targetExe)
    {
        var ver = FileVersionInfo.GetVersionInfo(targetExe);
        bool patched = SingleFileBundle.QuickHasPatchMarker(targetExe);
        string bakPath = targetExe + ".orig.bak";
        Console.WriteLine($"文件: {targetExe}");
        Console.WriteLine($"产品版本: {ver.ProductVersion}");
        Console.WriteLine($"是否已打补丁: {(patched ? "是" : "否")}");
        Console.WriteLine($"原版备份存在: {(File.Exists(bakPath) ? "是 (" + bakPath + ")" : "否")}");
        return 0;
    }

    private static int RunRestore(string targetExe)
    {
        string bakPath = targetExe + ".orig.bak";
        if (!File.Exists(bakPath))
        {
            Console.Error.WriteLine($"[错误] 未找到官方原版备份文件: {bakPath}");
            if (!SingleFileBundle.QuickHasPatchMarker(targetExe))
                Console.Error.WriteLine("提示：当前的 FModel.exe 本身就是未经本工具修改的官方原版，无需还原。");
            return 3;
        }

        if (SingleFileBundle.QuickHasPatchMarker(bakPath))
        {
            Console.Error.WriteLine($"[错误] 备份文件自身包含补丁标记，拒绝覆盖: {bakPath}");
            return 4;
        }

        File.Copy(bakPath, targetExe, overwrite: true);
        Console.WriteLine("[成功] 已从官方原版备份无损还原 FModel.exe！");
        Console.WriteLine($"       还原来源: {bakPath}");
        return 0;
    }

    private static int RunPatch(string targetExe, string? langPath, bool noUpdateFix, bool keepWpfHw)
    {
        langPath ??= Path.Combine(AppContext.BaseDirectory, "..", "lang", "zh-CN.json");
        langPath = Path.GetFullPath(langPath);
        if (!File.Exists(langPath))
        {
            var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "lang", "zh-CN.json"));
            if (File.Exists(fallback)) langPath = fallback;
            else throw new FileNotFoundException($"找不到中文词库文件: {langPath}");
        }

        var lang = LangPack.Load(langPath);
        string bakPath = targetExe + ".orig.bak";

        // Determine source: if targetExe is clean (unpatched), back it up as .orig.bak first.
        // If targetExe is already patched, read cleanly from .orig.bak so repeat runs are idempotent.
        string sourceExe;
        if (!SingleFileBundle.QuickHasPatchMarker(targetExe))
        {
            Console.WriteLine($"[1/5] 检测到官方原版 FModel.exe，正在备份到: {bakPath}");
            File.Copy(targetExe, bakPath, overwrite: true);
            sourceExe = bakPath;
        }
        else
        {
            if (!File.Exists(bakPath) || SingleFileBundle.QuickHasPatchMarker(bakPath))
                throw new InvalidOperationException($"目标文件已打过补丁，但找不到干净的原版备份: {bakPath}");
            Console.WriteLine($"[1/5] 检测到已打过补丁，将从干净原版备份重新应用最新词库: {bakPath}");
            sourceExe = bakPath;
        }

        var bundle = SingleFileBundle.Load(sourceExe);
        var ver = FileVersionInfo.GetVersionInfo(sourceExe);
        Console.WriteLine($"[信息] 识别版本: {ver.ProductVersion} | Bundle v{bundle.MajorVersion}.{bundle.MinorVersion} ({bundle.Entries.Count} 个内置模块)");

        int bamlReplaced = 0;
        int enumReplaced = 0;
        int codeReplaced = 0;
        int statusPatched = 0;
        bool fontPatched = false;
        bool wpfFixed = false;
        var updateActions = new List<string>();

        // Prepare dnlib assembly resolver backed by entries inside this bundle
        using var resolver = new BundleAssemblyResolver(bundle);
        var modCtx = new ModuleContext(resolver);
        resolver.DefaultModuleContext = modCtx;

        // Patch FModel.dll
        var fmodelEntry = bundle.Entries.FirstOrDefault(e => e.RelativePath.Equals("FModel.dll", StringComparison.OrdinalIgnoreCase))
                          ?? throw new InvalidDataException("Single-File Bundle 中不包含 FModel.dll。");

        Console.WriteLine("[2/5] 正在汉化 WPF 界面 (BAML) 与主程序提示文本...");
        using (var mod = ModuleDefMD.Load(fmodelEntry.Data, modCtx))
        {
            bamlReplaced = BamlLocalizer.LocalizeFModelResources(mod, lang, out _);
            enumReplaced += MetadataAndIlLocalizer.LocalizeEnumDescriptions(mod, lang);
            statusPatched = MetadataAndIlLocalizer.LocalizeStatusLabels(mod, lang);
            codeReplaced += MetadataAndIlLocalizer.LocalizeAssemblyStrings(mod, lang, restrictToFModelUiNamespaces: true);

            if (!noUpdateFix)
                UpdateCheckFixer.Apply(mod, out updateActions);

            if (!keepWpfHw)
                wpfFixed = WpfRenderFixer.Apply(mod);

            using var outMs = new MemoryStream();
            mod.Write(outMs);
            fmodelEntry.Data = outMs.ToArray();
        }

        // Patch CUE4Parse*.dll enum [Description] attributes
        Console.WriteLine("[3/5] 正在汉化 CUE4Parse 导出选项枚举与 3D 查看器 (Snooper/Editor)...");
        foreach (var dllName in new[] { "CUE4Parse-Conversion.dll", "CUE4Parse.dll" })
        {
            var entry = bundle.Entries.FirstOrDefault(e => e.RelativePath.Equals(dllName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) continue;
            using var mod = ModuleDefMD.Load(entry.Data, modCtx);
            int c = MetadataAndIlLocalizer.LocalizeEnumDescriptions(mod, lang);
            if (c > 0)
            {
                enumReplaced += c;
                using var outMs = new MemoryStream();
                mod.Write(outMs);
                entry.Data = outMs.ToArray();
            }
        }

        // Patch Editor.dll & Snooper.dll (ImGui Chinese font + UI strings)
        var editorEntry = bundle.Entries.FirstOrDefault(e => e.RelativePath.Equals("Editor.dll", StringComparison.OrdinalIgnoreCase));
        if (editorEntry != null)
        {
            using var mod = ModuleDefMD.Load(editorEntry.Data, modCtx);
            fontPatched = SnooperFontPatcher.PatchImGuiFonts(mod);
            codeReplaced += MetadataAndIlLocalizer.LocalizeAssemblyStrings(mod, lang, restrictToFModelUiNamespaces: false, includeSnooperStrings: true);
            using var outMs = new MemoryStream();
            mod.Write(outMs);
            editorEntry.Data = outMs.ToArray();
        }
        else
        {
            // Fallback for older FModel builds where ImGuiController lived inside FModel.dll
            using var mod = ModuleDefMD.Load(fmodelEntry.Data, modCtx);
            if (SnooperFontPatcher.PatchImGuiFonts(mod))
            {
                fontPatched = true;
                using var outMs = new MemoryStream();
                mod.Write(outMs);
                fmodelEntry.Data = outMs.ToArray();
            }
        }

        var snooperEntry = bundle.Entries.FirstOrDefault(e => e.RelativePath.Equals("Snooper.dll", StringComparison.OrdinalIgnoreCase));
        if (snooperEntry != null)
        {
            using var mod = ModuleDefMD.Load(snooperEntry.Data, modCtx);
            int c = MetadataAndIlLocalizer.LocalizeAssemblyStrings(mod, lang, restrictToFModelUiNamespaces: false, includeSnooperStrings: true);
            if (c > 0)
            {
                codeReplaced += c;
                using var outMs = new MemoryStream();
                mod.Write(outMs);
                snooperEntry.Data = outMs.ToArray();
            }
        }

        Console.WriteLine("[4/5] 正在写入更新修复补丁与打包单文件可执行程序...");
        var markerBytes = Encoding.UTF8.GetBytes($"patched_at={DateTime.Now:O}\nversion={ver.ProductVersion}\n");
        bundle.SetOrAddEntry(SingleFileBundle.PatchMarkerEntryName, 0, markerBytes);
        bundle.Save(targetExe);

        Console.WriteLine("[5/5] 完成！");
        Console.WriteLine($"  - WPF 界面词条替换: {bamlReplaced} 处");
        Console.WriteLine($"  - 枚举描述替换:     {enumReplaced} 处");
        Console.WriteLine($"  - 代码/3D界面词条:  {codeReplaced} 处 (状态栏补丁: {statusPatched})");
        Console.WriteLine($"  - 3D查看器中文字体: {(fontPatched ? "已注入微软雅黑 + CJK 字形范围" : "未找到 ImGui 入口（已跳过）")}");
        Console.WriteLine($"  - WPF 防白屏修复:   {(wpfFixed ? "已启用 WPF 2D 软渲染（规避双显卡/驱动钩子 D3D9 白屏，不影响 3D 查看器硬件加速）" : "未启用")}");
        foreach (var act in updateActions)
            Console.WriteLine($"  - 更新修复:         {act}");
        Console.WriteLine($"  - 原版备份保存于:   {bakPath}");

        return 0;
    }

    private sealed class BundleAssemblyResolver : IAssemblyResolver, IDisposable
    {
        private readonly Dictionary<string, byte[]> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AssemblyDef> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly string? _runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();

        public ModuleContext? DefaultModuleContext { get; set; }

        public BundleAssemblyResolver(SingleFileBundle bundle)
        {
            foreach (var e in bundle.Entries)
            {
                if (e.Type == 1 && !e.RelativePath.Contains('/'))
                {
                    var name = Path.GetFileNameWithoutExtension(e.RelativePath);
                    _map[name] = e.Data;
                }
            }
        }

        public AssemblyDef? Resolve(IAssembly assembly, ModuleDef sourceModule)
        {
            var name = assembly.Name.String;
            if (_cache.TryGetValue(name, out var cached))
                return cached;

            if (_map.TryGetValue(name, out var bytes))
            {
                var mod = ModuleDefMD.Load(bytes, DefaultModuleContext);
                if (mod.Assembly != null)
                {
                    _cache[name] = mod.Assembly;
                    return mod.Assembly;
                }
            }

            if (_runtimeDir != null)
            {
                var rtPath = Path.Combine(_runtimeDir, name + ".dll");
                if (File.Exists(rtPath))
                {
                    var mod = ModuleDefMD.Load(rtPath, DefaultModuleContext);
                    if (mod.Assembly != null)
                    {
                        _cache[name] = mod.Assembly;
                        return mod.Assembly;
                    }
                }
            }

            return null;
        }

        public void Dispose()
        {
            foreach (var a in _cache.Values)
            {
                a.ManifestModule?.Dispose();
            }
            _cache.Clear();
        }
    }
}
