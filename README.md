# PAK Asset Studio

PAK Asset Studio 是一个 Windows 桌面工具，用于扫描和解包传统 UE4 PAK，并批量导出模型、贴图和可选 FBX 副本。

## 功能

- 拖放或选择游戏目录，递归发现 `.pak`
- 使用 repak 判断 PAK 索引版本、压缩、索引加密和文件数
- 区分“索引可读”与“压缩方式受支持”，逐项记录跳过原因
- 基础包、Optional 包、补丁包按自然编号有序叠加解包
- UModel 批量导出 glTF、PNG/HDR（HDR 由 UModel 对浮点纹理自动产出）和材质描述
- 可选保守合并明确命名的模型分片；同时转 FBX 时自动使用独立副本，始终保留源 glTF
- Assimp 批量转换和重新验证二进制 FBX，覆盖与跳过语义明确
- 中文路径、取消、磁盘空间预检查、失败保留和持久日志
- 多语言界面（内置简体中文和 English，可向 `Languages/` 目录投放语言文件扩展）
- 不修改原游戏目录

## 使用

1. 运行 `PakAssetStudio.exe`。
2. 选择游戏根目录或直接拖入 Paks 目录。
3. 加密包先填写合法 AES 密钥；密钥变化会自动使旧扫描失效。
4. 点击“扫描 PAK”，确认解包候选状态和跳过原因（不支持的包默认隐藏，可用右上角开关查看全部）。
5. 只有 PAK 格式唯一对应一个 UE4 版本时才会自动填写 profile；显示版本范围时必须按目标游戏手动选择。
6. 选择一个空输出目录和处理步骤；默认建议位置在 `%LOCALAPPDATA%\PakAssetStudio`，电脑较卡时可开启“低占用模式”。
7. 点击“开始处理”。已有受管理输出必须勾选“覆盖”，旧结果会先移动到可恢复备份。

输出目录可能包含：

```text
CookedAssets/    解包后的 cooked 文件
ExportedAssets/  glTF、BIN、PNG、HDR 和材质描述
FbxAssets/       保留 glTF 时生成的 FBX 工作副本
PakAssetStudio.log
.pakassetstudio-output.json   输出目录所有权标记，请勿删除
```

为避免误删文件，程序不会接管非空且没有所有权标记的输出目录。选择典型 `GameName\Content\Paks` 时，整个 `GameName` 都视为受保护游戏目录。任务失败或取消时，新产生的部分结果和覆盖前备份都会保留。

## 运行要求

- Windows 10/11 x64
- 不需要安装 Unreal Editor
- 不需要安装 Python，发布包包含 Python embeddable runtime
- 如果 Assimp 报运行库缺失，安装随发布包提供的 VC++ x64 Runtime

## 限制

- 传统 `.pak` 使用 repak；`.utoc/.ucas` IoStore 暂不支持
- 随附 repak 明确支持 Zlib、Gzip、Zstd；其他压缩方式会在扫描时标记为不支持
- “索引可读且压缩受支持”只是解包候选；未加密索引中的数据加密问题可能要到实际解包时才能发现
- PAK 容器版本通常只能确定 UE 版本范围，UModel profile 必须与目标游戏实际版本匹配
- 保守合并仅识别 `_partNN`、`_pieceNN`、`_meshNN`、`_polySurfaceNN`，不能恢复蓝图或关卡中的部件关系与变换
- 当前一次任务只接受一个 AES 密钥；多 KeyGuid 游戏需要分批处理
- 解包步骤处理扫描到的全部候选 PAK，当前不提供逐包勾选
- 当前流程不导出动画或音频；Cooked 资源也不能还原为原始 C++、完整蓝图或可编辑关卡
- 仅处理自己拥有或已获授权的项目和资源

repak、UModel、Assimp、Python 的许可证随工具放在 `Tools`；SDL2、VC++ Runtime、.NET 与编译进程序本体的第三方声明见 `THIRD-PARTY-NOTICES`。

## 法律声明

