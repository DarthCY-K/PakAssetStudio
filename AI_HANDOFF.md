# PAK Asset Studio - 维护交接

## 1. 项目定位

PAK Asset Studio 是 Windows x64 WPF 桌面工具，编排传统 UE4 PAK 的扫描、有序解包、UModel 模型/贴图导出，以及可选的 glTF 合并和 FBX 转换。它不是 Unreal 资源解析器，核心解析能力来自随包发布的 repak、UE Viewer（UModel）和 Assimp。

当前代码基线位于 v0.4.0 之后。仓库不应包含游戏、PAK 样本、解包结果、日志、AES 密钥或历史发布 ZIP。

## 2. 技术栈

- C#、WPF、.NET 10，目标 `net10.0-windows`
- WPF-UI 4.x 深色主题
- MinVer 从 `v*` git tag生成版本号
- Python 3.14 embeddable runtime，仅使用标准库和 ctypes
- xUnit 测试及 Python `unittest`
- GitHub Actions Windows 构建、测试、publish 和发布布局校验

## 3. 代码结构

```text
PakAssetStudio/
  Models/PakEntry.cs             PAK 扫描结果、解包候选能力和分类
  Models/WorkflowOptions.cs      不可变工作流参数
  Services/ProcessRunner.cs      外部进程接口/实现、输出和进程树取消
  Services/PakToolService.cs     repak 扫描、脱敏诊断、压缩检查和排序
  Services/Ue4ProfileDetector.cs PAK 版本范围提示；只自动选择唯一版本
  Services/WorkflowService.cs    输出所有权、备份、解包、导出、转换和日志
  Services/LocalizationService.cs
  Services/UiLogBuffer.cs
  MainWindow.xaml(.cs)
PakAssetStudio.Tests/            xUnit 单元、合成 PAK 集成及可选真实 PAK 测试
tools/merge_gltf.py              保守、非破坏、原子 glTF 合并
tools/convert_gltf_to_fbx.py     Assimp 转换、验证和安全源清理
tools/check_part_bounds.py       开发诊断脚本：验证分片模型世界坐标是否烘焙进几何
                                 （不进发布包，无自动化测试）
tools/tests/                     Python 脚本测试
scripts/Publish.ps1             干净 publish、布局校验、ZIP 与 SHA-256
scripts/Test-PublishLayout.ps1   本地与 CI 共用的发布布局验收
.github/workflows/ci.yml
```

## 4. 工作流及不变量

1. 递归发现 `.pak`，用 repak `info` 读取索引。
2. `PakEntry.IsValid` 只表示索引可读；`CanAttemptExtraction` 还要求压缩方式为 None/Zlib/Gzip/Zstd/Oodle（Oodle 依赖随附的 `oo2core_9_win64.dll`）。
3. AES 输入变化必须使扫描缓存失效。不可处理的 PAK 在开始前必须由用户确认跳过。
4. PAK 容器版本只能推出范围。V5/V7/V8A/V9 唯一对应 UE4.20/4.21/4.22/4.25；其余范围不得自动猜 profile。
5. 输出根目录使用 `.pakassetstudio-output.json` 绑定输入游戏目录，并在任务期间持有 `.pakassetstudio.lock` 独占锁。不得操作非空、无标记的输出目录；选择 `GameName/Content/Paks` 时必须把整个 `GameName` 视为受保护输入。
6. 未勾选覆盖时，已有受管理产物必须导致任务停止。勾选覆盖时先把旧目录移动为同卷备份；失败时备份保留。仅转换已有 ExportedAssets 的原地模式也必须从备份复制工作副本，不能逐文件无备份覆盖。
7. 每次提取使用干净的 CookedAssets，并始终给 repak `-f`，使后解包 patch 覆盖 base。该 `-f` 与“覆盖旧任务”不是同一语义。
8. UModel 输出到干净的 ExportedAssets，因此不能依赖 `-nooverwrite` 混合旧结果。进程成功后仍须验证所请求的 glTF 与 PNG/HDR 确实产生。
9. 自动模型合并默认关闭。启用后只合并显式 `_partNN`/`_pieceNN`/`_meshNN`/`_polySurfaceNN` 组，源文件始终保留，输出为 `__merged.gltf`；同时转 FBX 时必须强制使用独立 FbxAssets 副本。
10. FBX 未启用覆盖时保留同名新 glTF并报告跳过；启用后使用临时 FBX 验证成功再替换。只有成功转换且无剩余 glTF 引用的本地 buffer 才会删除。
11. `PakAssetStudio.log` 以 append + AutoFlush 持续写盘。UI 日志仍有界；工作流进程不得捕获整份输出到内存。
12. CookedAssets 只允许在经过所有权检查的输出根目录下删除。控制文件和受管理目录树不得包含 reparse point。

