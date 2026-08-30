# PopGlot 核心交互 UX 决策

## 调研边界

本轮只参考公开产品行为、官方 Windows 指南和合法开源项目的架构说明，不复制品牌视觉、布局资产或源代码。

- [DeepL Windows 快捷翻译](https://support.deepl.com/hc/en-us/articles/360020613059-Use-DeepL-shortcuts-for-desktop-apps)：选中文字、主动快捷键、小窗就地显示；普通复制不会自动上传。
- [PowerToys Text Extractor](https://learn.microsoft.com/en-us/windows/powertoys/text-extractor)：全屏遮罩、拖选、Esc 取消、多屏入口和 OCR 结果需要校对。
- [Microsoft UI Automation TextPattern](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-textpattern-overview)：UIA 取词依赖目标控件实现对应模式，不能承诺覆盖全部桌面应用。
- [Windows 几何与排版](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry)、[层级](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/layering)：使用系统字体、稳定间距、少量圆角和两层表面；强调色只用于状态与主操作。
- [PowerToys 开源仓库](https://github.com/microsoft/PowerToys) 采用 MIT 许可证。本项目只借鉴“每屏遮罩、快捷键触发、OCR 适配层”的公开思路，没有复制其实现。

## 已落地决策

### 1. 划词采用独立快捷键，不监听普通复制

默认 `Ctrl+Alt+W`。这样只有明确意图才读取并可能发送文本，避免 `Ctrl+C+C` 状态机误触和普通复制隐私风险。事务模拟一次 `Ctrl+C`，在 450 ms 内读取 Unicode 文本并恢复原剪贴板；若用户随后复制了新内容，则不回写旧快照。

双击 Ctrl/C 或 UIA hover 以后可以作为关闭默认的增强项，不能替代确定路径。

### 2. 两种来源只使用一个翻译浮窗

划词与截图共享读取/翻译/完成/失败/取消状态、原文与译文层级、术语保护、复制、重试、固定和关闭。划词先使用前台控件的 Win32 caret 位置，取不到时回退鼠标位置；浮窗优先放在锚点右侧，其次下/左/上，最后夹紧到当前显示器工作区。

### 3. 截图是“先框选，再决定路线”

遮罩初始只显示一条短说明并绘制十字准星；拖动后选区恢复清晰、外围保持变暗，四角出现控制点，尺寸徽标按物理像素显示并贴近选区避开底边。右键或 Esc 取消。截图只在内存编码，且等遮罩真正离开合成器后才抓取，而不是等待一个固定毫秒数。视觉上传必须有明确许可；本地 OCR 未安装时显示真实限制，不以视觉上传冒充本地处理。

### 4. 主窗口工作台与独立设置窗口解耦，提供同级快捷入口

主窗口收敛为专注日常使用的生产力工作台，侧栏精简为两大核心视图：**翻译**（双栏输入与对照）与**资料库**（生词本与历史记录的 Master–Detail）。主窗口侧栏底部新增同级「设置」入口，便于快速调出设置窗口。
全部配置解耦至独立的 **SettingsWindow**（设置窗口），划分为 **通用 / 服务 / 快捷键 / 隐私与数据** 四个专区。设置窗口打开时默认直达「服务（翻译引擎）」专区；设置页采用分组标题 + 扁平设置行 + 发丝分隔线，去除多余嵌套卡片；日常修改即时保存或在窗口内草稿解决，日常流程零系统 MessageBox。

### 5. 服务页采用 Master–Detail 架构，草稿内联守卫与纯值比对

服务页采用清晰的 Master–Detail 布局：左侧为已配置服务列表（展示服务名称、模型与实时健康状态机），右侧为当前选定服务的完整配置编辑器（Base URL、Endpoint、Model、API Key 与高级 Header）。出厂 ProviderCatalog 模板仅在「添加服务」流程展示，全新安装时不占用已配置列表。
编辑区引入内联草稿守卫条（DraftGuardBar）与两步确认组件（ConfirmButton），切换未保存修改时在界面内拦截引导；Dirty 状态采用规范化纯值比较，改回原值自动恢复 Clean，测试连接使用独立内存草稿，不篡改已存凭据；保存与设为默认解耦。健康检查仅作为参考指标，不强制作为保存和路由的阻断门控。

### 6. 视觉风格与对比度审计（WCAG 2.1 AA）

- 强调色升级为冷靛蓝（Light `#5B5BD6` / Dark `#8B8FF7`），状态色语义化（成功绿、警告琥珀、危险红）。
- 几何与圆角 Token 化：交互控件 6 px、内容容器/面板 10 px、浮窗与主窗口 12 px。
- 文本渲染保真：TranslationPanel 与 QuickSearch 等文本浮窗采用不透明表面由 DWM 接管圆角与阴影，确保 Windows 原生 ClearType 次像素文字清晰锐利；移除导致文字模糊的整页切换过渡动画。
- **高对比度加固**：深浅色主题下的 `TextTertiary`、发丝边框与占位文本全面通过 `ThemeContrast` 的 WCAG 2.1 AA 级对比度审计；输入框聚焦时保持占位符可见。

### 7. Stream-Final 双层渲染与 UI Final Gate 动作防护

- **毫秒级首 Token 响应与平滑流式体验**：收到首个 delta 时，界面即时切换至轻量只读文本流式层，支持长文本自动跟随滚动；流式结束后无缝平滑切至 Rich Markdown 终态层。
- **视觉平滑无跳动**：增量流式层与终态 Markdown 层严格对齐字号（15px）与行高（22px），彻底解决流式结束瞬间文字缩水或跳动的视觉瑕疵。
- **UI Final Gate 门禁**：在流式增量阶段以及 partial 状态下，严格禁用自动复制、TTS 朗读与本地历史写入；所有动作必须且仅在收到完整合法终态 Envelope 时单次触发。

### 8. 模型推荐偏好与透明度原则

- 提供 `Speed`（极速）、`Balanced`（均衡）、`Quality`（高质）推荐偏好，辅助用户快速选择合适的大模型。
- 推荐基于 Provider 目录事实、模型家族启发式规则与本地基准测试；无确凿证据的模型明确显示为未知（Unknown），严守“未知模型不虚构能力”的产品契约。

## 明确限制

- `SendInput(Ctrl+C)` 受 Windows 权限隔离影响；普通进程不能读取更高权限窗口中的选择。
- 密码框、禁止复制的应用、自绘画布和部分终端可能没有可复制文本。
- UIA TextPattern、浏览器可访问性树和屏幕 OCR 各有覆盖盲区，“全局 hover 取词”不能诚实地宣称 100% 通用。
- 视觉截图与 Windows 本地 OCR 均已可执行；系统未安装 OCR 语言包时，强制 Local OCR 会给出可操作的安装指引而不是空结果。
