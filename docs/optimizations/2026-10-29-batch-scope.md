# PopGlot 0.1.34 批次实施范围界定（架构师 spec）

- 审查人：高见远（架构师）
- 日期：2026-10-29
- 基线：`cfc4853`（tag v0.1.33，工作区干净）
- 上轮清单：`docs/optimizations/2026-10-28-audit.md`
- 门禁：`scripts/verify.ps1`（LogicTests 361 / PureTests 58 / CliTests 29）
- 本批边界：**常规优化版**。巨型类整体拆分不做；本批只做下面 P0 四项 + 明确列出的收敛项。

---

## 0. 结论摘要

| ID | 处置 | 改动量 | 触碰「源文件路径硬编码」规则 |
| --- | --- | --- | --- |
| **OPT-021** | **P0 · 本批必做**（根因已定，修法与调用点无关） | M | 否（只动 tests 项目） |
| **OPT-002R** | **P0 · 本批必做**（前提已复核仍成立） | S | 否 |
| **OPT-010（切片 S1）** | **P1 · 本批做**（仅三个浮层表面的单一数值圆角，19 处） | S | **是** → 已核查 7 处源码读取点，见 §2.5 |
| **OPT-009** | **本批不做 · 降级重提**（原方案不等价，见 §3.2） | — | — |
| OPT-007 / 011 / 012 / 014 / 015 / 016 / 017 / 018 / 019 / 020 | 下批 | — | OPT-011/012 开工前必先 grep |

**本批最重要的一条**：OPT-021 的根因**不是**外部进程、**不是**「入口不对」，而是**测试宿主把「Application 只创建一次」这个 AppDomain 级不变量，委托给了一个不适任的判据（`Application.Current`）**。上轮设想的「决定性检查」（回看旧日志里 `AssertNoConflictingAppInstance` 落在 passed 还是 failed）**已经不需要做了** —— 当前代码里有一条更强的证据可以直接排除该假设，见 §1.1。

---

## 1. OPT-021 · LogicTests 非确定性

### 1.1 先拆掉上轮遗留的假设 3（外部 PopGlot 进程）

上轮把它列为「收窄但未证伪」，并建议回头翻一轮 227/134 的原始输出。**这个没有必要了 —— 假设 3 已死，且是被当前代码杀死的**：

- `tests/PopGlot.Windows.LogicTests/Program.cs:44-52`：进入 `Main` 后第一件事就是 `TestIsolation.FindConflictingAppInstancePid()`，命中即**打印一条错误 + `return 3`，一个用例都不跑**。
- `Program.cs:79` 还有第二道 `TestIsolation.AssertNoConflictingAppInstance`（常规用例）。
- `git log -S "No tests were executed" --date=short` → 该硬门禁由 `9e4832a`（**2026-09-13**）引入，**远早于** 2026-10-28 那次 227/134 的运行。

⇒ 227/134 那次运行**一定是在没有真实 PopGlot 实例的条件下跑满 361 个用例的**。外部进程冲突 produce 不出这个形态（若存在，退出的形态是 `return 3` + 0 个用例）。**假设 3 排除，不要再为它投入任何工时。**

同理，`verify.ps1` vs 裸命令之争（上轮 8.5 已论证两者逐字相同）也一并结束：**非确定性在套件内部，与调用方式、与外部环境都无关。**

### 1.2 本轮读码得到的四个事实

1. **公有 3 个 `new Application/App` 创建点，彼此互不知情，且没有一个带锁**：
   - `tests/.../Program.cs:9968` — `EnsureApplication()` 里的 `new App { ShutdownMode = OnExplicitShutdown }`（守卫 `if (Application.Current is null)`，9966 行）
   - `tests/.../WaveRegressionTests.cs:141` — 逐字复制的 `EnsureApp()`，同一守卫（139 行）
   - `tests/.../Program.cs:6149` — **`new Application { ... }`（基类，非 App）**，守卫同样是 `if (Application.Current is null)`（6147 行）。这个是上轮清单**完全没有提到的第三个创建点**。
2. **Runner 本身严格串行**：`Main` 主体（`Program.cs:35-1000`）内无 `Task.WhenAll` / `Parallel.*` / `Task.Run` / `ThreadPool`，`Run/RunAsync/RunSta/RunStaBatch` 都是「跑完一个再跑下一个」。⇒ **不存在「两个用例同时进 `EnsureApplication`」的常规路径。**
3. **设计上的首个创建者是确定的**：`Main:93` `RunSta("connection panel stays honest...", CliSubprocessTests.ConnectionPanelStaysHonestWithoutSpawning)` → `CliSubprocessTests.cs:289` 调 `Program.EnsureApplication()`。它是 `Program.cs` 里第一个 `EnsureApplication` 调用点（1090）之前唯一的 WPF 触碰点，而 `RunSta` 封送到 STA harness 线程 ⇒ **健康情况下 App 建在 harness STA 线程上**。
4. **`Main` 是 `[STAThread] async Task<int>`**：控制台进程无 `SynchronizationContext`，第一个真正 yield 的 `await`（`Main:82`）之后，后续续体落在**线程池 MTA 线程**上 ⇒ `Run(...)` 形态的用例**在 MTA 线程上直接执行**。`EnsureApplication` 若在 MTA 线程被调用，`new App()` 就建在 MTA 上（已实证：WPF 不校验 STA）。

### 1.3 结构诊断（这样可以解释全部观测）

失败信息是同一句「不能在同一 AppDomain 中创建多个 Application 实例」。要触发它，**必须同时成立**：

- (a) `Application.Current` 读到 **null**（守卫放行 → 尝试创建）
- (b) AppDomain 的「已创建」槽位已是 **true**（于是抛异常）

也就是说：**AppDomain 槽位与 `Application.Current` 发生了背离（divergence），且背离之后不可修复** —— 此后**每一个**触碰 WPF 的用例都会走 (a) 进 (b)，拿到同一条异常。这解释了三个观测：

| 观测 | 解释 |
| --- | --- |
| 失败数成片（84 / 134）而非零散 | 一旦背离，后续全部失败；失败数 = 背离点之后触碰 WPF 的用例数 |
| 总数恒为 361 | 用例一个没少，只是每个用例的**结论**被同一个坏状态改写：背离点之前的通过、之后的失败，两者相加恒等于总数 |
| 通过/失败两极跳 | 背离**是否发生**、以及**在哪个用例发生**，取决于运行时状态（哪个线程先建、是否曾故障、App 是否被释放） |

代码里已经留有两处「现场痕迹」，佐证背离在真实运行中出现过：