## 5. 固定发布布局

```text
Tools/repak/repak.exe
Tools/repak/oo2core_9_win64.dll
Tools/umodel/umodel_64.exe
Tools/umodel/SDL2_64.dll
Tools/assimp/assimp-vc143-mt.dll
Tools/python/python.exe
Tools/convert_gltf_to_fbx.py
Tools/merge_gltf.py
Prerequisites/vc_redist.x64.exe
```

原生文件和脚本必须保持 `CopyToPublishDirectory="PreserveNewest"` 及 `ExcludeFromSingleFile="true"`。项目 LICENSE 及实际存在的 repak、UModel、Assimp、Python 许可证必须进入 publish；`THIRD-PARTY-NOTICES` 完整包含 SDL2 条款及 VC++ Runtime 分发声明。`Test-PublishLayout.ps1` 会在本地发布和 CI 中验证。

## 6. 构建与测试

```powershell
dotnet build .\PakAssetStudio.slnx -c Release
dotnet test .\PakAssetStudio.slnx -c Release
.\tools\python\runtime\python.exe -m unittest discover -s .\tools\tests -v
```

可选真实 PAK 测试：

```powershell
$env:PAK_TEST_DIR = 'C:\Path\To\Paks'
dotnet test .\PakAssetStudio.slnx -c Release
```

正式发布（含测试、第三方版本/哈希清单、ZIP 与 SHA-256）：

```powershell
.\scripts\Publish.ps1 -Version 0.5.0
```

## 7. 能力边界

- 不支持 IoStore `.utoc/.ucas`。
- 不支持自动恢复蓝图、Actor 变换、完整材质图、源码或可编辑关卡。
- UModel 使用 `-noanim`，当前不导出动画；流程也未实现音频导出。
- 一次任务只有一个 AES 密钥；多 KeyGuid 游戏应分批处理。解包当前处理全部候选 PAK，不支持逐包选择。
- UModel 对特定游戏可能需要自定义 `-game=` tag，界面 profile 可编辑。
- 不要把“repak 能读取索引”描述成“该 PAK 一定能完整解包”。

## 8. 维护要求

- 修改输出、覆盖、合并或清理语义时，必须增加重复运行和失败恢复测试。
- 更新任何第三方二进制后，记录版本/来源，验证许可证、扫描、导出、转换和中文路径。
- 新增语言键必须同时更新 `zh-CN.json` 与 `en-US.json`。
- 不记录进程参数、AES 密钥或未经脱敏的 repak 失败命令。
- 发布前至少通过 CI、启动应用和一个合法小 PAK。CI 已用 repak 动态 base/patch fixture 实测扫描与覆盖解包，并用自生成三角形实测 glTF -> FBX -> 重新导入。

## 9. macOS 移植进度（2026-08-01，未发布）

### 现状

- Windows WPF 版功能完整（0.6.0+）；macOS 版为**开发中**的 Avalonia 移植，尚未发布。
- 导出链路采用方案 B（MVP）：macOS 上经 Wine/CrossOver 运行 Windows 版 `umodel_64.exe`，导出质量与 Windows 版一致；长期替代方案（CUE4Parse 重写导出器）仅调研未立项。

### 项目结构

- `PakAssetStudio.Avalonia/`：跨平台外壳（`net10.0` + Avalonia 12.1.1 + DataGrid 12.1.0），深色 Fluent 主题，`RootNamespace=PakAssetStudio`。
- **共享核心层**：Avalonia csproj 用 `<Compile Include="..\PakAssetStudio\...">` Link 编译 `Models/**` 与 `Services/*.cs`（排除 WPF 专属 `LocExtension.cs`）——两个程序集编译同一份源文件，双平台逻辑永不分叉；`Languages/*.json` 同样 Link 进输出。
- Avalonia 专属文件：`LocExtension.cs`（`{l:Loc}` 绑定扩展，语义与 WPF 版一致）、`SimpleDialog.cs`（MessageBox 替代，`ShowDialog` 异步，所有调用点已 await）、`MainWindow.axaml(.cs)`（WPF 版 779 行 code-behind 全量移植）。
- 共享服务若新增 Windows 专属文件，必须在 Avalonia csproj 的 `Exclude` 中排除（如 `LocExtension.cs` 先例）。

