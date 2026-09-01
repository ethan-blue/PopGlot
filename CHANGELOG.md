# 更新日志

本文件从 **0.0.1** 开始维护。更早的开发过程没有正式版本号；
曾被误打的 `v0.3.0` tag 已撤回（见 `docs/VERSIONING.md` 历史修正），编号不复用。

## 0.1.2 - 2026-09-01

本版本是 0.1.1 的稳定性修复版，重点解决其他电脑上划词/截图快捷键触发后 UI 假死、
快捷键录制后恢复失败不可见，以及便携包依赖目标机 .NET 10 Desktop Runtime 的问题。
详见 `docs/RELEASE_NOTES_0.1.2.md`。

### 变更

- Windows x64 便携包改为 self-contained 发布，自带 .NET 10 和 WPF 运行时。
- 设置策略与 Shell 配置写盘移到后台队列，避免慢磁盘或杀软扫描卡住设置窗口。

### 修复

- 剪贴板 OLE 访问移入独立 STA 工作线程，并增加超时、单工作者熔断与 fail-closed 快照保护；
  Office、RDP 或剪贴板增强工具的延迟渲染不再阻塞 UI，读取不完整时不发送合成 Ctrl+C。
- 截屏拷贝与 PNG 编码移出 UI 线程，改善 4K、多屏和远程桌面环境的响应。
- 快捷键批量注册失败时原子回滚；录制暂停后恢复冲突会明确提示，并保留原配置以便冲突消失后重试。
- 增加剪贴板工作者隔离、快照失败关闭、快捷键真实冲突/恢复与 self-contained 发布约束测试；
  Windows 逻辑测试增至 120 项。

### 迁移与风险

- 配置 Schema 不变，无需迁移。
- self-contained 压缩包体积将明显大于旧版框架依赖包，以换取开箱即用和一致运行时。

## 0.1.1 - 2026-08-31

修复 0.1.0 的关键缺陷并打磨界面：流式末帧丢字、视觉请求误报失败、快速切换菜单崩溃、
退出路径卡死、UI 线程同步写盘卡顿；新增右下角引擎快速切换器（含可选内置免费引擎）与
「视觉识别 + 文本模型」两段式截图管线；主题换 azure 蓝系并统一卡片/按钮/开关样式。
详见 `docs/RELEASE_NOTES_0.1.1.md`。

## 0.1.0 - 2026-08-31

本版本引入全新的低延迟流式输出架构（SSE + Text-first 协议 + 随机 Trailer 分隔符 + C# 流式缓冲协调器）、智能模型推荐偏好体系、离线与在线基准评测工具，并全面优化设置交互与主题对比度。

### 新增

- **流式传输与解析架构**：
  - Core 层引入轻量高效的 SSE（Server-Sent Events）增量解析器（`SseDecoder`），支持增量切分、跨 chunk 边界 UTF-8 字节拼接与严格行/事件大小防御。
  - 统一适配四大 Provider（OpenAI-compatible、OpenAI Responses、Anthropic Messages、Gemini GenerateContent）原生流式协议与事件解析。
  - 设计 **Text-first + 随机 Trailer 分隔符** 协议：模型首批正文到达后立即增量显示，元数据（保留术语、解释、建议）随防冲突随机 Trailer 在流尾结构化解析，未收齐或格式异常时优雅降级保全正文。
  - `popglot-ffi` 导出 `popglot_translate_text_stream_v1`、`popglot_translate_text_draft_stream_v1` 与 `popglot_translate_vision_draft_stream_v1` 流式 C ABI，支持基于回调的原生 delta 传输与非零返回值主动 abort。
  - C# 侧实现 `TranslationStreamBuffer`（O(1) 线程安全短锁缓冲、无 UI 阻塞、防抖 pump、硬上限截断防御）与 `TranslationCoordinator`（40ms 增量抽取与状态管理，支持 Connecting / Streaming / Finalizing / Completed / Failed / Cancelled 完整生命周期）。
  - 三大交互入口（TranslateSection、TranslationPanelWindow、QuickSearchWindow）实现 **Stream-Final 双层渲染**：首 delta 触发轻量纯文本增量渲染，终态平滑过渡至 Rich Markdown 渲染，字号与行高统一平滑不缩水。
  - 引入 **UI Final Gate** 动作门禁：流式阶段及 partial/错误状态下严格禁用复制、自动复制、生词收藏、TTS 朗读与本地历史写入，仅在收到合法完整终态 Envelope 时执行。
- **模型推荐与偏好体系**：
  - `ModelRecommendationService` 新增 `Speed`（极速）、`Balanced`（均衡）、`Quality`（高质）偏好策略与 `ModelTier` 分级体系。
  - 依据 Provider 目录事实、模型家族启发式规则（Heuristics）与本地实测基准进行综合推荐；恪守“未知模型不虚构能力（FallbackUnknown）”契约，健康状态不作为保存和使用的阻断门控。
