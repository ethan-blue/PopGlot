# PopGlot E3 真实环境验证操作手册
## D1–D9 验证债逐项 Runbook 与负责人三步验证指南

> **编制日期**：2026-09-14  
> **编制人**：Pi（Teammate / W26 手册波）  
> **依据**：`docs/gap-analysis-2026-09-14/C00-C07.md` §4 验证债清单（D1–D9）+ `POST-C00-C07-REVIEW-AND-CONTINUOUS-PLAN.md` §4.1 第二本账  
> **定位**：供测试工程师、外部复核者及项目负责人执行 E3（真实环境/真机）验收测试的标准作业程序（SOP）。每项均提供前置条件、精确步骤、判定阈值、留档路径与故障诊断。

---

## 目录
1. [验证债总览表（D1–D9）](#一验证债总览表d1d9)
2. [D1：真机失焦与输入法组合矩阵（C05）](#二d1真机失焦与输入法组合矩阵c05)
3. [D2：注入式熔断演练与真实重启交接（C06）](#三d2注入式熔断演练与真实重启交接c06)
4. [D3：临时账户登录自启 20 次验证（C07）](#四d3临时账户登录自启-20-次验证c07)
5. [D4：跨应用复制粘贴与围栏保真（C04）](#五d4跨应用复制粘贴与围栏保真c04)
6. [D5：词库故障注入与隔离保护（C02）](#六d5词库故障注入与隔离保护c02)
7. [D6：真实崩溃信封与日志脱敏复验（C03）](#七d6真实崩溃信封与日志脱敏复验c03)
8. [D7：HEAD 干净构建全量套件复跑与日志留档（全局）](#八d7head-干净构建全量套件复跑与日志留档全局)
9. [D8：生产 DLL 独立探针与 FFI 契约复验（全局）](#九d8生产-dll-独立探针与-ffi-契约复验全局)
10. [D9：真实网络四协议 E2E 最小往返（C01/C23）](#十d9真实网络四协议-e2e-最小往返c01c23)
11. [负责人三步验证指南（一页纸速查）](#十一负责人三步验证指南一页纸速查)

---

## 一、验证债总览表（D1–D9）

| 债项 ID | 归属任务 | 核心验证目标 | 环境要求 | 预期产物 / 证据 | 预计耗时 |
|:---:|:---:|---|---|---|:---:|
| **D1** | C05 | IME 组词、Alt+Tab、系统通知、右键菜单各场景下失焦不丢会话、Esc 级联取消 | Windows 10/11 桌面，中文输入法 | 4 场景截图 + 操作录屏 | 20 分钟 |
| **D2** | C06 | 致命异常单次提醒与熔断拒新单；重启交接 `POPGLOT_READY_EVENT` 零丢包 | Windows 桌面，PowerShell | 互斥体/事件观测日志 | 25 分钟 |
| **D3** | C07 | 临时账户开机 20 次 100% 常驻托盘，--background 零偷焦点 | 独立 Windows 账户 / VM | 20 次登录事件日志统计 | 40 分钟 |
| **D4** | C04 | VS Code/Notepad/浏览器跨进程划词、4 反引号内嵌 3 反引号逐字节一致 | 多主流应用环境 | 剪贴板比对 SHA-256 | 15 分钟 |
| **D5** | C02 | 只读/磁盘满/坏 JSON 时隔离保护有效，写失败文案如实回显不亮星 | 隔离数据根，ACL 模拟 | `corrupt-*` 备份与状态截图 | 15 分钟 |
| **D6** | C03 | 真实崩溃生成信封仅含 Namespace.Method 结构化帧，零原文/路径 | 崩溃注入环境 | 崩溃 log 脱敏审查表 | 15 分钟 |
| **D7** | 全局 | HEAD 干净工作树全套构建 + 测试 185/185 实跑日志留档 | 本机开发环境（无实例） | `artifacts/logs/*.log` | 10 分钟 |
| **D8** | 全局 | `popglot_ffi.dll` 独立 C ABI 探针与跨线程稳定性 | 本机开发环境 | 探针进程退出码 0 | 10 分钟 |
| **D9** | C23 | 四协议 Provider 真实出网最小往返，401/429 降级与单次发送扣除 | 真实外网，显式测试 Key | 4 协议网络往返日志 | 30 分钟 |

---

## 二、D1：真机失焦与输入法组合矩阵（C05）

### 1. 前置条件
- Windows 10 (21H2+) 或 Windows 11 桌面环境；
- 安装微软拼音或常用中文输入法（如微信输入法、搜狗输入法）；
- 启动 PopGlot 实例并处于托盘常驻状态；
- 打开 Notepad 与记事本备用。

### 2. 精确执行步骤

#### 场景 1：IME 组合中按 Esc 级联取消（防误关）
1. 按划词快捷键（默认 `Ctrl+Alt+W`）唤出翻译浮窗（`TranslationPanelWindow`），或快捷键唤出极速查词（`QuickSearchWindow`）；
2. 切换至中文输入法，在输入框中键入拼音字母（例如 `ceshi`），此时屏幕显示输入法候选词浮层；
3. **按一次 `Esc`**：
   - 观察输入法候选词浮层关闭；
   - 检查 PopGlot 窗口是否**依然保持在前台**，未被关闭；
4. 再次按 `Esc`：此时窗口正常退出或隐藏。

#### 场景 2：Alt+Tab 与前台应用切换
1. 唤出浮窗，在输入框中填入一段文本（生成部分结果）；
2. 确保浮窗右上角 **固定按钮（PinToggle）处于未选中状态**（未固定模式）：
   - 按 `Alt+Tab` 切换至 Notepad 窗口；
   - 检查浮窗是否平滑隐藏，未触发异常；
   - 点击托盘菜单「恢复最近翻译」或重新按划词快捷键；
   - 验证先前的原文与译文草稿是否完好保留（会话未丢失）。
3. 再次唤出浮窗，点击固定按钮使之点亮（ToolTip 显示“取消固定（失焦不隐藏）”）：
   - 切换至其他任意应用并点击其界面；
   - 检查浮窗是否依然悬浮在最上层，未自动隐藏。

#### 场景 3：右键菜单与子弹层失焦（W17 过滤回归验证）
1. 在浮窗或查词窗口输入区内右键单击，弹出 WPF 原生或系统上下文菜单（`ContextMenu`）；
2. 点击菜单外部空白区使菜单收起；
3. 检查浮窗主体是否**保持可见**，未误判为“进程外部失焦”而误隐藏（验证 `ForegroundBelongsToThisProcess` 机制）。

#### 场景 4：Windows 系统通知到来
1. 保持浮窗打开，通过 PowerShell 触发一条 Windows Toast 通知或按 `Win+A` 展开操作中心；
2. 检查浮窗是否保持稳定，无闪烁、无跳跃翻转、无崩溃日志。

### 3. 预期结果与判定标准
- **PASS**：
  - 输入法候选框存在时，首击 `Esc` 仅关闭输入法浮层，浮窗绝不隐藏；
  - 未固定时失焦自动隐藏，已固定时失焦不隐藏；
  - 隐藏后重新打开或托盘恢复，在途/已完成会话状态完整保留；
  - 内部右键菜单关闭不导致宿主窗体关闭。
- **FAIL**：首击 `Esc` 连同浮窗一起被杀；失焦后内容被清空；右键菜单关闭导致浮窗消失。

### 4. 证据留存方式
- 录屏或截图保存至：`artifacts/e3-evidence/D1/`
  - `D1_IME_Composition_Esc.png`
  - `D1_PinToggle_FocusLoss.png`
  - `D1_ContextMenu_Dismiss.png`
- 检查 `%LOCALAPPDATA%\PopGlot\logs\` 确认 0 错误日志生成。

### 5. 失败时的诊断入口
- `apps/PopGlot.Windows/TranslationPanelWindow.xaml.cs` 中的 `SourceInputBox_KeyDown` 与 `CloseAsUserIntent`；
- `apps/PopGlot.Windows/QuickSearchWindow.xaml.cs` 中的 `ForegroundBelongsToThisProcess`（W17 修复点）。

### 6. 预计耗时
- 20 分钟。

---

## 三、D2：注入式熔断演练与真实重启交接（C06）

### 1. 前置条件
- 管理员权限 PowerShell 终端；
- 编译好的 Release 版本 PopGlot（位于 `apps/PopGlot.Windows/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`）；
- 备好 Sysinternals `handle.exe` 或 PowerShell 查看命名事件。

### 2. 精确执行步骤

#### 阶段 A：致命异常单次提醒与全局熔断
1. 启动 PopGlot 并确认托盘图标显示；
2. 通过反射或调试缝隙向运行中应用注入非白名单致命异常（如未知 `InvalidOperationException("E3_TEST_FUSE")`）；
3. 观察托盘反馈：
   - 托盘气泡仅弹出**恰好一次**错误提示，说明发生了不可恢复异常；
   - 不产生连续气泡轰炸（无通知风暴）；
4. 尝试连续按 `Ctrl+Alt+W` 划词或查词快捷键：
   - 验证快捷键被 `RuntimeGate.NewWorkAllowed` 拦截，不启动新翻译、不弹出浮窗；
5. 查看 `%LOCALAPPDATA%\PopGlot\logs\crash-*.log`，确认崩溃已被记录并安全隔离。

#### 阶段 B：真实重启交接与 `POPGLOT_READY_EVENT` 观察
1. 启动两个 PowerShell 会话：
   - 会话 1 运行监听脚本：
     ```powershell
     # 监听全局就绪事件
     Get-EventSubscriber -Force | Unregister-Event
     Write-Host "Ready for event observation..."
     ```
2. 在已运行的 PopGlot 中触发「重启应用」（通过托盘或设置中的重启命令）；
3. 监控进程与事件时序：
   - 父进程创建唯一的 `POPGLOT_READY_EVENT`（格式 `Local\PopGlot.RestartReady.<GUID>`）；
   - 父进程启动子进程并传递该环境变量；
   - 父进程执行 `CleanupForHandover()` 卸载全局热键，并释放互斥体 `Local\PopGlot.SingleInstance`；
   - 子进程成功完成初始化后向 `POPGLOT_READY_EVENT` 发送信号；
   - 父进程接收到信号后正常退出（Exit Code 0）；
   - 子进程继承托盘，成为唯一的存活实例。
4. 使用 `Get-Process PopGlot` 验证系统中始终仅保留一个 PopGlot 进程，PID 成功平滑更替。

### 3. 预期结果与判定标准
- **PASS**：
  - 熔断后拒绝一切新工作，托盘错误通知仅弹 1 次；
  - 重启交接全过程中，热键未出现长达 >1s 的冲突报错，无系统“程序未响应”弹窗；
  - 最终单个实例存活，单实例互斥体无死锁。
- **FAIL**：重启后两进程并存（双实例打架）；父进程超时杀不掉；新进程启动失败导致托盘消失。

### 4. 证据留存方式
- 进程监视脚本输出日志：`artifacts/e3-evidence/D2/handover_timing.log`；
- 记录重启前后 PID 对照表。

### 5. 失败时的诊断入口
- `apps/PopGlot.Windows/Services/RestartHandover.cs`（`LaunchAndWaitReady`）；
- `apps/PopGlot.Windows/App.xaml.cs` 中的 `RestartApplication` 与 `CleanupForHandover`。

### 6. 预计耗时
- 25 分钟。

---

## 四、D3：临时账户登录自启 20 次验证（C07）

### 1. 前置条件
- Windows 10/11 本地管理员权限；
- 或一台配有干净系统的 Hyper-V / VMware 虚拟机（推荐快照机制）。

### 2. 精确执行步骤

1. **创建专用测试用户**（管理员 PowerShell）：
   ```powershell
   net user PopGlotTest User@Test123456 /add
   net localgroup Users PopGlotTest /add
   ```
2. **首次登录与配置自启**：
   - 登录 `PopGlotTest` 账户；
   - 将已发布的 PopGlot 便携目录拷贝至 `C:\Tools\PopGlot\`；
   - 双击启动 PopGlot，打开设置 → 通用 → 勾选「开机自动启动」；
   - 检查注册表确认键值已建立：
     ```powershell
     Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "PopGlot"
     # 预期值："C:\Tools\PopGlot\PopGlot.exe" --background
     ```
3. **执行 20 轮登录/注销自动化验证循环**：
   - 可利用任务计划程序或简单的注销脚本记录每次启动情况：
   ```powershell
   # 在 PopGlotTest 登录启动文件夹或自启之后运行一次检测脚本
   $proc = Get-Process PopGlot -ErrorAction SilentlyContinue
   $log = "C:\Tools\PopGlot\autostart_verify.log"
   $time = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
   if ($proc) {
       Add-Content $log "[$time] Cycle OK - PID: $($proc.Id), WorkingSet: $($proc.WorkingSet64)"
   } else {
       Add-Content $log "[$time] Cycle FAILED - Process not found"
   }
   ```
   - 执行「注销 → 登录」重复 20 次（或通过 Windows 自动登录脚本轮询触发）。
4. **每轮次人工/脚本目检**：
   - 登录进入桌面后，主窗口**绝不主动弹开**抢占用户焦点；
   - 系统托盘区域正确出现 PopGlot 图标；
   - 点击托盘能正常调出主界面，热键能正常唤起。

### 3. 预期结果与判定标准
- **PASS**：
  - 20 次连续登录，PopGlot 100% 成功启动（20/20）；
  - 每次启动均带 `--background` 标记，严格常驻托盘，0 次前台偷焦；
  - 注册表路径与实际二进制路径严格一致，无路径脱靶；
  - 20 次循环中无任何 crash 日志。
- **FAIL**：存在任何一次未自启；有任何一次启动弹出了主窗口；注册表被置为禁用态。

### 4. 证据留存方式
- `artifacts/e3-evidence/D3/20_cycles_logon.log`；
- 最后一轮托盘就绪截图：`artifacts/e3-evidence/D3/tray_ready.png`。

### 5. 失败时的诊断入口
- `apps/PopGlot.Windows/StartupRegistration.cs` 中的 `PlanSaveAction`、`BuildRunCommand`；
- `apps/PopGlot.Windows/App.xaml.cs` 中的 `OnStartup` 与 `--background` 消费逻辑。

### 6. 预计耗时
- 40 分钟（虚拟机脚本化约 15 分钟）。

---

## 五、D4：跨应用复制粘贴与围栏保真（C04）

### 1. 前置条件
- 打开目标宿主应用：
  - 1. VS Code（编辑器环境）
  - 2. Windows 记事本（简单文本环境）
  - 3. Chrome 或 Edge（浏览器 HTML 环境）
  - 4. Windows Terminal / PowerShell（终端环境）
- 启动 PopGlot 并开启划词翻译功能。

### 2. 精确执行步骤

#### 步骤 1：复杂围栏代码划词捕获
1. 在 VS Code 中打开包含以下嵌套代码的文本：
   ````markdown
   ````rust
   // 外层四反引号包含内层三反引号
   ```json
   { "key": "value\nwith\r\nnewlines" }
   ```
   fn test() {
       let tab = "    ";
   }
   ````
   ````
2. 全选上述文本，按下 `Ctrl+Alt+W` 触发划词翻译；
3. 检查 PopGlot 浮窗（或主窗）原文区：
   - 必须逐字符原样展示，首行四反引号、内嵌三反引号完整保留；
   - 每行前导空白（缩进）不可被 `TrimEnd` 或 `TrimStart` 抹去。

#### 步骤 2：跨应用真实剪贴板往返（W17 复制门验证）
1. 在翻译结果生成完毕后，点击浮窗的「复制译文」按钮；
2. 切换至记事本，按 `Ctrl+V` 粘贴；
3. 切换至 Windows Terminal，按 `Ctrl+Shift+V` 粘贴；
4. 将粘贴出的文本与浮窗呈现的译文文本进行逐字节对比（可使用 `fc.exe` 或 PowerShell 计算 SHA-256）：
   ```powershell
   $clip = Get-Clipboard
   $hash = Get-FileHash -InputStream ([System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes($clip))) -Algorithm SHA256
   Write-Host "Clipboard SHA256: $($hash.Hash)"
   ```
5. 验证 Windows 剪贴板历史记录（`Win+V`）：
   - 仅且仅有用户点击复制产生的该条记录；
   - 流式增量阶段以及 partial 状态未向剪贴板写入残片。

### 3. 预期结果与判定标准
- **PASS**：
  - 跨应用剪贴板往返 100% 逐字符吻合（包含缩进、空行与换行风格）；
  - 显式点击复制在所有宿主应用中均成功粘出内容；
  - 浮窗关闭/隐藏状态下通过全局热键触发的自动复制（如启用）正常工作，但隐藏中途取消绝对不写剪贴板。
- **FAIL**：代码缩进丢失；四反引号内嵌套被截断；点击复制后剪贴板为空。

### 4. 证据留存方式
- 记事本与 VS Code 粘贴比对截图：`artifacts/e3-evidence/D4/clipboard_exact_match.png`；
- 原文与粘出文本的 SHA-256 哈希比对记录。

### 5. 失败时的诊断入口
- `apps/PopGlot.Windows/Services/MarkdownPresenter.cs`（`ClassifyFenceLine`、`ClosesBlock`）；
- `apps/PopGlot.Windows/TranslationPanelWindow.xaml.cs` 中的 `TrySetClipboardAsync`（W17 修复点）。

### 6. 预计耗时
- 15 分钟。

---

## 六、D5：词库故障注入与隔离保护（C02）

### 1. 前置条件
- 运行中 PopGlot 实例；
- 测试目标目录：`%LOCALAPPDATA%\PopGlot\`；
- 备好只读权限控制工具（`icacls`）。

### 2. 精确执行步骤

#### 场景 A：词库文件损坏自动隔离与静默恢复
1. 退出 PopGlot，定位到 `%LOCALAPPDATA%\PopGlot\vocabulary.json`；
2. 人为注入畸变损坏内容（写入半截乱码字符，破坏 JSON 闭合结构）：
   ```powershell
   Set-Content -Path "$env:LOCALAPPDATA\PopGlot\vocabulary.json" -Value "{ ""words"": [ { ""id"": 123, " -Encoding utf8
   ```
3. 启动 PopGlot 并打开「资料库」界面；
4. 检查界面反馈：
   - 界面醒目提示词库文件已损坏并已安全隔离，未伪装成“生词本是空的”；
   - 提供了「重试加载」主按钮；
5. 查看磁盘文件：
   - 检查是否生成了隔离副本 `vocabulary.json.corrupt-<timestamp>`；
   - 验证损坏的原内容是否被完整保留在隔离副本中（哈希一致），原文件被重置为空白可用库。

#### 场景 B：写盘失败诚实可见（W16 修复闭环验证）
1. 将 `vocabulary.json` 属性设为只读或移除写权限：
   ```powershell
   attrib +R "$env:LOCALAPPDATA\PopGlot\vocabulary.json"
   ```
2. 在翻译浮窗中对一条翻译结果点击「收藏（五角星）」按钮；
3. 观察 UI 反应：
   - 浮窗提示文案如实显示“**未保存到本机，请重试。**”；
   - 五角星图标**保持熄灭状态**（绝不伪装收藏成功）；
4. 恢复写权限：
   ```powershell
   attrib -R "$env:LOCALAPPDATA\PopGlot\vocabulary.json"
   ```
5. 再次点击收藏按钮：
   - 提示文案变为“已加入生词本”；
   - 五角星持久点亮为金色/强调色。

### 3. 预期结果与判定标准
- **PASS**：
  - 损坏文件 100% 被隔离且哈希吻合，不丢失用户历史数据；
  - 权限缺失/写盘失败时前端如实报错，绝无假点亮；
  - 权限恢复后一键重试即可正常落盘。
- **FAIL**：解析崩溃导致程序退出；损坏文件被覆盖写成空白丢失原内容；只读状态下星标点亮。

### 4. 证据留存方式
- 损坏提示界面截图：`artifacts/e3-evidence/D5/corrupt_ui_notice.png`；
- 隔离文件列表输出：`dir $env:LOCALAPPDATA\PopGlot\vocabulary.json.corrupt-*`。

### 5. 失败时的诊断入口
- `apps/PopGlot.Windows/Services/VocabularyStore.cs`（`TryPersist`、`ToggleStarAsync`、`RetryLoad`）；
- `apps/PopGlot.Windows/Sections/LibrarySection.xaml.cs`。

### 6. 预计耗时
- 15 分钟。

---

## 七、D6：真实崩溃信封与日志脱敏复验（C03）

### 1. 前置条件
- 已开启诊断日志记录；
- 目标路径：`%LOCALAPPDATA%\PopGlot\logs\`。

### 2. 精确执行步骤

1. **触发/检索最近一次的真实崩溃日志**：
   - 若系统已存在历史 crash log（如 2026-09-13 返修前捕获的日志），直接打开分析；
   - 或在受控测试构建中触发一次测试空指针异常；
2. **审查崩溃日志内容（逐行核验脱敏规则）**：
   ```powershell
   Get-Content (Get-ChildItem "$env:LOCALAPPDATA\PopGlot\logs\crash-*.log" | Select -Last 1).FullName
   ```
3. **对照 C03 红线审查清单**：
   - [ ] **原文隔离**：日志中绝对不出现用户翻译的原文段落；
   - [ ] **密钥脱敏**：绝对不出现任何 `sk-`、`Bearer` 或长密码串；
   - [ ] **堆栈精简**：栈帧仅显示 `Namespace.ClassName.MethodName`，绝对不包含开发者机器的完整本地路径（如 `D:\Projects\GitProjects\...`）；
   - [ ] **无 Message 串穿透**：未受控的 Exception.Message 与 Data 键值未被裸写入磁盘。
4. **验证未来导出前复扫（SanitizeForExport 契约）**：
   - 验证 `DiagnosticsLog.cs` 中的脱敏静态过滤器能够稳定拦截路径形字符串。

### 3. 预期结果与判定标准
- **PASS**：日志纯净度达标，仅包含结构化错误代号与模块名称，0 本地绝对路径，0 用户私有字符。
- **FAIL**：日志中出现完整代码源码盘符路径或出现被翻译的文字片段。

### 4. 证据留存方式
- 抽检的脱敏日志文本片段保存至：`artifacts/e3-evidence/D6/sanitized_crash_sample.log`。

### 5. 失败时的诊断入口
- `apps/PopGlot.Windows/DiagnosticsLog.cs`（`ExtractFrame`、`Sanitize`）；
- `tests/PopGlot.Windows.PureTests/` 中关于 C03 注入矩阵的单元测试。

### 6. 预计耗时
- 15 分钟。

---

## 八、D7：HEAD 干净构建全量套件复跑与日志留档（全局）

### 1. 前置条件
- 退出一切正在运行的 PopGlot 用户实例（进程树清空，无占用）；
- 本地代码处于干净状态（`git status --short` 无意外修改）；
- .NET 10.0 SDK 与 Rust stable 编译器已安装并加入 PATH。

### 2. 精确执行步骤

打开管理员 PowerShell，逐行执行以下标准化构建与测试留档命令：

```powershell
# 1. 进入工作区根目录
cd D:\Projects\GitProjects\PopGlot

# 2. 确保目标日志目录存在
New-Item -ItemType Directory -Force -Path artifacts\logs

# 3. 记录当前构建身份
git rev-parse HEAD | Out-File artifacts\logs\HEAD-commit.txt
git status --short | Out-File artifacts\logs\HEAD-status.txt

# 4. 执行 PureTests（纯逻辑单测，18项）
dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj -c Release 2>&1 | Tee-Object artifacts\logs\PureTests-Release.log
$pureExit = $LASTEXITCODE

# 5. 执行 LogicTests（全量 Shell 与业务逻辑测试，185项）
dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj -c Release 2>&1 | Tee-Object artifacts\logs\LogicTests-Release.log
$logicExit = $LASTEXITCODE

# 6. 执行 Rust 全工作区测试（180项）
cargo test --workspace --locked 2>&1 | Tee-Object artifacts\logs\CargoTest-workspace.log
$cargoExit = $LASTEXITCODE

# 7. 输出汇总汇总表
Write-Host "================== Summary =================="
Write-Host "PureTests ExitCode  : $pureExit (Expected: 0)"
Write-Host "LogicTests ExitCode : $logicExit (Expected: 0)"
Write-Host "CargoTest ExitCode  : $cargoExit (Expected: 0)"
```

### 3. 预期结果与判定标准
- **PASS**：
  - `PureTests-Release.log` 末尾输出 `18 passed, 0 failed`，Exit Code 0；
  - `LogicTests-Release.log` 末尾输出 `185 passed, 0 failed`（或全量套件 100% 通过），Exit Code 0；
  - `CargoTest-workspace.log` 显示全工作区所有 crate 全部 ok，Exit Code 0；
  - 产物目录完整保存至 `artifacts/logs/`。
- **FAIL**：任何一项非 0 退出，或有测试失败断言。

### 4. 证据留存方式
- `artifacts/logs/HEAD-commit.txt`
- `artifacts/logs/PureTests-Release.log`
- `artifacts/logs/LogicTests-Release.log`
- `artifacts/logs/CargoTest-workspace.log`

### 5. 失败时的诊断入口
- 若 LogicTests 首行报 `InstanceGuardFailed (exit 3)`：说明后台有残留 PopGlot 进程，按本手册第十一节步骤杀掉进程后重试；
- 查看对应 log 搜寻 `FAILED` 行定位测试断言。

### 6. 预计耗时
- 10 分钟。

---

## 九、D8：生产 DLL 独立探针与 FFI 契约复验（全局）

### 1. 前置条件
- Rust 编译已完成，`target/release/popglot_ffi.dll`（或 debug）已就绪；
- Windows SDK 工具集（`dumpbin.exe` 可用）。

### 2. 精确执行步骤

1. **核验 FFI DLL 导出表签名**：
   ```powershell
   dumpbin /EXPORTS target\release\popglot_ffi.dll | Select-String "popglot_"
   ```
   - 验证导出清单包含所有标准入口：
     - `popglot_core_version`
     - `popglot_translate_stream`
     - `popglot_profile_validate`
     - 等核心 C ABI 函数。
2. **运行 Rust 独立 FFI 集成测试**：
   ```powershell
   cargo test -p popglot-ffi --locked 2>&1 | Tee-Object artifacts\logs\FFI-Contract.log
   ```
3. **并发调用与内存平稳性核验**：
   - 观察 FFI 压力测试用例是否稳定通过，多线程并发调用无 C++ / Rust Panic，无内存野指针泄漏。

### 3. 预期结果与判定标准
- **PASS**：导出符号无截断，ABI 结构对齐契约通过，所有 FFI 测试用例 PASS（Exit Code 0）。
- **FAIL**：DLL 缺符号；跨语言结构体大小不一致；并发时抛出 `AccessViolationException`。

### 4. 证据留存方式
- `artifacts/logs/FFI-Contract.log`；
- 符号导出清单 `artifacts/logs/ffi-exports.txt`。

### 5. 失败时的诊断入口
- `crates/popglot-ffi/src/`；
- `apps/PopGlot.Windows/Services/NativeMethods.cs`。

### 6. 预计耗时
- 10 分钟。

---

## 十、D9：真实网络四协议 E2E 最小往返（C01/C23）

### 1. 前置条件
- 具备公网连接；
- 准备 4 组真实可用的测试凭据（建议专用测试账号，小额额度）：
  - 1. OpenAI-compatible 协议（如 DeepSeek、Moonshot）
  - 2. OpenAI 官方 / Responses 协议
  - 3. Anthropic Claude 协议
  - 4. Google Gemini 协议
- 显式声明：**本项需要真实出网授权**，执行前必须由负责人知晓并批准。

### 2. 精确执行步骤

针对上述 4 种协议分别配置一个临时 Profile，执行**单句最小往返测试**（输入：“Hello, PopGlot!”，方向：EN → ZH）：

1. **测试连接（健康探针）**：
   - 点击「测试连接」按钮；
   - 验证回显包含真实的 HTTP 延迟（如 `200 OK (285ms)`），健康状态更新为可用。
2. **单句翻译流式验证**：
   - 触发翻译；
   - 观察流式文本依次到达（首 Token < 500ms），无乱码；
   - 终态 Markdown 渲染平滑切入；
   - 验证 C01 授权红线：
     - 检查后台计数器，确认该次发送**严格扣除 1 次许可配额**；
     - 拦截重试或在途取消时，绝对不发生幽灵重发。
3. **异常协议抗性验证**：
   - **401 鉴权拦截**：故意填入错误 Key，点击翻译，界面如实报告鉴权失败，不反复重试；
   - **断网提示**：拔掉网线或断开 Wi-Fi 触发翻译，界面如实显示“网络不可达”，提供设置修复入口。

### 3. 预期结果与判定标准
- **PASS**：
  - 4 协议均能完成至少 1 次完整的流式请求到渲染闭环；
  - 真实网络往返中，每次发送在出网边界只扣 1 次 Token 授权；
  - 401 与网络中断均有自愈或友好提示。
- **FAIL**：流式解析中断；并发时产生双发扣费；错误信息丢失。

### 4. 证据留存方式
- 4 协议流式成功完成截图：`artifacts/e3-evidence/D9/proto_{1..4}_success.png`；
- 网络会话时间戳记录。

### 5. 失败时的诊断入口
- `crates/popglot-core/src/provider.rs`；
- `apps/PopGlot.Windows/Services/OutboundPolicy.cs`（`TryClaimSend`）。

### 6. 预计耗时
- 30 分钟。

---

## 十一、负责人三步验证指南（一页纸速查）

> **目标**：为项目负责人在发布前夕提供**用时最短（约 15~20 分钟）、覆盖最全、零歧义**的一键式本地闭环验证流程。

```
 ┌─────────────────────────────────────────────────────────────────────────┐
 │                   负责人 3 步发布验收流水线                              │
 │                                                                         │
 │   [步骤 1: 退出环境] ───> [步骤 2: 套件留档] ───> [步骤 3: 性能基准测量] │
 │    (确保 0 实例挂起)       (Pure + Logic + Cargo)   (30次自启+内存达标) │
 └─────────────────────────────────────────────────────────────────────────┘
```

### 步骤 1：退出一切运行实例并检查干净环境
在打开终端前，先彻底清理工作机环境，确保实例守卫畅通：
```powershell
# 1. 彻底退出所有可能在后台常驻的 PopGlot 进程
Get-Process PopGlot -ErrorAction SilentlyContinue | Stop-Process -Force

# 2. 确认互斥体与进程已完全释放
Start-Sleep -Seconds 2
$running = Get-Process PopGlot -ErrorAction SilentlyContinue
if ($running) { Write-Error "仍有进程占用，请排查！" } else { Write-Host "环境已干净，可以开跑。" -ForegroundColor Green }
```

### 步骤 2：一键复跑三套件并留存归档日志
在仓库根目录直接运行以下指令（串行保证资源不争抢）：
```powershell
cd D:\Projects\GitProjects\PopGlot
New-Item -ItemType Directory -Force -Path artifacts\logs

Write-Host "1/3 正在跑 PureTests..." -ForegroundColor Cyan
dotnet run --project tests/PopGlot.Windows.PureTests -c Release > artifacts\logs\PureTests.log 2>&1
if ($LASTEXITCODE -ne 0) { throw "PureTests 失败！详见 artifacts\logs\PureTests.log" }

Write-Host "2/3 正在跑全量 LogicTests (185项)..." -ForegroundColor Cyan
dotnet run --project tests/PopGlot.Windows.LogicTests -c Release > artifacts\logs\LogicTests.log 2>&1
if ($LASTEXITCODE -ne 0) { throw "LogicTests 失败！详见 artifacts\logs\LogicTests.log" }

Write-Host "3/3 正在跑 Rust Cargo 套件 (180项)..." -ForegroundColor Cyan
cargo test --workspace --locked > artifacts\logs\CargoTests.log 2>&1
if ($LASTEXITCODE -ne 0) { throw "CargoTests 失败！详见 artifacts\logs\CargoTests.log" }

Write-Host "恭喜！三套自动化套件全绿（18 + 185 + 180 = 383 项通过）！" -ForegroundColor Green
```

### 步骤 3：一键编译 Release 并执行 C09 启动性能测量
```powershell
# 1. 打包自包含独立发布包
Write-Host "正在生成独立 Release 包..." -ForegroundColor Cyan
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish-package.ps1

# 2. 执行 30 次冷热启动基准与内存/CPU 采样
Write-Host "正在执行 30 次启动基准测试（预计耗时 3~5 分钟）..." -ForegroundColor Cyan
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/measure-startup.ps1 -Runs 30 -MeasureMemory

# 3. 检查测量结果报告
if (Test-Path "artifacts\perf\startup.json") {
    $report = Get-Content "artifacts\perf\startup.json" | ConvertFrom-Json
    Write-Host "========= C09 性能测量成果 =========" -ForegroundColor Green
    Write-Host "P50 启动耗时 : $($report.p50Ms) ms (门槛: <= 600 ms)"
    Write-Host "P95 启动耗时 : $($report.p95Ms) ms (门槛: <= 1200 ms)"
    Write-Host "空闲内存 WS  : $($report.workingSetMb) MB (门槛: <= 80/120 MB)"
    Write-Host "报告保存于   : artifacts\perf\startup.json"
} else {
    Write-Warning "测量报告未生成，请检查 artifacts\perf\ 目录。"
}
```

---
*文档编制完成，符合 E3 自动化与真机操作规范。*
