# N13 专注模式与快捷键暂停设计文档

> **编制日期**：2026-09-14  
> **编制人**：Pi（Teammate / W28）  
> **合同依据**：`docs/review-2026-09-12/PRODUCT-EXPANSION-REVIEW-V2.md` §5 N13 条款 + `docs/gap-analysis-2026-09-14/N10-N18.md` 对账F  
> **状态**：DESIGN_READY（设计完成，可供后续波次直接开工）  

---

## 一、目标与非目标

### 1. 核心目标
1. **全局快捷键一键暂停与专注支持**：
   - 托盘右键菜单增加「暂停全局快捷键」二级菜单，提供两档暂停选项：“暂停 15 分钟”与“直到手动恢复”；
   - 暂停期间释放系统全局热键注册，杜绝在进行全屏游戏、专业绘图或演示时发生快捷键冲突或误触弹窗；
2. **极简显式的状态可见性**：
   - 快捷键暂停期间，托盘菜单首项动态变为「**恢复全局快捷键**」；
   - 托盘图标 ToolTip 提示追加“（快捷键已暂停）”；
   - 主窗口工作台底栏状态点与文字显示“快捷键已暂停”，悬浮提示说明如何恢复；
3. **严格分离的双重暂停锁机制（核心防线）**：
   - 必须将**用户主动专注暂停**与**设置中录制快捷键产生的临时暂停**严格分离；
   - 录制快捷键打开/关闭设置窗体时，绝对不得意外恢复用户主动开启的“专注暂停”；
4. **低能耗且抗休眠的截止时间机制**：
   - 15 分钟暂停采用明确的世界时截止时间点（`PauseExpirationUtc`），杜绝死循环高频轮询；
   - 机器进入睡眠/休眠并跨过截止时间唤醒时，自动平滑恢复，无时间漂移。

### 2. 非目标
- 第一版不做复杂的第三方应用黑白名单识别（如检测特定 exe 自动静音）；
- 不记录用户屏幕使用轨迹或进程活跃度，严守隐私底线；
- 全屏状态下的被动弹窗屏蔽（如后台翻译完成不弹前台已由 A05/C05 覆盖，本模块专注全局键拦截）。

---

## 二、架构设计与双重暂停状态机

### 1. 状态管理模型

既有 `HotkeyService` 仅有单个 `_suspended` 布尔值，无法区分暂停来源。需在 `HotkeyService.cs`（或由 `App.xaml.cs` 统一调度）建立双层锁逻辑：

```csharp
public enum UserPauseMode
{
    Active,              // 正常启用
    Paused15Minutes,     // 倒计时暂停 15 分钟
    PausedIndefinitely   // 直到手动恢复
}

public sealed class HotkeyPauseCoordinator
{
    private UserPauseMode _userMode = UserPauseMode.Active;
    private DateTime? _expirationUtc;
    private bool _recorderSuspended = false;

    // 唯有当用户未暂停 且 录制器未挂起时，热键才真正在 Windows 处于注册生效状态
    public bool ShouldHotkeysBeRegistered => 
        _userMode == UserPauseMode.Active && !_recorderSuspended;

    public UserPauseMode CurrentUserMode => _userMode;
    public DateTime? ExpirationUtc => _expirationUtc;
}
```

```
┌────────────────────────────────────────────────────────────────────────┐
│                        全局热键物理生效决策门                         │
│                                                                        │
│   [ 用户主动模式 == Active ]  AND  [ 设置录制挂起 == False ]            │
│                 │                                │                     │
│                 └──────────────┬─────────────────┘                     │
│                                ▼                                       │
│                ┌──────────────────────────────┐                        │
│                │ 物理注册: HotkeyService 保持工作 │                      │
│                └──────────────────────────────┘                        │
│                                                                        │
│   任一条件为 False ──> 调用 HotkeyService.UnregisterAll() (安全静默)    │
└────────────────────────────────────────────────────────────────────────┘
```

### 2. 双重锁防护矩阵

| 场景 | `_userMode` | `_recorderSuspended` | 物理热键状态 | 退出场景后的处理 |
|---|---|---|---|---|
| **常态运行** | `Active` | `false` | **已注册（生效）** | — |
| **用户在托盘点暂停 15 分钟** | `Paused15Minutes` | `false` | **已注销（静默）** | 到期或手动恢复后切回 Active |
| **用户在托盘点直到手动恢复** | `PausedIndefinitely`| `false` | **已注销（静默）** | 必须显式点击“恢复快捷键” |
| **用户在暂停期打开设置录制** | `Paused...` | `true` | **已注销（静默）** | 录制完成 `_recorderSuspended=false`，**仍保持用户暂停态** |
| **常态下打开设置录制快捷键** | `Active` | `true` | **已注销（静默）** | 关闭设置窗口后恢复为物理注册 |

