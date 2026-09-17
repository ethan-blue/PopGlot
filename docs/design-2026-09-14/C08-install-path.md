# C08 安装路径与自启生命周期设计方案：商业安装、稳定目录与平滑升级迁移

> **设计日期**：2026-09-14
> **阶段/层级**：P1 关键稳定性基础
> **依据合同**：第一轮 `docs/review-2026-09-12/MASTER-PRODUCT-REVIEW-AND-EXECUTION-PLAN.md` §8 C08；关联 C07（开机启动真实状态）、C24（安装签名更新回滚）
> **前置依赖**：C07 核心成果（`PlanSaveAction`、`ExtractExecutablePath`、`BuildRunCommand` 引号命令与 `--background` 约定）

---

## 1. 目标、产品决策与非目标

### 1.1 产品架构决策（ADR-001：双渠道发行策略）
1. **渠道定位**：
   - **商业默认推荐渠道**：签名安装器（Per-User Windows Installer，采用标准轻量 WiX/Inno/NSIS 构建）。
   - **开发者与便携渠道**：保留现有 Zip 便携版（Unsigned / Code-signed Portable）。
2. **决策理由**：
   - 相比 MSIX 的沙盒文件重定向（Virtualization VFS 会污染 Rust FFI 与 `%LOCALAPPDATA%` 数据共享）以及 Store 审核壁垒，Per-User 安装器对 WPF+Rust 本地工具最友好，不需要提升 UAC 管理员权限，与标准用户权限完全契合。

### 1.2 目标
1. **稳定安装目录策略**：
   - 默认固定安装路径：`%LOCALAPPDATA%\Programs\PopGlot\`（Per-User 隔离，无需管理员提权，遵循现代 Windows 应用规范）；
   - 二进制与数据完全分离：应用可执行程序与 native DLL 位于 `Programs\PopGlot\`，用户配置/历史/词库/日志严格存放在已有的 `%LOCALAPPDATA%\PopGlot\`，升级与卸载互不影响。
2. **自启 Run 键平滑对齐与自愈（Align with C07）**：
   - 安装器与应用启动时统一写入注册表项：`"C:\Users\<User>\AppData\Local\Programs\PopGlot\PopGlot.exe" --background`；
   - 升级安装时自动检测并迁移旧的 Run 键路径，若原先为 OS 禁用（`OsDisabled == true`），迁移后保持禁用，绝不强行覆盖用户在任务管理器中的禁用决策。
3. **便携模式诚实声明与迁移指引**：
   - 检测当前运行目录若非标准安装目录，判定为 Portable 模式；
   - 设置界面展示便携版专属提示：「当前为便携运行；若移动此文件夹，Windows 开机自启将失效」；
   - 运行中发现 Run 注册表旧路径失效但物理文件已移至新位，遵循 C07 `RepairRunPath()` 进行自愈修复，且同样不覆盖 OS 禁用状态。
4. **升级回滚与原子覆盖**：
   - 升级过程中若旧进程正在运行，弹出友好占用提示（或通过 `PopGlotSingleInstance` 互斥体协商平滑退出）；
   - 更新采用两阶段（下载/解压至 `.pending` 临时目录，校验 SHA256 与版本一致性后原子更名替换）。

### 1.3 非目标
1. **系统级全用户安装（Per-Machine）**：不安装至 `C:\Program Files\`，避免写入注册表 `HKLM` 引发强制 UAC 弹窗。
2. **强制全自动静默后台升级**：首期不引入驻留后台的 Windows Service 升级服务，升级触发权始终交给用户。

---

## 2. 目录规范与数据隔离矩阵

| 目录类型 | 物理路径 | 拥有者与权限 | 升级行为 | 卸载行为 |
|---|---|---|---|---|
| **二进制与依赖** | `%LOCALAPPDATA%\Programs\PopGlot\` | 当前标准用户（可写） | 完全原子覆盖替换 | 完全删除 |
| **持久数据根** | `%LOCALAPPDATA%\PopGlot\` | 当前标准用户 | **绝对不动**，兼容向前迁移 | 提示用户：[保留数据] 或 [完全清除] |
| **快捷方式** | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\PopGlot.lnk` | 当前用户 | 指向新 Exe 路径 | 完全删除 |
| **开机自启键** | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\PopGlot` | 当前用户 | 校验并更新 Exe 路径 | 完全删除该键值 |

---

## 3. 自启路径迁移与生命周期交互

### 3.1 路径检测与模式识别
在 `StoragePaths.cs` 或新建 `InstallEnvironment.cs` 中增加环境探测纯逻辑：
```csharp
namespace PopGlot.Windows;