- **翻译基准评测子系统**：
  - 新增离线基准测试工具 `stream_benchmark`（支持真实本地 loopback / 内存 mock、网络抖动模拟、UTF-8 切分边界压力测试、TTFT 与吞吐量测量）。
  - 新增在线基准测试工具 `live_provider_bench`，具备严格的隐私与安全防护（默认离线 Dry-Run 退出 exit code 2，要求 `--live` 与 `--i-understand-cost` 双重显式确认，仅支持环境变量注入 API Key，输出内容严格脱敏）。
- **主题与对比度加固**：
  - 新增 `ThemeContrast` 工具类，对浅色与深色主题下的 `TextTertiary`、发丝边框与占位文本进行 WCAG 2.1 AA 级对比度审计与强化。

### 变更

- 主窗口侧栏底部新增同级「设置」入口，便于快速访问系统偏好。
- 设置窗口默认直达「翻译引擎」核心服务配置。
- 设置页面 Dirty 状态采用规范化 Snapshot vs Baseline 纯值比对，改回原值自动恢复 Clean，初始化与网络测试不置脏。

### 修复

- **设置保存状态与加载体验**：修复设置保存失败时卡在 Loading 状态的问题，保存失败后根据当前 Snapshot 准确恢复 Dirty/Clean 状态。
- **输入框占位符渲染**：修复空输入框聚焦时占位符（Placeholder）意外消失的问题。
- **截图流式与降级回退**：修复截图翻译在 OCR 后无法根据配置模型进行文本流式翻译的问题，以及视觉直译在零 delta 失败时的安全回退逻辑。
- **测试套件扩充**：扩充 Windows 逻辑测试套件至 113 项全量测试，覆盖流式生命周期、Fencing 隔离、主题对比度与推荐服务。

### 迁移与风险

- 本版本配置 Schema 保持为 v6（`product-config.json` v6，`provider-settings.json` v3），无需执行任何配置迁移，新旧版本配置完全无缝兼容。

## 0.0.2 - 2026-08-30

本版本聚焦稳定性强化、离线与隐私门禁收紧、原子存储与发布质量门禁。

### 新增

- Release 发布工作流（`.github/workflows/release.yml`）新增 Release Tag、`PopGlot.Windows.csproj`、`Cargo.toml` 与 `CHANGELOG.md` 四方版本一致性门禁检查，并在打包时自动生成 `.zip.sha256` 校验和文件。
- 便携版（Portable）分发包依赖 .NET 10 Desktop Runtime（x64），框架依赖产物更轻量且共享系统运行时补丁。

### 变更

- 配置文件 Schema 升级至 v6：由 `TextModel` 与 `VisionModel` 字段显式决定文本与视觉路由支持能力，自动同步 `IsLocal` 与角色推导，避免配置有效视觉模型时因历史状态位导致不可用；旧版本 v5 自动无损升级为 v6。

### 修复

- **TTS 离线门禁**：`TtsService` 增加离线策略检查，在网络未启用或安全离线模式下强制走 Windows 本地语音合成（SAPI / OneCore），杜绝云端 TTS 意外出网。
- **词库原子存储与损坏保全**：`VocabularyStore` 改用临时文件写入加原子替换机制，避免进程崩溃导致词库写损；遇到损坏 JSON 文件自动复制为 `<path>.corrupt-<timestamp>` 备份，防止异常时静默清空用户生词。
- **服务删除与运行时同步**：修复服务删除逻辑，删除时先保存配置，再尽力（best-effort）清理对应凭据（保存失败时凭据保持），并在删除默认文字服务时通过 `ApplyToCore` 同步更新或清空底层运行配置。
- **文本路由与凭据快照一致性**：统一草稿态与运行时解析，确保服务编辑与路由决策中的 Base URL、模型及 `CredentialTarget` 快照严格同步。
- **模型目录 TLS 与请求头规范**：`ModelCatalogService` 接入统一安全 TLS 策略，过滤保留敏感请求头，按 Provider 规范正确构造鉴权与请求头，避免跨协议拉取模型异常。
- **健康探测 Single-flight 去重**：服务连接测试增加单飞（single-flight）并发控制，避免快速切换或重复点击时发起重叠网络探测。
- **设置窗口主题事件退订**：`SettingsWindow` 关闭时显式退订 `ThemeService.ThemeChanged` 事件，消除长期运行下的主题事件监听泄漏。
- **FFI 取消与释放 Panic 防护**：`popglot-ffi` 对 `popglot_cancel_request`、`popglot_free_string` 等导出函数包裹 `catch_unwind` 并做空指针防护，阻止 Rust 侧 panic 穿透 FFI 边界导致前端崩溃。
- **测试套件稳定性**：修复逻辑测试中异步等待轮询形参副本的问题，完善 Windows 逻辑测试套件覆盖。

### 迁移与风险

- product-config.json v5→v6 自动迁移，已有服务、模型配置与凭据均完整保留。

## 0.0.1 - 2026-08-29

首个正式版本基线：四轮 UI/产品重构 + P0 缺陷修复的全部累积改动。

### 新增