- `Program.cs:6155` 的 `else if (Application.Current.Resources.MergedDictionaries.Count == 0)` —— 这段补丁的意思是「存在过一个 `Application`，但它没有合并主题字典」（即半初始化/基类实例）。这正是首次创建失败后留下的形态。
- `Program.cs:9977-9978` 的注释「Keep a strong root: `Application.Current` alone must not be the only reference keeping the host alive」—— 有人见过 Application 实例「保不住」。

**背离的具体触发事件，单靠读码无法定死**，候选按可能性排序：

| 候选 | 机制 | 我能否证实 |
| --- | --- | --- |
| H1 首次创建中途失败 | `new App { ShutdownMode = ... }` 唯一位于 `base()` 之后的代码是对象初始化器；App 无显式构造函数（字段初始化器在 `base()` **之前**跑），所以故障点只能在这里或 `base()` 尾部。失败后槽位已占、`Current` 不完整 → 全局背离 | 不能（需要复现现场） |
| H2 App 的 Dispatcher 关闭 | 若 harness dispatcher 被关闭，WPF 的 shutdown 路径会清 `_appInstance`（`Current`→null），而「已创建」槽位不清 | 佐证：`LifecycleStress.cs:68` 已经在打印 `Application.Current?.Dispatcher.HasShutdownStarted`，说明团队见过 App dispatcher 关闭态 |
| H3 遗留后台续体越路 | 某 `Run(...)` 用例残留的生产代码续体（fire-and-forget `Dispatcher.InvokeAsync`、`Task.Delay` 循环）在后续时间点触发创建。注意：触发「创建」的代码只有测试侧三处，所以这条只能经由 test helper，概率低但不能完全排除 | 不能 |

**架构结论：不需要先证明哪一个。** 三个候选唯一的共同前提是一样的 —— *「用过期的/线程相关的 `Application.Current` 来表达 AppDomain 级 once 语义」*。把这一点去掉，三个候选同时失效。**并且修复必须顺带让下一次发生变成自解释的（§1.5 STEP 6 / §1.7）**，否则我们下批还会再花一天猜。

### 1.4 关于「哪个调用者会成为首个触发者」—— 回答 team-lead 的原问题

- **设计上**：不存在竞争窗口。首个触发者恒为 `Main:93`（harness STA 线程），这是确定的。
- **运行时**：有窗口，但不是「两个用例抢创建」，而是**前述背离一旦发生，`EnsureApplication` 从「幂等守卫」退化成「每台 Mine Restart 都戳一次同一个坏锁」**。这正是 134 条一模一样的错误而不是 1 条 + 133 条级联的原因。
- 所以：试图「指出第一个调用者并修好它」是**治不了根**的 —— 问题的形状是「*没有所有者*」，不是一个调用点错。

### 1.5 精确改法（6 步，全部在 tests 项目内，零生产代码改动）

> 设计目标：**AppDomain 级 once 语义由我们自己的 `bool` 表达，完全不读 `Application.Current`；Application 永远且只在唯一的 STA harness 线程上创建；失败必须在第 1 个用例之前暴露，不得级联。**

#### STEP 1 · 新增状态与引导方法（插入 `Program.cs:9962` 之后、`EnsureApplication` 之前）

```csharp
    // ---- OPT-021: exactly-once WPF Application, owned by the STA harness ----
    private static readonly object AppBootGate = new();
    private static Exception? _appBootFailure;          // 以 Volatile 读写
    private static volatile bool _appBootAttempted;
    private static volatile bool _appCreatedCore;

    /// <summary>
    /// Creates the ONE Application, on the STA harness thread, before any test
    /// can create one from its own thread. Returns null on success, or the
    /// primal failure (sticky — never retried, never re-created).
    /// </summary>
    internal static Exception? BootWpfApplicationOnce()
    {
        if (Volatile.Read(ref _appBootAttempted)) return Volatile.Read(ref _appBootFailure);
        lock (AppBootGate)
        {
            if (Volatile.Read(ref _appBootAttempted)) return Volatile.Read(ref _appBootFailure);
            try
            {
                // Main never runs on the harness thread, and no test can see
                // this before it completes → Invoke cannot deadlock here.
                GetStaHarnessDispatcher().Invoke(CreateApplicationCore);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _appBootFailure, ex);
            }
            Volatile.Write(ref _appBootAttempted, true);
        }
        return Volatile.Read(ref _appBootFailure);
    }

    /// <summary>Runs ONCE and only on the STA harness thread.</summary>
    private static void CreateApplicationCore()
    {
        if (Volatile.Read(ref _appCreatedCore)) return;
        var thread = Thread.CurrentThread;
        try
        {
            Console.WriteLine($"[harness] creating Application on '{thread.Name}' #{thread.ManagedThreadId} apartment={thread.GetApartmentState()}");
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try { app.InitializeComponent(); }
            catch (Exception initEx)
            {
                // Kept from the original code: a failed resource load must not
                // be fatal, but it IS diagnostically important — print it.
                Console.WriteLine($"[harness] InitializeComponent fell back: {initEx.GetType().Name}: {initEx.Message}");
            }
            _bootstrappedApp = app;
            app.DispatcherUnhandledException += (_, args) =>
            {
                _dispatcherFailures++;
                Console.WriteLine($"DISPATCHER EXCEPTION (#{_dispatcherFailures}): {args.Exception.GetType().Name}: {args.Exception.Message}");
                Console.WriteLine(args.Exception.StackTrace);
                args.Handled = true;
            };
            Volatile.Write(ref _appCreatedCore, true);
        }
        catch (Exception ex)
        {
            // By the time we get here the AppDomain slot is claimed whether or
            // not Application.Current is readable — the run CANNOT recover.
            throw new InvalidOperationException(
                $"the WPF harness could not create its Application on thread #{thread.ManagedThreadId} " +
                $"(name='{thread.Name}', apartment={thread.GetApartmentState()}). The AppDomain slot is now claimed " +
                $"and this process can never create another Application; every later WPF test would fail with a " +
                $"duplicate-instance error that has nothing to do with itself.", ex);
        }
    }
```

#### STEP 2 · 重写 `EnsureApplication`（替换 `Program.cs:9964-9996` 全部）

```csharp
    /// <summary>
    /// Ensures the harness Application is usable and the dark theme is applied.
    /// OPT-021: this never CREATES anything — the single owner is
    /// <see cref="BootWpfApplicationOnce"/>. Application.Current is AppDomain
    /// state owned by WPF and must never be used as the once-flag.
    /// </summary>
    internal static void EnsureApplication()
    {
        var failure = Volatile.Read(ref _appBootFailure);
        if (failure is not null)
        {
            throw new InvalidOperationException(
                "the WPF harness Application failed to boot; continuing would only produce hundreds of identical " +
                "duplicate-instance failures: " + failure.Message, failure);
        }
        ThemeService.Apply(ThemePreference.Dark);
    }
```