public static class InstallEnvironment
{
    public static string StandardInstallRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "PopGlot");

    public static string CurrentExecutablePath =>
        Environment.ProcessPath ?? "";

    public static bool IsStandardInstalled =>
        string.Equals(
            Path.GetDirectoryName(CurrentExecutablePath)?.TrimEnd('\\', '/'),
            StandardInstallRoot.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
}
```

### 3.2 注册表 Run 键与 C07 合同联动（零破坏契约）
1. **启动自检阶段（`App.OnStartup`）**：
   - 读取当前 Run 键值并执行 `StartupRegistration.ReadState()`；
   - 若 `state.RunEntryPresent == true` 但 `state.PathMatches == false`：
     - 若 `state.OsDisabled == true`：**绝不调用任何写注册表动作**，避免解除任务管理器禁用；
     - 若 `state.OsDisabled == false`：触发 `StartupRegistration.RepairRunPath()`，自动将路径校正为当前运行的真实合法路径（由 `BuildRunCommand` 统一包裹引号与 `--background`）。
2. **安装器/升级脚本行为契约**：
   - 安装包打包器（Installer Script）在执行安装完成时：
     - 检查注册表是否有旧的 `HKCU\...\Run\PopGlot`；
     - 若存在旧路径且 `HKCU\...\StartupApproved\Run\PopGlot` 未被禁用，更新该字符串为新目录 Exe 路径；
     - 若用户勾选「安装完成后自启动」，写入新路径并直接通过参数 `--background` 启动首个实例。

---

## 4. 便携版交互与安全自愈（Portable UX）

针对以解压 Zip 形式运行的用户，UI 层（`GeneralSection.xaml`）提供针对性解释与状态呈现：
1. **状态条提示**：
   - 若检测到 `InstallEnvironment.IsStandardInstalled == false`：
   - 开机自启设置行下方显示淡灰色小字说明：「便携运行中：若移动此文件夹，请重新保存开机自启以校正路径。」
2. **文件夹移动后的自愈引导**：
   - 当用户将解压目录从 `D:\Tools\PopGlot` 移动到 `E:\PopGlot` 并首次双击启动时：
   - 应用静默校准：由于路径不一致且用户未被 OS 禁用，启动时托盘日志记录 `[INFO] Run path updated to E:\PopGlot\PopGlot.exe`；
   - 用户打开设置界面时，自启开关如实展示「已开启（路径已自愈匹配）」，不弹报错干扰用户。

---

## 5. 验收清单与实施改动范围

### 5.1 验收清单
- [ ] **全新安装验收**：在全新测试机执行 Per-User 安装，验证默认落盘在 `%LOCALAPPDATA%\Programs\PopGlot\`，开始菜单生成快捷方式，无 UAC 弹窗。
- [ ] **覆盖升级路径迁移**：旧版本写入旧路径自启，运行新安装器，验证 Run 键无缝更新至新路径，旧历史与词库 100% 完整继承。
- [ ] **OS 禁用保持测试**：在任务管理器中将 PopGlot 设为禁用，重新运行安装程序升级，验证升级后仍然保持「Windows 已禁用」，不被误开。
- [ ] **便携版移动测试**：解压便携版开启自启，关闭应用并移动文件夹，从新目录拉起，验证自愈修路径且 `--background` 参数未丢失。
- [ ] **卸载干净性与数据保留**：卸载程序提供选择框：「保留我的翻译历史与生词本（推荐）」，勾选时仅删除 `Programs\PopGlot`，不碰 `%LOCALAPPDATA%\PopGlot`；若全选清除，彻底清空注册表与数据根。

### 5.2 涉及文件清单
1. **新建安装配置与环境辅助**：
   - `apps/PopGlot.Windows/InstallEnvironment.cs`（路径识别与模式判定纯逻辑）
   - `scripts/installer/`（轻量安装器打包脚本模板，如 WiX 或 InnoSetup 配置文件）
2. **现有代码适配接缝**：
   - `apps/PopGlot.Windows/Sections/GeneralSection.xaml(.cs)`（便携模式提示文案绑定）
   - `apps/PopGlot.Windows/StartupRegistration.cs`（配合 `InstallEnvironment` 进行跨目录自愈逻辑加固）
