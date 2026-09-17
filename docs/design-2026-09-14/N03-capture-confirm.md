# N03 截图确认模式设计方案：截图微调、OCR校对与明确提交

> **设计日期**：2026-09-14
> **阶段/层级**：L1 截图调整与快速通道，L2 OCR 文本校对与明确上传
> **依据合同**：`docs/review-2026-09-12/PRODUCT-EXPANSION-REVIEW-V2.md` §5 N03；关联 C10（热键首帧性能）、C23（Provider 明确数据上传合规）
> **前置依赖**：现有 `CaptureOverlayWindow`、`ScreenCaptureService`、多屏 DPI 体系（`ScreenGeometry`）

---

## 1. 目标与非目标

### 1.1 目标
1. **双模式无缝共存**：
   - 默认保持既有**快速框选模式**（松开鼠标即截图并提交，零等待，适合熟练高频用户）；
   - 在设置中提供「截图确认与微调模式」开关（可选，或快捷键带修饰键即时进入）。
2. **真可微调选区**：
   - 确认模式下，鼠标释放后不立即关闭遮罩，而是进入选区确认态：显示 4 角 + 4 边拖拽手柄及确认/重选工具条；
   - 支持拖动手柄缩放、拖动内部平移选区、Enter 或双击确认、Esc 或右键取消、R 键重新框选；
   - 误点过滤：小于 6×6 物理像素的轻微误触回到初始等待状态，绝不意外闪退。
3. **冻结帧保障（Freeze-Frame Integrity）**：
   - 无论调整耗时多久，最终截取并识别的画面严格基于呼出截图时刻已捕获的冻结全屏图像，杜绝「用户确认 A、实际截取到随后弹出的 B」的视觉与隐私安全风险。
4. **OCR 校对与明确上传（L2 衔接）**：
   - 识别文字后，在进入网络翻译前提供「校对面板」：可编辑识别结果、显式告知「仅发送校对后的文字」或「将发送选区图片到视觉模型」，杜绝静默出网。

### 1.2 非目标
1. **复杂图像标注工具**：不实现箭头、画笔、马赛克等复杂标注功能；本模块纯粹聚焦于翻译取词的边界校准与 OCR 校对。
2. **外部 OCR 引擎静默替换**：本地 Windows Media OCR 不可用时给出标准系统指引与显式替换建议，绝不擅自未经用户同意切换为云端付费视觉 API。

---

## 2. 状态机设计与交互流

### 2.1 覆盖层状态枚举 `CaptureState`
```csharp
namespace PopGlot.Windows;

public enum CaptureState
{
    InitialCrosshair, // 初始全屏十字准星，等待按下左键
    DraggingSelection,// 正在按住鼠标拉框
    AdjustingConfirm, // 已框选完毕，停留在微调与操作栏激活态
    Committing,       // 用户确认，正在切图并派发事件
    Dismissed         // 已取消退出
}

public enum ResizeHandleType
{
    None,
    MoveSelection,
    TopLeft, Top, TopRight,
    Right,
    BottomRight, Bottom, BottomLeft
}
```

### 2.2 状态流转图
```text
           [按下截图快捷键]
                  │
                  ▼
         InitialCrosshair ◄────────────┐ (误触 <6px 或按 R 重新框选)
                  │                    │
          (MouseDown + Drag)           │
                  │                    │
                  ▼                    │
          DraggingSelection            │
                  │                    │
             (MouseUp)                 │
                  │                    │
                  ├────[快速模式]───────┼───────► Committing ──► 派发 Captured ──► Close
                  │                    │
                  ▼ [开启确认模式]      │
           AdjustingConfirm ───────────┘
                  │
                  ├────[Esc / 右键点击 / 点击取消]──► Dismissed ──► Close
                  │
                  └────[Enter / 双击选区 / 点击确认]─► Committing ──► 派发 Captured ──► Close
```

---

## 3. UI 表现与交互规范

### 3.1 选区框与手柄设计（基于 DESIGN_SYSTEM.md）
- **选区边框**：`AccentBrush`（品牌强调色），宽度 2 DIP，外加 1px 半透明白色衬底，保证在深浅色任意背景上均清晰可辨。
- **8 个交互手柄**：
  - 4 角（8×8 DIP 方块）+ 4 边中点（横边 16×6 DIP，竖边 6×16 DIP）；
  - 手柄填充白色背景，边框为 `AccentBrush`；
  - 鼠标悬停显示标准系统光标：`Cursors.SizeNWSE`、`Cursors.SizeNESW`、`Cursors.SizeWE`、`Cursors.SizeNS`；内部悬停为 `Cursors.SizeAll`。