> 注意：`ThemeService.Apply`（`apps/PopGlot.Windows/ThemeService.cs:41-51`）自己会按 `Application.Current.Dispatcher` 做跨线程封送。**App 现在恒属于 harness 线程，因此所有 `RunSta` 用例里的 `Apply` 走同步分支，`Run(...)` 用例里的 `Apply` 走 `BeginInvoke` 分支 —— 与今天的健康形态逐字一致**，不改行为。

#### STEP 3 · 删除第三个创建点（替换 `Program.cs:6144-6162`）

```csharp
        // SettingsWindow resolves its nav styles from the app-level theme
        // dictionary — App.xaml merges Themes/Controls.xaml, so the harness
        // Application already carries it. OPT-021: this file must NEVER create
        // an Application of its own.
        EnsureApplication();
```
（`Program.cs:6144` 起那段 `if (Application.Current is null) { new Application {...} } else if (...MergedDictionaries.Count == 0) {...}` 整块删除；保留紧随其后的 `ThemeService.Apply(ThemePreference.Dark);`？——**不要保留**：`EnsureApplication()` 内部已经 Apply。）

**为什么这样删是安全的**：`apps/PopGlot.Windows/App.xaml:5-9` 已经把 `Themes/Controls.xaml` 合进 `Application.Resources`，所以 harness App 天然带那本字典；原 6155 行的 `else if` 补丁就是为了给「半初始化实例」补字典，而这个形态在新方案下不再可能出现。

#### STEP 4 · 收敛重复实现（`WaveRegressionTests.cs:133-154`）

该文件有 30+ 处 `EnsureApp()` 调用，不要逐处改，改函数体即可（保留 `EnsureApp` 这个名字以最小化 diff）：

```csharp
    /// <summary>
    /// OPT-021: delegates to the single owner. This file must never create an
    /// Application of its own — a second creator is how the harness used to
    /// poison its own AppDomain.
    /// </summary>
    private static void EnsureApp() => Program.EnsureApplication();
```
**必须同时删除 `WaveRegressionTests.cs:133` 的 `private static Application? _bootstrappedApp;`** —— 删除后该字段不再被赋值（只有旧的 150 行用过），保留会因未赋值私有字段触发警告，而本仓库 `TreatWarningsAsErrors` 会让门禁直接红。

#### STEP 5 · 修 harness dispatcher 的二次检查竞态（`Program.cs:9931-9961`）

现有写法在**锁外**读 `_staHarnessDispatcher`（非 volatile），另一个线程可能因此拿到 `null` 并在 `_dispatcherFailures` 之前炸 NRE。最小修法：

```csharp
    private static System.Windows.Threading.Dispatcher GetStaHarnessDispatcher()
    {
        var current = Volatile.Read(ref _staHarnessDispatcher);
        if (current is not null) return current;
        lock (StaHarnessGate)
        {
            if (_staHarnessThread is null)
            {
                _staHarnessThread = new Thread(() =>
                {
                    var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                    SynchronizationContext.SetSynchronizationContext(
                        new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
                    Volatile.Write(ref _staHarnessDispatcher, dispatcher);   // ← 原为普通赋值
                    StaHarnessReady.Set();
                    System.Windows.Threading.Dispatcher.Run();
                })
                { IsBackground = true, Name = "PopGlot.LogicTests.WpfHarness" };
                _staHarnessThread.SetApartmentState(ApartmentState.STA);
                _staHarnessThread.Start();
            }
        }
        StaHarnessReady.Wait();
        return Volatile.Read(ref _staHarnessDispatcher)
               ?? throw new InvalidOperationException("the STA harness dispatcher was never published");
    }
```

#### STEP 6 · `Main` 里提前引导（`Program.cs:56` 之后、`TestIsolation.Initialize();` 的下一行）

```csharp
        TestIsolation.Initialize();

        // OPT-021: the ONE Application is created here — before test #1 and on
        // the dedicated STA harness thread — so no later creator can race it,
        // and a failure reports ONE root cause instead of cascading into
        // hundreds of identical duplicate-instance failures.
        var appBootFailure = BootWpfApplicationOnce();
        if (appBootFailure is not null)
        {
            Console.Error.WriteLine(
                "harness failure: the WPF Application could not be created before any test ran; " +
                "aborting so this run reports a single actionable root cause. No tests were executed.");
            Console.Error.WriteLine(appBootFailure.ToString());
            return 4;   // 4 = harness bootstrap failure (3 is already taken by the conflicting-instance gate)
        }
```

**放在这一行的理由**：必须晚于 `TestIsolation.Initialize()`（App 里的数据加载器是 `Lazy`，但 `TestIsolation.Initialize():64` 已经先碰 `App.SharedSessionStore`，且用户文件路径切换在此完成）；必须早于第一个用例（否则退化成「级联形态」）。两个特殊模式分支（`Main:58-76`）也需要它，放 57 行一并覆盖。

`new App()` 的**副作用可忽略**（已核查）：`App` 无显式构造函数，实例字段初始化器只有三个 `Lazy<>` 包装 + `new ClipboardSelectionService(new WindowsSelectionClipboardAdapter())`，而 `ClipboardSelectionService` 的构造函数（`ClipboardSelectionService.cs:26-29`）**只是赋值，不碰剪贴板/COM**。也就是说 `new App()` 不读写用户数据 —— 与 `App.xaml.cs` 顶部注释「The data root is selected in OnStartup, after Application construction」一致。

#### 兜底方案（仅当 STEP 6 的提前引导出现新增失败时）

不要回到老样子，改成**惰性但同样无锁/p不发 atoms** 的版本：保留 STEP 1/2 的 `CreateApplicationCore`，把 `EnsureApplication` 换成：

```csharp
    // 不在持锁状态下 Invoke；harness 线程上永不等候（否则自锁）
    if (Volatile.Read(ref _appBootFailure) is not null) throw sticky;
    var dispatcher = GetStaHarnessDispatcher();
    if (dispatcher.CheckAccess()) { CreateApplicationCore(); return; }        // 已在正确的线程
    bool iOwn = false;
    lock (AppBootGate) { if (!_appBootAttempted) { _appBootAttempted = true; iOwn = true; } }
    if (iOwn) { try { dispatcher.Invoke(CreateApplicationCore); } catch (Exception ex) { sticky = ex; throw; } }
    else { WaitUntil(_appCreatedCore || sticky != null); }                    // 仅非 harness 线程会走到这里
    ThemeService.Apply(ThemePreference.Dark);
```
死锁分析（为何安全）：① 任何线程都不持锁做 `Invoke`；② harness 线程永远不在等待分支（要么直接建，要么返回）；③ 别人持有的 Invoke 请求最终会被 harness 线程处理，因为它没有被自己阻塞。