---

## 三、UI 交互设计与通知联动

### 1. 托盘右键菜单结构演进

```
【正常状态下的托盘菜单】
  打开 PopGlot
  极速查词
  恢复最近翻译
  ────────────────────────
  翻译选中文字        Ctrl+Alt+W
  截图翻译            Shift+截图
  截图提取文本 (OCR)
  ────────────────────────
  暂停全局快捷键  ▶  [暂停 15 分钟]
                     [直到手动恢复]
  ────────────────────────
  设置
  退出

【暂停状态下的托盘菜单】
  ▶ 恢复全局快捷键   <── 置顶高亮，单一主入口一键恢复
  ────────────────────────
  打开 PopGlot
  极速查词
  恢复最近翻译
  ────────────────────────
  ... (快捷键项目置灰显示 "已暂停")
```

### 2. 主窗口底栏与 ToolTip 反馈
- **托盘图标 ToolTip**：
  - 正常：“PopGlot”
  - 暂停：“PopGlot（快捷键已暂停，15分钟后恢复）”或“PopGlot（快捷键已暂停）”；
- **工作台底栏（`MainWindow.xaml` StatusFooter）**：
  - 左侧状态点 `StatusDot` 颜色切为 `WarningBrush`（琥珀色）；
  - `StatusTextBlock.Text = "快捷键已暂停（专注模式）"`；
  - 悬停 ToolTip 提供：“全局划词与截图快捷键已临时关闭，可在托盘右键恢复”。

---

## 四、抗睡眠与低开销定时器设计

### 1. 倒计时调度器（`DispatcherTimer`）
- 启动 `Paused15Minutes` 时：
  - 计算确切失效时间：`_expirationUtc = DateTime.UtcNow.AddMinutes(15)`；
  - 启动定时器，时间间隔设定为 **15 秒**（超低 CPU 占用，绝对不使用 100ms 级高频轮询）；
- 每次 Tick 检查：
  ```csharp
  if (DateTime.UtcNow >= _expirationUtc)
  {
      ResumeUserHotkeys();
  }
  ```

### 2. 系统休眠与锁屏唤醒感知（`PowerModeChanged`）
- 订阅 `SystemEvents.PowerModeChanged` 事件：
  ```csharp
  private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
  {
      if (e.Mode == PowerModes.Resume && _userMode == UserPauseMode.Paused15Minutes)
      {
          // 电脑唤醒时立即检查是否已超过 15 分钟
          if (DateTime.UtcNow >= _expirationUtc)
          {
              ResumeUserHotkeys();
          }
      }
  }
  ```
- 保证系统在休眠 1 小时唤醒后，快捷键立即处于恢复状态，不会发生“睡醒后依然冻结”的逻辑断层。

---

## 五、验收清单（DoD）

- [ ] **托盘菜单切换**：点击“暂停 15 分钟”后，托盘菜单立即呈现“恢复全局快捷键”，热键完全失去响应（被系统其他软件或游戏接管）；
- [ ] **手动即时恢复**：点击“恢复全局快捷键”，热键瞬间重新注册并可正常唤出浮窗；
- [ ] **倒计时自然到期**：15 分钟到期后，热键自动恢复生效，托盘菜单与主界面状态平滑归位；
- [ ] **设置录制互斥防御**：
  - 在“直到手动恢复”暂停态下打开设置窗口，录制新的快捷键；
  - 保存并关闭设置窗口；
  - **断言**：关闭设置后，全局热键依然处于用户暂停态，未被误唤醒；
- [ ] **进程退出清理**：快捷键暂停状态下退出应用，不残留任何后台孤儿定时器，下次冷启动默认恢复为 `Active` 正常状态（不持久化临时的 15 分钟暂停）。

---

## 六、涉及文件清单

- **修改**：
  - `apps/PopGlot.Windows/HotkeyService.cs`（引入双重锁状态与安全注销/重注册接口）
  - `apps/PopGlot.Windows/App.xaml.cs`（托盘菜单项扩展、定时器管理、电源事件监听与状态广播）
  - `apps/PopGlot.Windows/MainWindow.xaml(.cs)`（底栏状态栏响应暂停状态广播并展示琥珀色指示点）
- **测试**：
  - `tests/PopGlot.Windows.LogicTests/Program.cs`（增补 N13 暂停状态机、双重锁防线与到期时间断言用例）
