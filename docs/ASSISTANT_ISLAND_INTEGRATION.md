# 元枢与既有灵动岛整合批次

后续用户实机发现旧胶囊会挡住浏览器点击，现已追加 [鼠标穿透和焦点修正](ASSISTANT_ISLAND_INPUT_PASS_THROUGH.md)。下文 296×54 尺寸与旧候选哈希为整合首批历史；当前默认 224×28 穿透状态条，手动打开后才接受鼠标。

用户授权：沿用既有元枢灵动岛，顶部小胶囊常驻；说话或执行时展开，先显示语音转写，再执行并显示结果。保留原语音、模型配置、Session 和管理确认。本批不是重新设计一套 WPF 岛，也不新增第二个执行入口。

基线：Core `e5796b84469a6b9b051e457c6e94de1b0cbce213` 加已有助手开发草稿；Island `36df1f47287227e74b50f1335502b718850df9bb`，开始时 clean。继续使用当前工作区，保留历史预览、正式安装和原运行数据。当前用户正在运行上一候选；开发/测试先隔离，切换前正常退出准确实例。

## 结构与范围

- DesktopClient 仍负责本机识别、播报与既有输入取消；Host SessionCoordinator 仍是唯一会话和动作入口。
- 真正的 Tauri Island 接收 Client 的瞬时展示投影。原任务 Bridge 的安全摘要合同不塞入转写，不改 Host/数据库协议。
- `--island` 与 `SCREEN_GUIDE_ISLAND_PATH` 选择真实 Island 可执行程序。Client 启动其子进程，只有窗口真实显示且完成展示回执后才隐藏旧小球并开启获准的监听。Island 退出、断线或隐藏时停止麦克风，返回可见恢复入口。
- 语音转写仅存在内存和本机用户管道，不录音、不保存转写文件、不增加模型请求。操作确认仍在原管理窗口完成。
- 保留 Island 单独运行的媒体、音量与任务功能；整合模式不自动消失成小球、不按全屏策略隐藏麦克风提示，不抢前台焦点。

## 瞬时展示协议 v1

Client 新建随机名称 CurrentUserOnly Named Pipe，以 `YUANSHU_ASSISTANT_PIPE` 仅传给所启动 Island；用 Windows 管道对端进程 ID 绑定该子进程。每个连接一请求一响应，4 字节 little-endian 长度 + UTF-8 JSON，单帧最多 32768 字节，无网络、文件转写或日志正文。

请求均含 `protocolVersion: 1`，操作：

- `snapshot`：读取当前投影。
- `presented`：`instanceId`、`revision`、`visible`。前端在 DOM 更新后的帧中确认该版本；Rust 结合实际主窗口可见性生成 visible。每次轮询后均续报，供 Client 三秒展示存活检查。不得确认未来版本。
- `command`：`instanceId`、`controlToken`、`action`。仅允许 `start`、`pause`、`stop`、`management`、`memory`、`exit`。Client 验证当前 token 后调用既有处理；token 在麦克风/当前请求/替换代际变化时更新。不得代批文件、窗口、模型出站或任何业务确认。

响应统一为 `{ ok: boolean, error: string | null, snapshot: AssistantIslandSnapshot | null }`。稳定错误码，不含异常、路径或对话正文。snapshot 字段：

```text
protocolVersion: 1
instanceId: UUID
revision: nonnegative integer
controlToken: UUID
phase: idle|listening|transcribing|recognized|working|speaking|waiting|success|error|standby|paused|stopping
status: string (<=160 chars)
transcript: string (<=2000 chars)
detail: string (<=4000 chars)
microphoneOn: boolean
canStart: boolean
canPause: boolean
canStop: boolean
needsAttention: boolean
updatedAtUtc: ISO timestamp
```

前端约 200 ms 轮询，不重叠请求；断线清除转写，不自动重发控制命令。拒绝旧实例、旧版本和未知枚举。最终文本用 recognized 阶段发送，Client 在展示回执到达后才把该原始语音请求提交给 Host；回执失败/停止/替换均取消该次未提交请求，不悄悄执行。

## 界面与验收

继承 Island 的深蓝灰与青绿、顶部居中、不抢焦点窗口。空闲为小胶囊；实际转写、已识别、执行、回答、待确认展开；完成结果短暂保留后回小胶囊。不能把仅麦克风开启当作一直有人说话。转写和状态分别标识，可访问的暂停/停止/管理/记忆/退出入口可达。

检查：协议和对端/版本边界、断线停止、真实 React/Tauri 顶部窗口、模拟逐步转写先于单次 Host 提交、取消/旧回执拒绝、原确认可达、单独 Island 回归、新版入口和原配置保留。真人麦克风与播报听感仍遵循用户此前的稍后验收安排；未通过项目如实记录，不用模拟取代真人。

## 本批验证与交付身份

2026-10-11：Client 177 项、Island 前端 135 项、Rust 51 项通过；严格 Clippy 与生产编译通过。原小球真实 WPF 回归仍通过，覆盖替换/迟到回复/停止/管理/自然退出。

真实 Release Tauri 窗口与真实 Client/Host 联测通过，监听器、播报和模型为受控替身：暂停启动不开麦、顶部小胶囊、3.5 秒模拟加载不阻断显示、逐步转写先展示、最终精确版本回执先于唯一 Host 提交、完成 6.5 秒后收起、管理窗口返回、暂停/重启、隐藏时撤销收音、未展示指令不执行和可见恢复。测试自然退出并清理自己的临时目录。第一次测试因 WebView 按钮不提供旧 WPF 使用的 InvokePattern 中止；第二种键盘方式因焦点前提不成立中止；最后通过窗口内已识别控件的原生可访问性默认动作完成，未弱化前台/目标边界或移动鼠标。

浏览器仅使用外部假传输与模拟文字检查实际生产 React/CSS，检查 296×54 胶囊及 480×330 展开、长文字滚动、确认入口和固定底栏。未采集真实桌面、录音或调用真实模型。设计检测仅报告已有紧凑界面字级/近白色的建议项，视觉核查后保留，不构成阻断。

外部证据：`<private-evidence>/assistant-island-20261011`，包括 `assistant-island-client-final.trx`、`frontend-tests.json`、`native-tests-final.log`、`island-integration-4.log`、`orb-regression.log` 与 `visual-review.md`。最终 Island EXE SHA-256 为 `030B80C0C242ABE23916E70BD5B73F872F899FFEAB0B535DE538FC78ACB70D16`。

交付脚本 Stage 校验两仓工作区源码指纹、原生构建输入和每项产物，Activate 备份并更新准确的桌面“元枢”快捷方式。候选为 `assistant-island-20261011` / `0.7.0-assistant-island`，仍使用既有 `application-open-fix-20260922/runtime` 与语音模型目录，不复制或重置凭据。实际切换、入口运行及数据保留结果由候选 `activation.json` 和外部 `delivery-entry.json` / `runtime-after.json` 记录；这不是干净源码冻结版、干净机安装或真人语音验收。