### 1.6 这一版为什么是确定性的（显式列出，供 QA/下批复核）

| 风险 | 修复前 | 修复后 |
| --- | --- | --- |
| 「只创建一次」的判据 | `Application.Current is null`（WPF 内部 AppDomain 静态，可能与真实槽位背离） | 我们自己的 `volatile bool` + 锁，与 WPF 状态无关 |
| 创建线程 | 谁先调谁就是（可能是线程池 MTA 线程） | 恒为 `PopGlot.LogicTests.WpfHarness` 这一个 STA 线程 |
| 创建点数量 | 3 处，互相不知道 | 1 处（`CreateApplicationCore`），其余全部降级为断言 |
| 创建失败的后果 | 第 N 个用例失败 + 后续成片失败 | 在第 1 个用例之前 `return 4`，**不可能级联** |
| 再次发生时能否自解释 | 只能看到「多个 Application」 | 错误信息直接带出线程号 / 线程名 / apartment + primal exception |

### 1.7 验收标准（QA 必读）

1. **稳定性**：同一台机器、不改任何环境，连续 `dotnet run --project tests/PopGlot.Windows.LogicTests` **5 次**。
   - 通过：5 次结果**逐字相同**（通过数、失败数、失败用例名全部一致）。
   - 不通过：任何一次出现不同 PUBLISH 的通过/失败分布 —— 无论数字多好看都不算过头。
2. **快速失败**：临时把 `CreateApplicationCore` 改成必抛（人工故障注入），确认输出是**一条** harness failure + `exit 4` + 0 个用例执行，**而不是**成片 FAIL。注入后必须恢复。
3. **改动面**：`git diff` 不应包含任何 `apps/` 下的文件（本项零生产代码改动）。
4. 日志里不再出现 `AppDomain` 相关异常：
   `grep -ci "AppDomain" <run log>` 必须为 0。
5. 全量门禁：`scripts/verify.ps1` 一次通过。

> **判据（与上轮一致但更严）**：成片失败 ⇒ 疑似非确定性，先不当回归处理；但本项修复之后，「这大概又是非确定性」不再是可接受的结案理由 —— 必须给出单数的根因。

---

## 2. OPT-010 · 美观 token 化的最小可收敛切片（切片 S1）

### 2.1 分布统计（实测，`apps/PopGlot.Windows` 下 `--include=*.xaml`）

**圆角**：63 处数值字面量，其中 `Themes/Controls.xaml` 内 17 处（token 文件自身的模板用途，本批不动），库外 46 处。

值分布（库外，单一数值）：`6`×12、`12`×6、`4`×5、`10`×4、`3.5`×3、`0`×4、`5`×1、`3`×1、`16`×1、`17`×1、复合值（`11,11,0,0` / `0,0,11,11` / `9` 系列）×6。

**字号**：76 处数值字面量。值分布：`12`×23、`11`×18、`14.5`×8、`13`×8、`12.5`×6、`14`×5、`11.5`×4、`13.5`×2、`20`×1、`15`×1。

### 2.2 为什么「三个浮层表面 + 单一数值圆角」是最小可收敛切片

- 单一数值 → token 逐字等价，**零视觉变化可证**；复合值（`9` 是 PaneRadius-1 的边框补偿、`11` 是 PaneRadius+1）语义不等价，**本批不做**，下批再以 `PaneInnerRadius` 这类显式 token 处理。
- 三个文件均为「浮层/浮窗」视觉语义，边界清晰，一个 commit 可以读到底。
- 覆盖率：改造 19 处 / 库外 46 处 = **41%**，且覆盖了最集中的四个值里的全部（6/12/4/10 共 27 处中的 19 处）。

### 2.3 新增 token（`apps/PopGlot.Windows/Themes/Controls.xaml`，紧贴现有 19-22 行的注释块）

```xml
    <!-- Surface system: controls 6px, panes/cards 10px, floating windows 12px. -->
    <CornerRadius x:Key="ControlRadius">6</CornerRadius>
    <CornerRadius x:Key="CardRadius">10</CornerRadius>
    <CornerRadius x:Key="PaneRadius">10</CornerRadius>
    <CornerRadius x:Key="ChipRadius">4</CornerRadius>          <!-- 新增：小胶囊/标签 -->
    <CornerRadius x:Key="FloatingRadius">12</CornerRadius>     <!-- 新增：浮层窗体外框 -->
```

> **红线**：`CardRadius` 与 `PaneRadius` 数值重复（都是 10）是历史遗留，**本批保持两个 token 都存在、数值都不动**（存在若干个曝露/依赖），不要「顺手合并」。

### 2.4 精确替换清单（文件 + 行号 + 改法）

| 文件 | 行 | 现值 | 改为 |
| --- | --- | --- | --- |
| `TranslationPanelWindow.xaml` | 25 | `12` | `CornerRadius="{StaticResource FloatingRadius}"` |
| `TranslationPanelWindow.xaml` | 59 | `6` | `{StaticResource ControlRadius}` |
| `TranslationPanelWindow.xaml` | 288 | `6` | `{StaticResource ControlRadius}` |
| `TranslationPanelWindow.xaml` | 358 | `6` | `{StaticResource ControlRadius}` |
| `TranslationPanelWindow.xaml` | 411 | `6` | `{StaticResource ControlRadius}` |
| `TranslationPanelWindow.xaml` | 413 | `6` | `{StaticResource ControlRadius}` |
| `TranslationPanelWindow.xaml` | 415 | `6` | `{StaticResource ControlRadius}` |
| `TranslationPanelWindow.xaml` | 456 | `6` | `{StaticResource ControlRadius}` |
| `TranslationPanelWindow.xaml` | 499 | `12` | `{StaticResource FloatingRadius}` |
| `QuickSearchWindow.xaml` | 15 | `12` | `{StaticResource FloatingRadius}` |
| `QuickSearchWindow.xaml` | 111 | `10` | `{StaticResource CardRadius}` |
| `QuickSearchWindow.xaml` | 205 | `4` | `{StaticResource ChipRadius}` |
| `QuickSearchWindow.xaml` | 382 | `12` | `{StaticResource FloatingRadius}` |
| `CaptureOverlayWindow.xaml` | 77 | `6` | `{StaticResource ControlRadius}` |
| `CaptureOverlayWindow.xaml` | 86 | `4` | `{StaticResource ChipRadius}` |
| `CaptureOverlayWindow.xaml` | 94 | `12` | `{StaticResource FloatingRadius}` |
| `CaptureOverlayWindow.xaml` | 108 | `4` | `{StaticResource ChipRadius}` |
| `CaptureOverlayWindow.xaml` | 118 | `4` | `{StaticResource ChipRadius}` |
| `CaptureOverlayWindow.xaml` | 131 | `12` | `{StaticResource FloatingRadius}` |

