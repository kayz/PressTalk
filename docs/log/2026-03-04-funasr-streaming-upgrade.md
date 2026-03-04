# PressTalk 流式升级工作日志 (2026-03-04)

## 目标

将 PressTalk 从 Qwen 离线链路升级为 FunASR 流式链路，支持边说边写、Toggle 交互、历史记录、热词和可选说话人分离。

## Phase 完成情况

### Phase 1: FunASR 流式后端

- 新增 `src/PressTalk.Asr/funasr_runtime.py`
  - 支持 `start_streaming_session` / `push_audio_chunk` / `end_streaming_session`
  - 集成 FunASR 流式模型
  - 支持 CUDA 设备选择
  - 支持 int8 动态量化（CPU best-effort）
  - 集成 CampPlus 说话人分离（在会话结束阶段执行）
- 新增 `docs/funasr-streaming-protocol.md`
- 新增 `FunAsrRuntimeClient` + `FunAsrBackend`
- 新增流式合同 `IStreamingAsrBackend` / `StreamingAsrResult`

### Phase 2: 流式状态机和控制器

- `SessionState` 新增 `Streaming`
- `SessionTrigger` 新增 `StartStreaming` / `StreamingChunk` / `StopStreaming`
- `SessionStateMachine` 增加流式状态迁移
- 新增 `StreamingController`
  - 每 300ms 推送新增音频块
  - 回调实时流式结果
  - 支持开始/停止会话
- 新增 `IStreamingPipeline` 与 `StreamingPipeline`

### Phase 3: 实时文本提交

- 扩展 `ITextCommitter`
  - `CommitIncrementalAsync`
  - `ResetIncrementalState`
- 更新 `SendInputTextCommitter` 和 `ClipboardPasteTextCommitter`
  - 内置增量去重
  - 避免重复写入
- 当前默认采用 `ClipboardPasteTextCommitter`，实现输入框提交 + 剪贴板同步更新

### Phase 4: UI 交互改造

- `HoldSignal.cs` 替换为 `ToggleSignal.cs`
- `Program.cs` 主链路改为 Toggle 开始/停止
- 热键单击触发 Toggle；悬浮窗左键也触发 Toggle
- 悬浮窗右键菜单新增设置入口
- `FloatingRecorderForm` 增加
  - 录音波形动画
  - 实时文本预览区域
  - 说话人分段彩色展示

### Phase 5: 历史记录保持

- `HistoryRecord` 结构保持文本字段不变（不记录音频）
- 新增 `JsonHistoryStore`（jsonl 持久化）
- 流式会话结束后记录最终文本

### Phase 6: 专业词库支持

- 新增 `HotwordConfig`
- 设置页支持热词列表（每行一个词）
- 热词随会话传入 FunASR 运行时

### Phase 7: 声纹识别（可选）

- 新增 `SpeakerSegment` 合同
- `funasr_runtime.py` 集成 CampPlus 说话人分离（结束阶段）
- UI 支持按说话人颜色区分展示

## 验证结果

- `dotnet build PressTalk.sln -c Release` 通过
- `dotnet test tests/PressTalk.Engine.Tests/PressTalk.Engine.Tests.csproj -c Release` 通过 (8/8)

## 真人测试准备

### 前置环境

1. Python 环境安装 `funasr`, `torch`, `torchaudio`
2. 若启用 GPU，确保 CUDA 与 PyTorch 版本匹配
3. 首次运行模型下载约 1-2GB，需保证网络和磁盘空间

### 建议测试流程

1. 启动 PressTalk，确认悬浮窗显示“点击开始/停止”
2. 在设置页添加 3-5 个热词，保存
3. 打开任意文本输入框（如记事本）
4. 按热键一次开始录音，说一段 10-20 秒文本
5. 观察实时文本是否持续写入输入框，且剪贴板文本同步更新
6. 再按热键一次停止
7. 检查最终文本、历史记录文件和 UI 预览是否一致
8. 勾选说话人分离后复测双人对话样例，确认彩色分段显示

### 验收观察点

- Toggle 开始/停止行为是否稳定
- 边说边写是否连续无明显重复
- 最终文本是否进入历史记录
- 热词识别是否相较未配置时更稳定
- 说话人分离结果是否可读