- **浮动操作栏（Confirm ActionBar）**：
  - 始终紧贴选区右下角外部（若出屏则自适应翻转至选区内部或上方，复用 `ScreenGeometry` 边界防溢出）；
  - 包含三个紧凑图标按钮（尺寸均遵从 32×32 DIP）：
    1. **确认按钮**（Primary 样式，勾号图标，带 ToolTip「确认翻译 (Enter)」）；
    2. **重选按钮**（Ghost 样式，重置图标，带 ToolTip「重新选区 (R)」）；
    3. **取消按钮**（Ghost 样式，叉号图标，带 ToolTip「取消 (Esc)」）。

### 3.2 冻结画面采样（ScreenCaptureBuffer）
在当前架构中，`CaptureOverlayWindow` 在 `CaptureAndCloseAsync` 时才触发 `ScreenCaptureService.CapturePngAsync(pixelRect)`。在确认模式下，调整过程用户可能切换了后台窗口。
**解决方案**：
1. 呼出遮罩的瞬间，`ScreenCaptureService` 在内存中完成各显示器物理位图的单次快照缓冲 `_frozenDesktopBitmap`；
2. 确认模式下无论调整多久，最终裁切直接在内存中的 `_frozenDesktopBitmap` 执行 `CopyPixels` 或 GDI `BitBlt` 截取选区，截取后立即 `Dispose()` 释放内存位图；
3. 确保 100% 绝对一致性，防止捕获内容与用户微调视线出现偏差。

---

## 4. OCR 校对面板接缝（L2 规格衔接）

当截图模式为 OCR 文本识别时，在结果送入翻译流前，`TranslationPanelWindow` 激活可折叠的校对视区：
1. **缩略图对照**：左上角以 64×64 DIP 紧凑卡片展示裁剪后的截图源图缩略；
2. **文本核对与修改**：直接在 `SourceInputBox` 中呈现 OCR 提取的文字（自动去除行尾连字符，保留代码段换行），提示文案：「请核对识别文字，支持就地编辑」；
3. **免重跑规则**：用户在编辑框中增删或纠偏文字后，点击「翻译」直接使用修改后的文本向引擎发包，绝对不重跑本地 OCR 流程；
4. **明确数据出网告知**：
   - 文本翻译分支：底部状态栏明示「仅发送校对后的纯文字」；
   - 视觉多模态分支（如截长图或图文混合发往视觉模型）：明确提示「将上传选区图片至所选视觉引擎」，并提供显式取消按钮。

---

## 5. 验收清单与实施改动范围

### 5.1 验收清单
- [ ] **快速/确认模式切换**：在设置中切换选项，核验是否即时生效。
- [ ] **误触容错**：在遮罩上点击小于 6×6 像素，验证窗口不关闭且恢复十字光标。
- [ ] **8 手柄与全方向拖拽**：分别拉动 4 角、4 边以及整体平移，核验尺寸尺寸标签与选区坐标计算准确（含负方向反拉）。
- [ ] **多显示器混合 DPI 验证**：在 100% 主屏与 200% 副屏之间跨屏拉框并微调，核验最终裁剪图片坐标零偏移。
- [ ] **Esc / Enter 快捷响应**：按 Enter 立即完成截取；按 Esc 立即退出且 0 任务残留。
- [ ] **冻结帧一致性**：开启确认模式并拖出选区，在后台视频持续播放状态下等待 5 秒确认，验证截取的是触发时刻的画面而非最新帧。

### 5.2 涉及文件清单
1. **核心覆盖层**：
   - `apps/PopGlot.Windows/CaptureOverlayWindow.xaml`（增加 8 手柄与浮动操作条 XAML）
   - `apps/PopGlot.Windows/CaptureOverlayWindow.xaml.cs`（引入 `CaptureState` 状态机与拖拽手柄命中测试）
2. **服务与设置**：
   - `apps/PopGlot.Windows/ShellSettings.cs`（增加 `ScreenshotConfirmMode` 配置项）
   - `apps/PopGlot.Windows/Sections/GeneralSection.xaml(.cs)`（增加「截图后允许确认微调」设置项开关）
   - `apps/PopGlot.Windows/ScreenCaptureService.cs`（支持全屏预捕获冻结快照）