**明确不碰**（列出来，防止工程师顺手多做）：

- `TranslationPanelWindow.xaml:15`（`0`）、`:41`（`11,11,0,0`）、`:474`（`3`）
- `QuickSearchWindow.xaml:29`（`11,11,0,0`）、`:70`（`5`）、`:334`（`0,0,11,11`）
- **整个 `Sections/` 目录**（`TranslateSection.xaml` 的 `9,0,0,0` 等是布局分段契约，且被下方 due 测试按 token 推导，必须单独一批）
- `MainWindow.xaml` / `SettingsWindow.xaml` / `HelpWindow.xaml`（只有 `0` 与 `3.5`，无对应 token）
- **字号一律不动**（见 §2.6）

### 2.5 视觉回归风险 —— 零，当且仅当以下三条同时满足

1. token 数值与旧字面量**逐字相等**（上表每一行的「现值」列就是验收基准）。任何 HVfigure 的改动都是**行为变化**，不是 token 化。
2. `git diff` 中每一处只改属性值；不许顺带重排、不许改注释。
3. 语法有效：`TranslationPanelWindow.xaml` 被 `Program.cs:5207` 用 `XDocument.Load` 加载做结构化断言，语法错会立刻红。

**触碰「源文件路径硬编码」规则：是。** 本批改的文件里，有 7 处在测试中以源码文本方式被读取（规则来源：OPT-008 修订记 §8.2）：

| 测试读取点 | 读的文件 | 断言内容 | 是否受本切片影响 |
| --- | --- | --- | --- |
| `Program.cs:5179` | `Themes/Controls.xaml` | 图标几何 / CaptionButton 存在性 | 否 |
| `Program.cs:5208` | `TranslationPanelWindow.xaml` | `XDocument` 解析 + `Grid.Row` 结构 | 否（但语法错误会红） |
| `Program.cs:5438`、`5454` | `Themes/Controls.xaml` | 禁 Role=Separator 触发器、Separator 隐式样式存在 | 否 |
| `Program.cs:5498` | `TranslationPanelWindow.xaml`、`QuickSearchWindow.xaml` | 禁止 `AllowsTransparency` / `DropShadowEffect` | 否 |
| `WaveRegressionTests.cs:2150`、`2170` | `Themes/Controls.xaml` | 滚动条 5/12 DIP、`FocusRing` 存在 | 否 |

已全量 grep 确认：**`tests/` 下不存在任何 `"CornerRadius=\"` 字面量断言**；圆角相关断言只有 `WaveRegressionTests.cs:1594+` 的 `WorkbenchPaneCornerRadiiMatchLayout`，它从 `Application.Current.Resources["CardRadius"]` **读 token** 再与实际控件值比对 —— 因为我们不改 token 数值、也不动 `TranslateSection`，该用例天然通过。

### 2.6 字号 token 为什么本批不做

- 量级不符：76 处 / 12 个文件，远超一个可收敛切片。
- 语义族未定：`12`（23 处）里既有多用途正文又有表格元信息，`11`（18 处）同理 —— 先拿同一个 token 把它们焊死，反而比现在的硬编码更难改。
- 但也不必恐慌：现有的 7 条字号断言（`Program.cs:13882/13886/14828/14829/15022/15025/15050`）读的是**活控件**（`quickSearch.StreamBox.FontSize` 等），不是源码文本，所以「值相等即可通过」，它们是下批做字号 token 时的现成护栏。
- ⇒ 下批优先度建议：**字号 token 高于剩余圆角**（复用率最高），但必须一个语义族一个 commit。

### 2.7 验收

1. `grep -c "CornerRadius=\"[0-9]" <三个文件>` 三个文件改后应分别剩 3 / 3 / 0（只剩 §2.4 明确不碰的项）。
2. `scripts/verify.ps1` 全绿（自动覆盖上述 7 处源码文本测试）。
3. `CliTests --ui` 目视确认三个浮层在 100% / 150% DPI 下的圆角无变化（只做 Read-Only 目视，不做_tokens 自检）。

---

## 3. OPT-002R / OPT-009 现状核实

### 3.1 OPT-002R · `MainWindow.OnClosing` 两次 Load 合并为一次 → **前提全部仍然成立，可直接实施**

复核对象 `apps/PopGlot.Windows/MainWindow.xaml.cs:1071-1111`（当前 HEAD）：

| 上轮的前提 | 现状 | 结论 |
| --- | --- | --- |
| `1075` 是第一次 Load | 确认：`var closeToTray = ShellSettingsStore.Load().CloseMainWindowToTray;` | ✓ |
| `1094` 是第二次 Load | 确认：`if (!_closeHintShown && !ShellSettingsStore.Load().CloseHintShown)` | ✓ |
| 两次之间没有写设置 | 确认：之间只有 `e.Cancel = true; Hide(); if (App.TrimOnHideEnabled) App.TrimWorkingSet();`。**追加核查**：`TrimOnHideEnabled` 定义在 `App.xaml.cs:2114-2115`，只读环境变量 `POPGLOT_TRIM_ON_HIDE`，**不读设置存档**，所以连「隐藏的第三次读盘」都不存在 | ✓ 比上轮更强的确认 |
| 唯一写入在两次读取之后 | 确认：`1102` `ShellSettingsStore.Update(...)` | ✓ |
| `1075` 在 `if (!closeToTray) return;` 之前 → 合并不会新增读盘分支 | 确认：1076 才是早退 | ✓ |
| `_closeHintShown` 只共享快照，不能省 | 确认：`1094` 外层判断 + `1096` 赋值，语义分级（内存短路 vs 跨进程持久化） | ✓ 保留 |

**精确改法**（`MainWindow.xaml.cs`）：