### 平台服务（共享文件，双平台编译）

- `Services/PlatformPaths.cs`：repak（`.exe` ↔ 无扩展名）、assimp（`.dll` ↔ `libassimp.dylib`）、python（嵌入式 ↔ 系统 `python3`，工具脚本仅用标准库）按平台解析；`_umodelPath` 刻意保持 `.exe`（Wine 场景需要）。
- `Services/WineService.cs`：Wine 探测（CrossOver → Homebrew 常见目录 → PATH）、macOS 路径 → `Z:\` 盘转换、UModel 命令行组装。公开带 `useWineDrive` 的纯函数重载，供单测直接覆盖 mac 分支（Windows 测试机上 `IsWineNeeded=false`）。
- `WorkflowService.ResolveUmodelLaunch()`：UModel 启动三元组（可执行文件/工作目录/参数）解析；Windows 平台短路返回原值（行为与旧版一致）；Wine 缺失抛 `Error_MissingWine`（语言键已加，含安装引导）。

### 已验证

- 三项目（WPF + Tests + Avalonia）构建 0 警告 0 错误；测试 73/73（含 10 个 WineService 测试）。
- Windows 上 `dotnet publish PakAssetStudio.Avalonia -r osx-arm64 --self-contained` 可产出 Mach-O arm64 可执行文件（实测）。
- Avalonia 版窗口在 Windows 上可正常拉起（含完整 UI 与数据网格）。

### 待办（无 Mac 环境）

1. **repak macOS 二进制**：官方 v0.2.3 只发 win/linux（v0.1.8 历史上有 x86_64-apple-darwin，过旧不可用）；需 macOS 上 `cargo build --release`（纯 Rust，无 Windows 专属代码，已取证），arm64+x64 用 `lipo -create` 合并 universal。免费 macOS CI：Codemagic 500 分钟/月（private 可用）、Cirrus CI（public 500 分钟/月）。
2. **Oodle mac dylib**：`liboo2coremac64.2.9.10.dylib`（SHA-256 `b09af35f6b84a61e2b6488495c7927e1cef789b969128fa1c845e51a475ec501`，与 repak 的 oodle_loader 期望逐字节一致；jsdelivr 可下载，universal 双架构）——放 repak 二进制旁即自动加载，零代码改动。
3. **打包/签名/公证**：AvaloniaUI.Parcel 1.0.6（文档明言 Windows 可建 .app）、rcodesign 0.29.0（官方发布 Windows 预编译版，无需 Mac）、Apple Developer 账号 $99/年是公证硬前提；无账号只能 ad-hoc（Gatekeeper 拦分发）。
4. **真机验证**：`wine + umodel_64.exe` 无头导出链路必须 Mac 实测（调研评级 B-，社区仅 Linux 一手证据）；macOS 上系统 python3 跑 merge/fbx 脚本需顺带验证。
5. **方案 A（CUE4Parse）**：仅调研（15-25 人天；输出 .glb 与现有 merge/fbx 脚本不兼容；Crunch 贴图 mac 不可解；ACL 动画需自编 natives；4.25+ 需 .usmap；用户测试样本 .uptnl 无法验证导出链路）。

### Avalonia 12 已知坑（移植实测）

- **移除了 `ICollectionView`/`CollectionViewSource`/`DataGridCollectionView`** → 过滤集合自行增量维护（见 `MainWindow.axaml.cs` 的 `RefreshPakFilter`/`PakEntries_CollectionChanged`）。
- 拖放 API 大改：`DragEventArgs.Data` → `DataTransfer`；`DataFormats.Files` → `DataFormat.File`；取文件走 `IDataTransferItem.TryGetRaw(DataFormat.File)`。
- `ToggleSwitch` 无 Checked/Unchecked 事件 → `IsCheckedChanged`。
- `Dispatcher.BeginInvoke` → `Dispatcher.UIThread.Post`。
- 编译绑定默认开启 → Window 根加 `x:CompileBindings="False"` 恢复 WPF 式反射绑定。
- DataGrid NuGet 版本 12.1.0（核心包 12.1.1，无 12.1.1）；无 `AlternatingRowBackground`；TextBox 滚动条是附加属性 `ScrollViewer.VerticalScrollBarVisibility`。
- Avalonia `TextBox.Text` 是 `string?`（WPF 是非空），`.Trim()` 前需 `?? string.Empty`。
