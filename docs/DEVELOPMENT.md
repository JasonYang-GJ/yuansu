# 元枢开发指南

本指南用于构建和研究当前 0.8.0 合并候选源码，不代表已完成正式发布。首次使用先阅读 [README](../README.md) 的功能与数据边界。完整产品包含内置灵动岛，统一构建入口是 [build-yuanshu.ps1](../scripts/build-yuanshu.ps1)，所需 Node.js、Rust、WebView2 和安装编译器见 [统一构建指南](UNIFIED_PRODUCT_BUILD.md)；下方 C# 命令只用于主程序开发。

## 环境和启动

1. 使用 Windows x64 和可交互桌面。当前目标框架为 `net10.0-windows10.0.19041.0`；编译目标不能代替在具体 Windows 版本上的实机验收。
2. 安装 [global.json](../global.json) 指定的 .NET SDK。首次还原需要访问 NuGet 或使用已有完整离线缓存。不要随意更新锁文件来掩盖依赖缺失。
3. 按 README 还原、Release 构建，再为开发版设置独立的数据目录和通信名称，避免误连正在运行的旧版后台。
4. 在设置中配置 DeepSeek 或千问。服务账号、有效密钥、联网能力及服务费用由使用者自行准备。模型配置由本仓代码定义，不保证服务方永久提供相同模型。
5. 编程功能另需本机 Codex。必要时通过 `SCREEN_GUIDE_CODEX_PATH` 指定其可执行入口；聊天模型设置不会自动配置编程环境。

仓库根目录的 `.env.example` 是早期占位示例，不是当前桌面程序的密钥配置入口。不要在其中填写真实密钥。

完成 README 的构建后，在仓库根目录使用以下 PowerShell 示例。E 盘路径仅为示例，请替换为自己存在且可写的位置：

```powershell
$env:SCREEN_GUIDE_DATA_DIRECTORY = 'E:\YuanshuData\development'
$env:SCREEN_GUIDE_VOICE_MODEL_DIRECTORY = 'E:\YuanshuData\voice-models'
$env:TEMP = 'E:\YuanshuData\temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
$env:SCREEN_GUIDE_PIPE_NAME = 'ScreenGuide.Yuanshu.Development'
$env:SCREEN_GUIDE_DESKTOP_HOST_PATH = (Resolve-Path '.\src\ScreenGuide.DesktopHost\bin\Release\net10.0-windows10.0.19041.0\ScreenGuide.DesktopHost.exe').Path
Start-Process '.\src\ScreenGuide.DesktopClient\bin\Release\net10.0-windows10.0.19041.0\ScreenGuide.DesktopClient.exe'
```

这些变量只对当前终端及其子进程生效；若要求还原和构建也使用指定临时目录，应在构建前设置 `TEMP` 和 `TMP`。使用独立数据目录不会自动复制旧会话或密钥。

## 数据目录

| 环境变量 | 用途 |
| --- | --- |
| `SCREEN_GUIDE_DATA_DIRECTORY` | 会话数据库、任务、日志、设置和加密凭据根目录 |
| `SCREEN_GUIDE_VOICE_MODEL_DIRECTORY` | 本机语音模型根目录 |
| `SCREEN_GUIDE_CODEX_PATH` | 可选：本机 Codex 可执行入口 |
| `SCREEN_GUIDE_DESKTOP_HOST_PATH` | 可选：与 Client 配对的后台程序 |
| `SCREEN_GUIDE_PIPE_NAME` | 开发实例的本机通信名称；Client 与 Host 必须相同 |
| `TEMP`、`TMP` | 当前命令及子进程使用的临时目录 |

默认运行数据在 `%LOCALAPPDATA%\ScreenGuide\V01`，默认语音模型在 `%LOCALAPPDATA%\ScreenGuideTeacher\models`。需要放 E 盘时，在启动前设置路径。不要直接用不兼容的旧版本打开新数据库。

当前 SQLite schema 为 v11、桌面通信协议为 v12。历史回滚必须保留新数据库并使用对应备份或独立数据目录，不能依靠删除当前数据实现。

## 离线语音模型

仓库不包含语音模型，也不会因源码公开而自动获准捆绑分发模型。自行取得并核对适用来源、许可和模型兼容性后，当前代码期望以下结构：

```text
<SCREEN_GUIDE_VOICE_MODEL_DIRECTORY>/
└── sherpa-onnx-streaming-zipformer-zh-int8-2025-06-30/
    ├── encoder.int8.onnx
    ├── decoder.onnx
    ├── joiner.int8.onnx
    └── tokens.txt
```

缺少模型时离线语音不可用。先用文字入口检查界面和聊天配置；不要把模型缺失误判为整个项目无法构建。普通启动保持语音暂停；模型就绪后，用户点击可见的“开始语音”才开启监听，暂停语音或退出程序可停止。

## 测试：先检查受影响范围

日常修改先运行相关测试，例如修改普通聊天公共逻辑时：

```powershell
dotnet test tests/ScreenGuide.AI.Core.Tests -c Release --no-restore
```

在准备好的交互式 Windows 环境中，才运行涉及桌面窗口、语音或完整历史回滚的检查。为避免非语音测试读取本机真实语音模型，测试时可将模型目录设置为独立空目录：

```powershell
$env:SCREEN_GUIDE_DATA_DIRECTORY = 'E:\YuanshuData\test-fallback'
$env:SCREEN_GUIDE_VOICE_MODEL_DIRECTORY = 'E:\YuanshuData\empty-test-models'
$env:TEMP = 'E:\YuanshuData\temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
dotnet test ScreenGuide.slnx -c Release --no-build --no-restore --maxcpucount:1
```

运行前应已按 README 构建整个 Release 解决方案。`--maxcpucount:1` 限制构建并行度，不保证所有测试集合内部串行。真实桌面操作测试不要和其他界面自动化或人工操作同时进行。

部分回滚测试会在临时目录导出历史标签源码，要求完整 Git 历史、原有标签和已缓存的精确依赖。ZIP 下载或浅克隆可用于阅读源码，不能替代这些验收前提。历史回滚脚本可能按祖先目录查找 `.nuget/packages`；应准备正确缓存，不改写历史标签使测试通过。

`tools/` 中的真实模型、窗口、麦克风和安装生命周期 Runner 不是普通单元测试。运行前必须明确目标、隔离目录、实际权限和可能发生的模型费用，不能批量无条件执行。

## 构建产物与发布

普通 `dotnet build` 生成开发产物。`scripts/build-desktop-release.ps1` 属于单独的候选分发流程，要求精确源码提交、父提交、版本和对应归属合同；直接无参数调用不能生成正式发布版。

本次公开不附带安装包或模型，不创建新的发布标签。冻结版本的 NOTICE、归属哈希和安装生命周期证据保持历史身份，不应套用到修改后的任意构建。

## 提交改动

- 明确一个改动解决哪个实际问题，并附对应的复现方式与验证结果。
- 保持单个写入者负责同一模块；需要额外审查时只提供必要范围。
- 不提交 `bin/`、`obj/`、运行数据库、密钥、私人截图、录音、真实会话或本机日志。
- 权限、捕获、目标身份、取消与恢复逻辑改动必须保留相应边界测试。
- 提交建议和使用源码前先阅读 [公开范围与权利说明](PUBLIC_SOURCE_STATUS.md)；公开仓库目前没有项目级开源许可或贡献者许可协议。