```csharp
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            // OPT-002R: ONE snapshot for the whole method — the only write is
            // CloseHintShown below, after both reads. The in-memory
            // _closeHintShown short-circuit stays: it is per-process, while
            // settings.CloseHintShown is the cross-process switch.
            var settings = ShellSettingsStore.Load();
            var closeToTray = settings.CloseMainWindowToTray;
            if (!closeToTray)
            {
                RequestExit?.Invoke();
                return;
            }
            ...  // e.Cancel / Hide / TrimWorkingSet 保持原样
            if (!_closeHintShown && !settings.CloseHintShown)
            {
                ...
            }
        }
        base.OnClosing(e);
    }
```

**行为差异必须如实披露（1 处，极小）**：提升之后，1094 读的是 1075 那一刻的快照；原来这里会重新读一次盘。若在两次读取之间**有别的线程**通过 `ShellSettingsStore.Update` 改写了 `CloseHintShown`，旧代码有可能读到新值而不再弹气泡，新代码会再弹一次 —— 影响面是「多弹一次提示气泡」，且同一进程内的重复仍被 `_closeHintShown` 挡住。这是唯一的行为差异，我认为可以为「省掉一次读盘 + 一次 SHA256」支付。

- 风险：低｜改动量：S｜触碰源文件路径规则：**否**（只动 `OnClosing` 内部）

### 3.2 OPT-009 · HistoryStore 改用 `ReadBounded` → **本批不做（原方案不等价），降级重提**

**结论先说**：上轮方案「与 VocabularyStore 对齐，改用 `ReadBounded` + 严格 UTF-8」**不满足「corrupt/oversize 处理不能变」这条要求**，而且它**并没有解决它自己声称要解决的问题**。

核查对象 `apps/PopGlot.Windows/HistoryStore.cs:254-292`（`LoadUnlocked`）与 `apps/PopGlot.Windows/Services/VocabularyStore.cs:699-790`：

**(1) 声称的问题是 UI 线程同步读盘，但 `ReadBounded` 不改变执行线程。**
`ReadBounded`（`VocabularyStore.cs:766-790`）仍然是同步阻塞读，只是去掉了 `FileInfo` 那一次 stat。4 MiB 的上限**本来就在**（`HistoryStore.cs:262` 的 `new FileInfo(_path).Length > MaxFileBytes`），所以「无界读入」并不是当前的事实 —— 当前实现是「有界（4 MiB）+ 同步读」。换成 `ReadBounded` 后 UI 阻塞时间几乎不变，**收益接近零**。真正的开销（UI 线程阻塞）要靠「把调用挪出 UI 线程」来解决，而不是换一个读字节的函数。

**(2) 引入了真实的、且无测试覆盖的行为变化：解码严格化。**

| 场景 | 现在（`File.ReadAllText`） | 改成严格 UTF-8 后 |
| --- | --- | --- |
| UTF-8 + BOM | 自动剥离，正常读 | 需手工剥离 —— 若做过则等价 |
| **UTF-16 / UTF-32 + BOM** | BOM 探测后正常读 | **严格 UTF-8 抛 `DecoderFallbackException`** |
| 非法 UTF-8 字节 | 替补字符替换 → 大概率进 `JsonException` → quarantine | 直接 `DecoderFallbackException` |

而 `DecoderFallbackException` **不在** `HistoryStore.cs:284-285` 的 catch 白名单（`IOException or JsonException or UnauthorizedAccessException`）里 ⇒ 换实现后它会**逃出 catch**，`LoadUnlocked` 直接向外抛，行为从「妥善隔离 + 空列表」变成「未处理异常」。这是明确的行为变化，必须白名单补 `DecoderFallbackException` 才能得到「同样 quarantine」；但即使补上，「UTF-16 历史文件被当作损坏隔离」仍然是行为变化。

**(3) 而且这条路径没有测试保护。**
- 我们自己的写（`HistoryStore.cs:53,172`）恒为 **UTF-8 无 BOM**，所以自家产出的文件不受影响；
- 但项目对 BOM 变体是敏感的 —— 同为存档的 `ShellSettings` 就有 UTF-8/UTF-16 BOM 两条回归（`Program.cs:1734 / 1798`），`VocabularyStore` 也有 `utf8 bom vocabulary loads without quarantine`；
- **HistoryStore 一条 BOM 回归都没有**（已全量 grep：`istory.*[Bb]om` 零命中）。

⇒ 也就是说：改了之后，一旦有用户用编辑器把 `history.json` 存成 UTF-16，**他的整份历史会被静默隔离，且没有任何用例会红**。这与项目在 OPT-002 上沉淀的规则 2（「看起来可以省掉的校验，先假设它在偿还某次事故」）是同一类错误：**隔离旁门必须不做着色。**

**(4) 建议的下批形态（二选一，推荐 A）**

- **A（推荐，零语义风险，真正解决 UI 阻塞）**：`Sections/LibrarySection.xaml.cs:161` 的 `ReloadHistory()` 走 `Task.Run` 真正离开 UI 线程，结果回到 `Dispatcher` 再绑定。**完全不动 `HistoryStore` 的读取逻辑**，因此所有 corrupt/oversize/quarantine 语义逐字不变，也不需要新增解码测试。
- **B（保守微修，只消灭 stat-then-read 竞态，保持解码语义 100% 等价）**：保留 `ReadAllText` 的解码行为，但改成单句柄 + 单次长度判断：

  ```csharp
  if (!File.Exists(_path)) return [];                          // 缺失必须仍保持"静默空列表"，不 quarantine
  using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
  if (stream.Length > MaxFileBytes) { QuarantineUnlocked(); return []; }
  using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
  var text = reader.ReadToEnd();                               // 与 File.ReadAllText 逐字等价的解码语义
  ```
  （`File.ReadAllText` 内部就是 `StreamReader(UTF8, detectEncodingFromByteOrderMarks: true).ReadToEnd()`，所以这是**可证的等价**，不是「大概一样」。）
- **不建议**：直接照搬 `ReadBounded` + `StrictUtf8` 而不同时补齐 (a) `DecoderFallbackException` 白名单、(b) 至少一条 UTF-16-BOM 历史的隔离行为断言、(c) 一条 UTF-8-BOM 历史仍能加载的断言。若下批坚持走这条路，这三条是**入场券**，缺一不可。**另注**：`QuarantineUnlocked`（`HistoryStore.cs:294-310`）只 catch `IOException` / `UnauthorizedAccessException`，并且先做 `File.Exists` 判断 —— 所以「文件缺失」路径改坏也不会崩，但 `LastQuarantinePath` 语义要保持。

- 当前状态：**未改动**（符合上轮「下批」判定）；本批建议**维持不动**，并在清单里把 OPT-009 从「性能」重标为「性能 + 语义风险，需配套测试」。

---

