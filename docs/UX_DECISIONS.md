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

### 4. 设置首先服务任务，高级协议字段后置

主窗口采用控制中心信息架构，侧栏固定七区：**快速翻译 / 资料库 / 通用 / 快捷键 / 服务 / OCR 与隐私 / 数据与高级**。历史与生词是"资料"而不是"设置"，归入资料库统一组织；所有出网权限（安全离线、网络开关、免费引擎同意、截图上传授权）集中到「OCR 与隐私」一页，避免同一策略在多处重复解释。状态和错误使用一句可执行说明，不展示工程堆栈。

### 5. 服务页是"列表 + 向导"，不是巨大表单

服务页首页是服务列表（名称、协议、模型、本地/在线、默认徽章），编辑走表单，新增走三步向导：**选择服务类型 → 凭据与模型 → 测试并保存**。向导第 1 步只显示预设与协议；高级字段（Base URL、Endpoint、Header、TLS）从第 2 步起折叠在"高级"区，普通厂商配置不需要展开。测试连接使用内存草稿，不保存；新服务保存后不静默成为默认，需显式「设为默认」。首次启用 Profile 体系时，现有配置自动种子化为默认服务，用户已配好的模型不会被工厂预设覆盖。

### 6. 视觉风格是 Windows 原生的克制层级

使用 Segoe UI Variable / Microsoft YaHei UI、一个青绿色强调色、中性深浅主题、12–14 px 表面圆角和轻阴影。没有大面积渐变、玻璃噪声、发光边框或装饰性图标墙。浮窗使用短暂 150 ms 淡入，不给截图与翻译增加等待动画。

## 明确限制

- `SendInput(Ctrl+C)` 受 Windows 权限隔离影响；普通进程不能读取更高权限窗口中的选择。
- 密码框、禁止复制的应用、自绘画布和部分终端可能没有可复制文本。
- UIA TextPattern、浏览器可访问性树和屏幕 OCR 各有覆盖盲区，“全局 hover 取词”不能诚实地宣称 100% 通用。
- 视觉截图与 Windows 本地 OCR 均已可执行；系统未安装 OCR 语言包时，强制 Local OCR 会给出可操作的安装指引而不是空结果。