- 本项目与 Epic Games 及任何游戏发行商均无关联，"Unreal" 等相关商标归其各自所有者。
- 本工具不内置、不分发任何游戏的加密密钥；加密 PAK 的 AES 密钥须由用户自行合法取得。
- 解包后的游戏资源版权归原权利方所有。用户须自行遵守目标游戏的最终用户许可协议（EULA）及所在地法律，仅处理自己拥有或已获授权的内容。
- 作者不对用户使用本工具的行为及其后果承担责任。

## 开发

需要 Windows 10/11 x64 和 .NET 10 SDK。仓库已包含运行流程所需的 repak、UModel、Assimp、Python embeddable runtime 与 VC++ x64 Runtime。

```powershell
dotnet build .\PakAssetStudio.slnx -c Release
dotnet test .\PakAssetStudio.slnx -c Release
.\tools\python\runtime\python.exe -m unittest discover -s .\tools\tests -v
```

用随仓库 Assimp DLL 执行真实 glTF -> FBX -> 重新导入验证：

```powershell
$env:ASSIMP_TEST_DLL = (Resolve-Path '.\tools\assimp\Release\assimp-vc143-mt.dll')
.\tools\python\runtime\python.exe -m unittest discover -s .\tools\tests -v
Remove-Item Env:ASSIMP_TEST_DLL
```

常规 .NET 测试会用随附 repak 动态生成 base/patch PAK fixture，验证扫描、排序、`-f` 覆盖与解包。可选的外部真实 PAK 扫描测试：

```powershell
$env:PAK_TEST_DIR = 'C:\Path\To\Paks'
# 仅加密 PAK：$env:PAK_TEST_AES_KEY = '0x...'
dotnet test .\PakAssetStudio.slnx -c Release
```

发布 Windows x64 便携版本（运行测试、生成第三方版本/哈希清单、ZIP 和 SHA-256）：

```powershell
.\scripts\Publish.ps1 -Version 0.5.0
```

仅调试 publish 时仍可直接运行 `dotnet publish`；正式发布应使用脚本或 tag 触发 GitHub Actions。

`bin/`、`obj/`、`publish/`、`artifacts/` 和软件运行输出均已加入 `.gitignore`。

## 仓库结构

```text
PakAssetStudio/        WPF 应用源码
PakAssetStudio.Tests/  xUnit 单元、合成 PAK 集成与可选真实 PAK 测试
tools/                 随程序发布的第三方工具、运行时及许可证
AI_HANDOFF.md          面向后续 AI/开发者的维护交接说明
```

## 版本记录

### 未发布

- 修复重复运行沿用旧 FBX、AES 变化不重扫、补丁覆盖受 UI 开关影响等正确性问题
- 补丁解包顺序按 chunk 分组并自然排序，无编号补丁统一排最后
- 模型合并改为默认关闭、保守显式分组、保留源文件和原子写入
- 增加输出目录所有权标记、独占任务锁、覆盖前备份、流式持久日志和目录链接安全检查
- PAK 扫描区分索引可读与压缩可解包；模糊 UE 版本不再自动猜测 profile
- 测试迁移到 xUnit，增加合成 PAK、真实 Assimp fixture、Python 脚本测试、Windows CI 和发布布局校验

### 0.4.0

- 新增"任务模式"预设下拉：完整导出（推荐）/ 仅解包 PAK / 仅导出资源（不转 FBX）/ 自定义，一键批量配置各步骤开关
- 手动改动任一步骤开关时，任务模式自动切换为"自定义"
- 处理步骤面板按"执行步骤"与"输出与清理"分组，降低开关堆叠的理解成本

### 0.3.0

- 新增"合并分片模型"开关（默认开）：UModel 导出的分片 glTF 按目录合并为单模型（仅 LOD0）
- 新增"完成后删除 CookedAssets"开关（默认开）：导出成功后清理解包中间产物
- "保留原 glTF" 默认改为不勾选

### 0.2.0

- 引入 MinVer：版本号由 git tag 自动生成，不再手写
- 标题栏显示当前版本号（预发布版本附带短提交号）

### 0.1.1

- 日志改为批量节流刷新，避免 UModel 大量输出堵塞 UI 线程
- 完成阶段的目录复制和文件统计移到后台线程
- 界面日志限制显示容量，完整日志仍写入 `PakAssetStudio.log`
