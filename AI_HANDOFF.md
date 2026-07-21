# PAK Asset Studio - AI 开发交接文档

## 1. 项目结论

PAK Asset Studio v0.1.1 是一个 Windows WPF 桌面工具，封装传统 UE4 PAK 的扫描、有序合并解包、UModel 模型贴图导出，以及 glTF 到 FBX 转换流程。

仓库已经清理为独立工程，不包含原始 UE 游戏、PAK 样本、解包结果或历史发布 ZIP。不要假设本地存在真实 PAK 测试数据。

## 2. 技术栈与边界

- C# / WPF / .NET 10，目标 `net10.0-windows`
- UI 使用 WPF-UI (lepo.co) 4.x 深色主题 + 自定义青绿强调色，主窗口为 `ui:FluentWindow`（Mica 背景），强调色在 `App.OnStartup` 通过 `ApplicationAccentColorManager.Apply` 设置
- 多语言：`LocalizationService` 从程序目录 `Languages/*.json` 加载（缺项回退 zh-CN，再回退键名），XAML 用 `{l:Loc Key}`（`LocExtension`），代码用 `LocalizationService.Text/TextFormat`；语言偏好存于 `%LOCALAPPDATA%/PakAssetStudio/settings.json`；新增语言只需投放同名格式 JSON
- Windows 10/11 x64
- 不要求安装 Unreal Editor
- 发布包自带 .NET runtime、Python embeddable runtime 和原生工具
- 支持传统 `.pak`，暂不支持 `.utoc/.ucas` IoStore
- 不负责恢复源码、完整蓝图或可直接运行的 UE 工程
- 只应处理用户拥有或已获授权的内容

## 3. 仓库结构

```text
PakAssetStudio.slnx
PakAssetStudio/
  Models/
  Services/
  App.xaml
  MainWindow.xaml
  MainWindow.xaml.cs
  PakAssetStudio.csproj
PakAssetStudio.Tests/
  Program.cs
  PakAssetStudio.Tests.csproj
tools/
  repak/
  umodel/
  assimp/
  python/runtime/
  vc_runtime/
  convert_gltf_to_fbx.py
README.md
AI_HANDOFF.md
```

`bin/`、`obj/`、`publish/`、`artifacts/` 和运行输出不应提交。

## 4. 核心组件

| 文件 | 职责 |
| --- | --- |
| `MainWindow.xaml(.cs)` | UI、输入校验、异步工作流和批量日志刷新 |
| `Models/PakEntry.cs` | PAK 元数据和 optional/patch 分类 |
| `Models/WorkflowOptions.cs` | 工作流参数 |
| `Services/ProcessRunner.cs` | 外部进程执行、输出捕获和取消 |
| `Services/PakToolService.cs` | repak 扫描、元数据解析和提取顺序 |
| `Services/Ue4ProfileDetector.cs` | 按 PAK 格式版本推测 UE4 版本标签（映射见 repak 兼容性表；V11 跨 4.26–5.3，默认取 ue4.26 并提示可手动切换） |
| `Services/WorkflowService.cs` | 解包、UModel 导出、目录复制与 FBX 转换 |
| `Services/UiLogBuffer.cs` | 有界 UI 日志队列，避免大量输出堵塞 UI 线程 |
| `tools/convert_gltf_to_fbx.py` | 通过 Assimp 转换并验证 FBX |

## 5. 工作流

1. 递归发现用户输入目录中的 `.pak`。
2. 使用 repak `info` 判断版本、压缩、索引加密和文件数。
3. 跳过不可读取或非 Unreal PAK；UI 默认隐藏不支持的包，可用开关显示。
4. 按基础包、optional 包、patch 包排序并依次解包到同一 cooked 目录。
5. 调用 UModel，按用户选定的 UE4 profile 导出 glTF、贴图和材质描述。
6. 可选复制导出目录，并用内置 Python + Assimp 将 glTF 转为 FBX。
7. 完整日志写入磁盘；UI 只显示有界批次。

低占用模式（`WorkflowOptions.LowResource`）：并行度钳制到 2，所有子进程以 `BelowNormal` 优先级运行（`ProcessRunner.RunAsync` 的 `priority` 参数）。

安全规则：

- 输出目录不能等于或位于输入游戏目录内部。
- 不修改原 PAK。
- AES 密钥只作为本地进程参数使用，不能写入 UI、磁盘日志或异常文本。
- 失败时保留已有输出，便于排查和续作。

## 6. 第三方文件固定布局

运行时按 `AppContext.BaseDirectory` 查找：

```text
Tools/repak/repak.exe
Tools/umodel/umodel_64.exe
Tools/umodel/SDL2_64.dll
Tools/assimp/assimp-vc143-mt.dll
Tools/python/python.exe
Tools/convert_gltf_to_fbx.py
Prerequisites/vc_redist.x64.exe
```

源码仓库中的文件位于 `tools/`，由 `PakAssetStudio.csproj` 通过 `Link` 映射到上述发布布局。原生 EXE/DLL 必须保留 `CopyToPublishDirectory="PreserveNewest"` 和 `ExcludeFromSingleFile="true"`，否则单文件发布后业务代码无法按路径启动它们。

## 7. 构建与测试

```powershell
dotnet build .\PakAssetStudio.slnx -c Release
dotnet run --project .\PakAssetStudio.Tests\PakAssetStudio.Tests.csproj -c Release
```

默认测试不依赖外部数据，覆盖提取顺序和 50,000 行 UI 日志压力场景。可选传入真实 PAK 目录执行集成扫描：

```powershell
dotnet run --project .\PakAssetStudio.Tests\PakAssetStudio.Tests.csproj -c Release -- C:\Path\To\Paks
```

发布命令：

```powershell
dotnet publish .\PakAssetStudio\PakAssetStudio.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o .\artifacts\publish\win-x64
```

## 8. v0.1.1 卡死修复

v0.1.0 在 UModel 产生大量输出时，会为每一行向 WPF Dispatcher 排队一次 UI 更新。后台流程结束后，UI 线程仍要消费庞大队列，表现为“处理完成后卡死”。

v0.1.1 的约束不能回退：

- `UiLogBuffer` 使用并发队列。
- DispatcherTimer 每 100 ms 批量刷新，每批最多 400 行。
- 待显示积压超过 5,000 行时只保留最近 1,000 行。
- TextBox 约 800,000 字符时裁剪，保留最近约 550,000 字符。
- 完整日志始终写入 `PakAssetStudio.log`。
- 大目录复制和结果统计在 `Task.Run` 中执行。

## 9. 后续优先事项

1. 将当前控制台式测试迁移到正式测试框架，并为 repak/UModel 进程层增加可替换接口。
2. 增加小型、可合法分发的 PAK fixture，覆盖解析和失败分支。
3. 增加 GitHub Actions Windows 构建、测试和 release workflow。
4. 增加版本化发布脚本、ZIP 和 SHA-256 自动生成。
5. 如需支持 IoStore，应新增独立后端，不要把 `.utoc/.ucas` 混入 repak 逻辑。

## 10. 维护注意事项

- 工作区可能包含用户未提交修改，操作前先看 `git status`。
- 不提交真实游戏文件、PAK、解包资源、日志或用户 AES 密钥。
- 不随意升级工具版本；升级后必须重新验证 PAK 扫描、UModel 导出和 FBX 转换。
- 发布验证至少检查应用启动、工具完整性、默认测试和一个小型转换样本。
