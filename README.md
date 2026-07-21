# PAK Asset Studio

PAK Asset Studio 是一个 Windows 桌面工具，用于扫描和解包传统 UE4 PAK，并批量导出模型、贴图和可选 FBX 副本。

## 功能

- 拖放或选择游戏目录，递归发现 `.pak`
- 使用 repak 判断 PAK 版本、压缩、加密和文件数
- 跳过 Chromium 等非 Unreal PAK
- 基础包、Optional 包、补丁包有序合并解包
- UModel 批量导出 glTF、PNG、HDR 和材质描述
- Assimp 批量转换和重新验证二进制 FBX
- 中文路径、取消、磁盘空间预检查、失败保留和持久日志
- 多语言界面（内置简体中文和 English，可向 `Languages/` 目录投放语言文件扩展）
- 不修改原游戏目录

## 使用

1. 运行 `PakAssetStudio.exe`。
2. 选择游戏根目录或直接拖入 Paks 目录。
3. 点击“扫描 PAK”，确认 Unreal PAK 状态（不支持的包默认隐藏，可用右上角“显示不支持的包”开关查看全部）。
4. UE4 版本标签会在扫描后按 PAK 格式版本自动识别（可手动修改）；加密包填写合法 AES 密钥。
5. 选择输出目录和处理步骤；电脑较卡时可开启“低占用模式”（并行度降低，子进程低优先级运行）。
6. 点击“开始处理”。

输出目录可能包含：

```text
CookedAssets/    解包后的 cooked 文件
ExportedAssets/  glTF、BIN、PNG、HDR 和材质描述
FbxAssets/       保留 glTF 时生成的 FBX 工作副本
PakAssetStudio.log
```

## 运行要求

- Windows 10/11 x64
- 不需要安装 Unreal Editor
- 不需要安装 Python，发布包包含 Python embeddable runtime
- 如果 Assimp 报运行库缺失，安装随发布包提供的 VC++ x64 Runtime

## 限制

- 传统 `.pak` 使用 repak；`.utoc/.ucas` IoStore 暂不支持
- UModel profile 必须与目标 UE4 版本匹配
- Cooked 资源不能还原为原始 C++、完整蓝图或可编辑关卡
- 仅处理自己拥有或已获授权的项目和资源

第三方工具许可证随各工具一同放在 `Tools` 目录。

## 开发

需要 Windows 10/11 x64 和 .NET 10 SDK。仓库已包含运行流程所需的 repak、UModel、Assimp、Python embeddable runtime 与 VC++ x64 Runtime。

```powershell
dotnet build .\PakAssetStudio.slnx -c Release
dotnet run --project .\PakAssetStudio.Tests\PakAssetStudio.Tests.csproj -c Release
```

可选的真实 PAK 扫描测试：

```powershell
dotnet run --project .\PakAssetStudio.Tests\PakAssetStudio.Tests.csproj -c Release -- C:\Path\To\Paks
```

发布 Windows x64 便携版本：

```powershell
dotnet publish .\PakAssetStudio\PakAssetStudio.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o .\artifacts\publish\win-x64
```

`bin/`、`obj/`、`publish/`、`artifacts/` 和软件运行输出均已加入 `.gitignore`。

## 仓库结构

```text
PakAssetStudio/        WPF 应用源码
PakAssetStudio.Tests/  可独立运行的轻量测试与可选 PAK 集成测试
tools/                 随程序发布的第三方工具、运行时及许可证
AI_HANDOFF.md          面向后续 AI/开发者的维护交接说明
```

## 版本记录

### 0.2.0

- 引入 MinVer：版本号由 git tag 自动生成，不再手写
- 标题栏显示当前版本号（预发布版本附带短提交号）

### 0.1.1

- 日志改为批量节流刷新，避免 UModel 大量输出堵塞 UI 线程
- 完成阶段的目录复制和文件统计移到后台线程
- 界面日志限制显示容量，完整日志仍写入 `PakAssetStudio.log`