- 独立设置窗口（通用 / 服务 / 快捷键 / 隐私与数据），主窗口只保留「翻译」「资料库」工作台。
- 服务页 Master–Detail：左侧已配置服务列表（名称/模型/健康状态三层排版），右侧编辑器；
  正文滚动、草稿守卫条与操作栏固定。
- ProviderCatalog 服务模板与已配置服务分离：全新安装的配置服务列表为空，
  出厂模板只出现在「添加服务」流程；schema v4→v5 自动迁移
  （仅剔除从未使用过的出厂模板，改名/改模型/有 Key 的服务全部保留，迁移前自动备份 .bak）。
- 服务健康状态机：未测试 / 可用 / 鉴权失败 / 限流 / 接口不存在 / 服务不可达 / 本地不可达 / 缺少 Key，
  会话内测试结果不伪装为永久健康。
- 就绪门控（`CheckReadiness`）：缺少 Key、缺少文字模型、缺少 Base URL 的服务不能设为默认；
  默认文字/视觉下拉中以禁用项 + 原因展示。
- 保存语义拆分：首个服务「保存并使用」，后续「保存服务」，编辑「保存修改」；
  独立「设为文字默认」，保存不再擅自切换实际路由。
- 内联草稿守卫条（DraftGuardBar）：切换服务/设置页/新增/关闭窗口前的未保存修改
  以「保存并继续 / 放弃并继续 / 取消」在窗口内解决，日常流程零系统弹窗。
- 两步确认组件（ConfirmButton）：删除服务、清除密钥、清空历史、清空生词本；
  只有启动失败保留系统 MessageBox。
- 模型列表真实拉取（ModelCatalogService）：从服务商 /models 接口读取草稿可选模型，不落盘。
- 应用头像：多尺寸 ICO（16–256px）接入 exe/窗口/托盘/侧栏；新增 `scripts/make-ico.ps1`。
- 版本规则文档（`docs/VERSIONING.md`）与本更新日志。

### 变更

- 色彩体系重建：品牌强调色改为冷靛蓝（浅 `#5B5BD6` / 深 `#8B8FF7`），成功/警告/危险独立色相；
  浅色四层、深色五层背景亮度层级肉眼可辨；Surface 角色语义化（Muted/Raised/Disabled）。
- Typography Token 化（20/13/12/11/12.5），消除散落字号；控件高度与圆角统一（控件 6、内容面 10、浮窗 12）。
- 主窗口侧栏收敛（168px、去占位 Logo、中性选中态+短指示条），底部状态栏降级为 Metadata 层级。
- 翻译工作台：原文=编辑面（Input），译文=只读（SurfaceMuted），次级操作图标化。
- 设置页去卡片化：分组标题 + 扁平设置行 + 发丝分隔线；「当前实际线路」保留警示卡形态。
- TranslationPanel 与 QuickSearch 改为不透明窗口（DWM 圆角/阴影接管），正文获得 ClearType。
- 翻译请求输出 token 上限按源文本长度收紧（降低网关排队延迟）；Gemini 3 系模型关闭思考模式。

### 修复

- 标题栏最小化/最大化/关闭图标未渲染（模板未读取 Ui.Icon 附加属性、CloseBtn 缺几何）；
  模板改显式 Stroke 线稿渲染，关闭悬停红底白叉；主窗口 Tooltip 改「关闭到托盘」并首次提示。
- 页面切换与浮窗加载的整页动画导致文字先模糊后清晰（ClearType 丢失）——已全部移除。
- 保存服务时 API Key 写入错误凭据目标（新增 DeepSeek/Gemini/Claude 的 Key 曾落入 OpenAI 默认槽）。
- 设置保存非原子（Core 策略在快捷键校验前落盘）——重排为校验→注册→提交→回滚。
- `ProfileManager.Save` 在文件写入成功前更新内存缓存——改为替换成功后再提交缓存。
- 状态栏与隐私路线预览使用默认凭据目标而非实际激活 Profile 的目标。
- 凭据写入成功但 Profile 落盘失败时回滚凭据；`ApplyToCore` 失败时明确报告「已保存，重启后生效」。
- Profile 列表刷新重复读取配置；整页外层 ScrollViewer 破坏列表虚拟化。
- 本地数据页布局错误（清空按钮被拉伸）、详情区固定 MaxHeight 限制等布局缺陷。

### 移除

- 主窗口「控制中心」、全局保存栏、彩色字母 P 占位 Logo、宣传式副标题、
  日常流程的系统 MessageBox、Chip 网格预设按钮、运行时免费引擎授权弹窗。

### 迁移与风险

- product-config.json v4→v5 自动迁移；异常时保守保留条目。
- 免费引擎首次使用从「弹窗询问」改为「失败 + 指引到隐私设置」，需用户主动授权一次。
- 会话级测试状态不持久化；「本地不可达」需主动测试连接才能发现。

（0.0.1 之前的开发史见 `docs/UI-REFACTOR-PLAN.md` 第一至十四节。）
