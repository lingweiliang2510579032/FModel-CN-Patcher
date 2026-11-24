# FModel-CN-Patcher

**FModel 通用全汉化与更新检测修复工具** —— 基于动态二进制修补的跨版本自动汉化方案，支持对**任意官方版本的单文件 `FModel.exe`** 一键应用全中文界面与更新检测修复，并支持 **100% 哈希一致无损还原官方英文原版**。

> [!TIP]
> 无需等待他人维护过时的魔改汉化版！去 [FModel 官方仓库](https://github.com/4sval/FModel) 下载任意新版后，双击 `一键汉化FModel.bat` 即可立即生效。

## 核心特性

1. **跨版本动态修补，不随官方更新失效**
   - 直接解析官方 .NET Single-File Bundle（兼容未压缩与 Deflate 压缩的单文件结构）及内部托管模块，按语言字典与 IL 模式动态注入，**不锁定特定版本**。
   - 后续新增翻译只需编辑 `lang/zh-CN.json` 并再次运行 `一键汉化FModel.bat`，工具会自动基于首次生成的 `FModel.exe.orig.bak` 幂等重打补丁，不会重复叠加损坏程序。
2. **全模块中文化**
   - **WPF 主界面与设置窗口（BAML）**：菜单栏、选项卡、右键菜单、设置项、目录选择器、搜索/引用窗口、AES 密钥弹窗、更新弹窗等。
   - **CUE4Parse 导出选项枚举**：通过修改内置枚举 `[Description]` 元数据，汉化网格/动画/贴图/音频等导出格式与材质导出选项下拉框。
   - **状态栏与运行提示**：注入 `FStatus.UpdateStatusLabel` 中文状态映射及主程序界面提示文本。
   - **3D 查看器（Snooper / Editor）字体注入与 ImGui 汉化**：自动回退加载 Windows 微软雅黑（`msyhbd.ttc` / `msyh.ttc` / `simhei.ttf`）并固定 CJK 字形范围，解决 3D 查看器中文显示为方块的问题。
3. **修复官方更新检测（解决 `No commits found` 与启动弹窗卡顿）**
   - **静音启动阻塞弹窗**：抑制启动时强制弹出的模态 `UpdateView` 更新窗口及断网/限流报错弹窗（仍保留右下角状态栏更新状态指示）。
   - **避免 GitHub API 403 限流**：禁用 `LoadCoAuthors` 对 `api.github.com/users/{name}` 的高频并发请求。
   - **官方通道直连兜底**：在 `UpdateViewModel.LoadAsync` 结束后接入 `https://api.fmodel.app/v1/infos/Qa` 直连回退解析，解决国内/未登录环境下 GitHub API 限流导致更新界面显示 `No commits found`、点击下载无反应的问题。
4. **修复双显卡 / 虚拟显示器环境下 WPF 白屏（v1.0.1 新增）**
   - 部分笔记本双显卡（核显+独显）、远程虚拟显示器或注入显卡驱动 Hook 的环境中，修改后的 `FModel.exe` 可能因未匹配原驱动配置导致 WPF Direct3D 9Ex 合成呈现纯白屏。
   - 补丁器在 `App.OnStartup` 注入 `RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly`，彻底避免 2D 界面白屏；**3D 查看器（Snooper / OpenGL）仍运行在独立硬件加速窗口中，性能完全不受影响**（如需强制保留 WPF 硬件加速，可附加 `--keep-wpf-hw`）。
5. **100% 无损还原官方英文原版**
   - 首次执行汉化时会在同目录自动备份 `FModel.exe.orig.bak`。
   - 随时运行 `还原官方英文原版.bat`，即可毫秒级无损恢复为字节与 SHA-256 完全一致的官方原版。

## 使用方法

### 方式一：一键脚本（推荐）

1. 前往 [Releases](../../releases) 下载并解压 `FModel-CN-Patcher-v*.zip`（**请完整解压整个压缩包，保留 `tools/` 和 `lang/` 文件夹**）。
2. 通过以下任意一种方式运行 `一键汉化FModel.bat`：
   - **放在一起**：将解压后的文件夹内容放到 `FModel.exe` 同目录（或把 `FModel-CN-Patcher` 文件夹整体放进 `FModel.exe` 所在目录），直接双击 `一键汉化FModel.bat` 自动识别。
   - **拖拽使用**：直接把任意位置下载的 `FModel.exe` 拖放到 `一键汉化FModel.bat` 图标上松开。
   - **弹窗选择**：在任意位置直接双击 `一键汉化FModel.bat`，按提示在弹出的文件选择框中选择你的 `FModel.exe`。
3. 需还原官方英文原版时，同样方式运行 `还原官方英文原版.bat` 即可。

### 方式二：命令行调用

```cmd
tools\FModelCnPatcher.exe patch   "C:\路径\FModel.exe" [--lang "lang\zh-CN.json"] [--no-update-fix] [--keep-wpf-hw]
tools\FModelCnPatcher.exe restore "C:\路径\FModel.exe"
tools\FModelCnPatcher.exe inspect "C:\路径\FModel.exe"
```

- `--lang <路径>`：指定自定义 JSON 语言包（默认自动读取 `lang\zh-CN.json`）。
- `--no-update-fix`：仅汉化界面与 3D 查看器字体，不修改更新检测逻辑。
- `--keep-wpf-hw`：不注入 WPF `SoftwareOnly` 防白屏模式，保持原生 WPF D3D9 硬件加速。

## 目录结构

```text
FModel-CN-Patcher/
├── scripts/
│   ├── 一键汉化FModel.bat        # 自动识别/拖放/选择框汉化入口
│   └── 还原官方英文原版.bat      # 一键无损还原入口
├── lang/
│   └── zh-CN.json                # 外置开源简体中文词库
└── src/
    └── FModelCnPatcher/          # .NET 8 核心修补器源码 (dnlib + BamlParser)
```

## 本地编译与运行环境要求

- **使用预编译发布包**：目标机器需已安装 **.NET Desktop Runtime 8.0 或更高版本**（运行新版 `FModel.exe` 本身已自带 `.NET 8/10` 环境；`FModelCnPatcher` 已配置 `RollForward=LatestMajor`，有 `.NET 8/9/10` 任意运行时即可直接启动）。
- **从源码编译**：

```powershell
dotnet publish src/FModelCnPatcher/FModelCnPatcher.csproj -c Release -r win-x64 -p:DebugType=None -p:DebugSymbols=false -o dist/tools
```

## 开源协议与致谢

- 本项目遵循 [GNU General Public License v3.0](LICENSE) 开源。
- 词库基线对照官方项目 [4sval/FModel](https://github.com/4sval/FModel) 与 [FModel/Snooper](https://github.com/FModel/Snooper) 最新版本构建，仅用于本地化与修复更新接口访问体验。