## 4. 「源文件路径硬编码」规则：本批哪些项触碰

规则原文（OPT-008 修订 §8.2 / 已进项目长期记忆）：**做类移动 / 文件拆分前，先 grep 测试里对源 `.cs` 路径的字符串引用。**

当前事实（实测）：`tests/PopGlot.Windows.LogicTests/` 下共 **40 处**以 `Path.Combine(FindProjectRoot(), "apps", ...)` 形式按源码路径读生产文件 —— `Program.cs` 32 处、`WaveRegressionTests.cs` 7 处、`ThemeAuditHelper.cs` 1 处。被硬引用的生产文件包括：`App.xaml.cs`、`MainWindow.xaml.cs`、`MainWindow.xaml`、`TranslationPanelWindow.xaml.cs`、`TranslationPanelWindow.xaml`、`CaptureOverlayWindow.xaml.cs`、`SettingsWindow.xaml.cs`、`Services/RuntimeGate.cs`、`Sections/PrivacySection.xaml.cs`、`Sections/PromptSection.xaml.cs`、`ThemeService.cs`、`ClipboardSelectionService.cs`、`ScreenCaptureService.cs`、`HotkeyService.cs`、`App.xaml`、`Themes/Controls.xaml`。

| 本批项 | 是否触碰 | 说明 |
| --- | --- | --- |
| OPT-021 | **否** | 只动 `tests/` 项目，不涉及任何按路径定位的生产文件 |
| OPT-002R | **否** | 只改 `OnClosing` 内部，`MainWindow.xaml.cs` 文件本体不被搬动 |
| OPT-010（S1） | **是** | 改的是 `Themes/Controls.xaml` + 3 个 XAML 的**源文本**。已逐一核查 7 处读取点（§2.5），均断言无关属性，且 `tests/` 下无 CornerRadius 字面量断言 → 安全，但**必须重跑门禁**（已包含在 `verify.ps1`） |
| OPT-009 | 不适用（本批不做） | — |
| 下批预警 | — | **OPT-011 / OPT-012（巨型类切片）会大面积触碰这条规则**，开工前必须先跑上面的 40 处清单逐条评估，风险等级不得再按「纯移动」估 |

---

## 5. 本批建议范围与不做清单

### 做（建议全批体量 S/M）

1. **OPT-021**（§1.5 六步）— P0，质保根基
2. **OPT-002R**（§3.1）— P0，零行为变化（除已披露的一处极小差异）
3. **OPT-010 切片 S1**（§2.4 十九处）— P1

### 不做（明确写出，防止被下游夹带）

- OPT-009 原方案（改为下批 A 方案，见 §3.2）
- 字号 token（§2.6）
- `Sections/` 下的圆角（含 `TranslateSection` 的 `9` 系列分段圆角）
- `CardRadius` / `PaneRadius` 合并（数值重复的收敛需独立评估）
- 任何巨型类整体拆分 / partial 拆分（OPT-011 / 012）
- 任何新的文件移动（本批不新增 `*.cs` 文件；OPT-021 的三个字段原地加在 `Program.cs` 内，`Program` 已是 `partial class`，但我们**不利用**这一点，保持 diff 可审计）

### 建议提交顺序（依赖均为单向、可独立验证）

```
T1  OPT-021（先单独提交、单独立即验证 5 连跑）—— 它是后面所有片子的可信度前提
T2  OPT-002R（独立）
T3  OPT-010 S1（独立）
T4  全量 verify.ps1 + CHANGELOG / 文档收口
```

理由：OPT-021 修好后，「成片红＝非确定性」这种免责解释不再成立，T2/T3 的门禁结论才具备可判定性；反过来说，若 T2/T3 先跑、OPT-021 后跑，QA 无法区分两者引入的失败。

---

## 6.1 实施后复核（2026-10-29，工程师一轮实施 + QA 复核后）

> 本节记录「spec 与实际落地的差异」，与 OPT-002 的撤销记录同性质 —— 差异本身是下批的负向知识。

### 已核实落地（与 spec 一致）

| spec 要求 | HEAD 现状 | 结论 |
| --- | --- | --- |
| 唯一创建点 | 全仓 `tests/` 内 `new App {` / `new Application {` 命中 3 处，其中 LogicTests **只有 `Program.cs:10033`**（`CreateApplicationCore`） | ✓ |
| 不再用 `Application.Current` 做 once | `tests/` 内 `Application.Current is null` 命中 **0 处** | ✓ |
| `EnsureApplication` 只断言 | `Program.cs:10078-10089`：读 sticky failure → 抛或 `ThemeService.Apply(Dark)`，不创建 | ✓ |
| 引导早于第 1 个用例 | `Program.cs:62` `BootWpfApplicationOnce()`，失败即 `return 4`，位于 `TestIsolation.Initialize()` 之后、所有用例之前 | ✓ |

### 两处对 spec 的必要偏离（均为编译期强制，判断正确）

1. **去掉 `volatile` 关键字**：字段同时声明 `volatile` 又用 `Volatile.Read/Write(ref ...)` 会触发 **CS0420**（对 volatile 字段使用 ref 是警告），`TreatWarningsAsErrors` 下升级为 error。正确写法就是现在的「纯字段 + 全部走 `Volatile.*`」。**我 spec 里的伪代码写错了**，工程师的修法才是对的 —— 记下来，下批不要在别的字段上重犯。
2. **故障注入包了环境变量开关**：直接 `throw` 会导致后续代码不可达（**CS0162**），同样在 TreatWarningsAsErrors 下变 error。

### 一处必须如实记载的缺口：故障注入缝不在工作区内

团队以「注入 `CreateApplicationCore()` 必抛 → 输出恰 1 条根因 + exit 4 + 0 个用例」作为**机制证据**。我在 HEAD 全量检索（`tests/` 下 `FAIL_APP_BOOT` / `APP_BOOT` / `BOOT_FAIL` / `INJECT`，以及 `LogicTests/Program.cs` 的全部 `GetEnvironmentVariable` 命中）确认：

- `LogicTests/Program.cs` 里只有 `POPGLOT_SMOKE_FREE`（500/516）与 `POPGLOT_TESTS_FILTER`（11614）两处环境变量；
- `CreateApplicationCore`（10022-10070）体内**没有任何注入分支**，直接就是 `new App {...}`。

⇒ **该注入是一次本地临时改动，未留在工作区，第三方无法从 HEAD 复现这条证据。** 这不否定修复本身（结构性论证靠读码即可验证，见上表，且 11 次跨执行者的 MD5 一致是独立的强证据），但它意味着：

- CHANGELOG 若引用「故障注入验证」作为判据，应注明是**实施期的临时观测**，不要写成可从代码复现的常设能力；
- **下批建议补一条常设、默认关闭的注入缝**（约 3 行：`if (Environment.GetEnvironmentVariable("POPGLOT_TESTS_FAIL_APP_BOOT") == "1") throw ...;`）。它同时是我在 §1.5 STEP 1 里要求的「下一次发生要能自解释」的可回归化形态 —— 现在这条只实现了一半（错误信息带线程号/apartment 已落地，可重复的验证入口没有）。

### 顺带发现：CliTests 仍留着两份同类创建点（本批不动，下批卫生项）

- `tests/PopGlot.Windows.CliTests/Program.cs:23`（`--probe-app-construction` 早期返回分支里的 `new App`）
- `tests/PopGlot.Windows.CliTests/UiScenario.cs:20`（`new Application` 基类，资源靠手工 XmlReader 解析 `App.xaml` 拼出来）

两者在**独立进程**里，且 `UiScenario.Run` 每次 `--ui` 只被调一次（`CliTests/Program.cs:83`），所以**今天不会冲突**，不属于本批缺陷。但它们是同一个模式的第二、三份拷贝：一旦以后再加一个 UI 场景或再调一次 `UiScenario.Run`，就会长出 LogicTests 已经治好的那个毛病。**下批处理 LogicTests 之外的宿主时一并收敛**，成本约 5 行。

---

## 6.2 收口期的一次「进程残留」事故：一个被证伪的假设 + 一条自我修正（2026-10-29）

QA 收口期遇到一次「LogicTests 进程残留 → 锁住 `bin\Debug\...\PopGlot.Windows.LogicTests.exe` → 下一次 `dotnet run` 报 MSB3027/MSB3021」。现场线索：日志最后打印的是 `[Component Benchmarks]` 那一块，**之后没有任何 `[style menu]` 输出、也没有任何 PASS 行**，同时进程仍存活。

### 假设 A（已证伪）：截图阶段跑在非 harness 线程上

我最初的推测是：`RenderScreenshotsAndMeasureBaseline` 构造并渲染 ~10 个真实 WPF 窗口，若它跑在 Main 的续体（线程池 MTA）上，就会与「Application 归 harness 线程所有」冲突。

**证伪**（读码确认）：`Program.cs:346` 是 `RunStaBatch(`，第 372 行的 screenshot 用例就在其中 ⇒ 它**确实跑在 harness STA 线程上**；344-345 行的注释还专门写了「Both ride one STA thread: the windowed regression needs the Application the screenshot pass bootstraps, and Application resources are thread-affine」。

⇒ **下批不要再去查「截图阶段跑错线程」，这条路已经堵死。** 记下来省一轮排查。

### 假设 B（连带被修正）：我先前给的 `Environment.Exit` 建议覆盖不了这次事故

我先前建议把 `Main:522` 的 `return _failed == 0 ? 0 : 1;` 改成 `Environment.Exit(...)`，理由是「保证进程必然干净退出」。

**这个建议只对了一半**：QA 的证据是「输出在中途停止、且进程存活」——这说明是**卡死（hang）**，不是「Main 已返回但进程不退出（linger）」。`Main` 没走到结尾，`Environment.Exit` 就永远不会被调用。**所以这条建议能消除「退出后残留」那一类，消除不了本次这一类。**

（补充事实：`Main` 以 `return` 结束且全仓无 `Environment.Exit`；生产代码 3 处 `new Thread(` 全部 `IsBackground = true`，所以「前台线程挡退出」这条**没有证实**，不要当成已定位缺陷。）

### 下批真正该做的：看门狗而不是收尾退出

能同时覆盖「卡死」与「退出后残留」两类的最小形态是**看门狗**，不是收尾 `Environment.Exit`：

- 在 `Main` 起一个后台线程（或用一个 `Timer`），以「最后一次用例输出时间」为心跳；
- 超过阈值（例如 10 分钟无任何新输出）就打印当前已完成/未完成用例名 + `Environment.Exit(5)` 并输出一条明确的「harness watchdog timeout」；
- 顺带在正常收尾处也显式 `Environment.Exit(...)`（覆盖 linger 类）。

它比单纯的收尾退出更有价值：本次事故里没有它，我们只能靠人去看「最后一行输出是什么」再反推卡在哪；有它的话，超时那一刻会自动留下「卡在哪个用例」的证据——这正是我在 §1.5 STEP 1 里要求的「下一次发生要能自解释」，换到「卡死」这个场景上的同一形态。

配合已提的 `scripts/verify.ps1` 门禁互斥锁（`.verify.lock`），三者构成一套：撞车时脚本自己报是谁在跑、卡死时看门狗自动留证、正常收尾时必然干净退出。**三项均不进 0.1.34。**

（本次未继续深挖卡死点本身：候选空间包括 harness 线程上的 WPF 渲染/布局死锁、`SystemEvents` 监听线程、以及被测窗口内的定时器回调，都需要能复现才能定性；在 0.1.34 收口期做这个不划算。）

---

## 6. Anything UNCLEAR / 假设清单

1. **OPT-021 背离的确切触发事件未能定性**（H1/H2/H3，§1.3）。我选择不去做用例层的 PoC，因为：(a) 三者共享同一个结构性缺陷，修法相同；(b) 修复后下一次发生会自带线程/apartment 诊断，届时一次就能定性。若 team-lead 希望在合并**前**拿到确定结论，我可以再做一个带故障注入的最小 PoC（成本约半天），但**不建议阻塞本批**。
2. `WaveRegressionTests._bootstrappedApp` 删除依赖这与「`Program._bootstrappedApp` 已足够做强根」的判断 —— 两者原本是两个独立强根；收敛后只有 Program 一份，**这是刻意的**（一个 Application 只能有一个所有者），但需要在 diff review 时确认没有别的地方读 `WaveRegressionTests._bootstrappedApp`（我已 grep：只有 133/150 两行，无第三方读取）。
3. §2 的圆角分布统计口径为 `CornerRadius="<数字>"` 形式的属性；`Ui.` 附加属性或 code-behind 里设置的圆角不在统计内。若后续发现漏项，按 §2.4 的规则扩展即可。
4. OPT-002R 提升后可能「多弹一次气泡」的差异（§3.1）无法用现有用例覆盖（`MainWindow.OnClosing` 需要真实窗口生命周期），是否需要补一条集成用例由 team-lead 裁决；我的建议是**不补**，因为该差异只表现为「多弹一次提示气泡」，而每少一次 `Load()` 所省下的读盘与哈希是稳定可测的。
