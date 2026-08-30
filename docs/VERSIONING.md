# PopGlot 版本规则

> 生效日期：2026-08-30。当前版本：**0.0.2**。

## 历史修正

仓库曾错误地打过一个 `v0.3.0` tag（指向 `c8ac688`）。该编号是凭空跳跃出来的，
不符合任何已发布的版本序列，已于 2026-08-29 撤回（本地与远端 tag 均已删除），
`0.3.x` 编号永不复用。版本序列自此从 **0.0.1** 重新开始，之后只做增量递增。

## 版本格式

`MAJOR.MINOR.PATCH`，例如 `0.0.2`。

| 段 | 何时递增 | 示例 |
|----|----------|------|
| PATCH（0.0.x） | 缺陷修复、视觉微调、小改进。每次发布 +1，**绝不跳跃** | 0.0.1 → 0.0.2 |
| MINOR（0.x.0） | 大功能批次：信息架构调整、新子系统（如模型目录、新翻译线路）、配置 schema 变更 | 0.0.x → 0.1.0 |
| MAJOR（1.0.0） | 正式公开发布承诺（稳定性契约、安装包分发、自动更新）。当前不设时间表 | 0.x → 1.0.0 |

规则：

1. **只递增，不回退、不跳跃、不重排**。每个已打 tag 的编号永久占用。
2. 0.x 阶段允许配置 schema 不兼容变更，但必须附带自动迁移
   （如 product-config v4→v5、v5→v6）与 CHANGELOG 中的迁移说明。
3. 一次提交可以同时包含多个 patch 的累积改动，但发布时只占用下一个编号。

## 单一事实来源与四方发布门禁

- 桌面产品版本号单一事实来源（SSOT）：`apps/PopGlot.Windows/PopGlot.Windows.csproj` 的 `<Version>`。
  它自动生成 Assembly/File/Informational 版本，`dotnet publish` 的产物元数据随之更新。
- Rust Workspace 同步要求：`Cargo.toml` 的 `[workspace.package] version` 也必须严格同步保持一致。所有子 crate 均继承该版本。
- 四方发布门禁（Release Gate）：在 `.github/workflows/release.yml` 与发布前检查中，强制验证以下四方完全一致，任意不一致直接阻断发布：
  1. Git Release Tag（`vX.Y.Z`）
  2. `apps/PopGlot.Windows/PopGlot.Windows.csproj`（`<Version>`）
  3. `Cargo.toml`（`[workspace.package] version`）
  4. `CHANGELOG.md`（顶部最新版本条目）

## 发布清单

1. 更新 `apps/PopGlot.Windows/PopGlot.Windows.csproj` 的 `<Version>`。
2. 同步更新 `Cargo.toml` 的 `[workspace.package] version`。
3. 在 `CHANGELOG.md` 顶部新增对应版本条目（新增/变更/修复/迁移/风险）。
4. 全量验证：运行 `scripts/verify.ps1`（包含 `cargo fmt`、`cargo test`、`cargo clippy`、`dotnet build`、68 项 Windows 逻辑测试全部通过）。
5. 运行四方版本一致性本地检查，确保 Release Tag、csproj、Cargo.toml 与 CHANGELOG.md 版本一致。
6. 构建发布产物并生成校验和：
   ```bash
   dotnet publish apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -r win-x64 \
     --self-contained false -o dist/release
   cp target/release/popglot_ffi.dll dist/release/
   ```
7. 提交所有改动，打 annotated tag：
   ```bash
   git tag -a v0.0.2 -m "PopGlot 0.0.2"
   ```
8. 推送（需要远端时）：`git push origin main --tags`。
   GitHub Actions release 工作流将自动校验四方版本、打包并附带 `.zip.sha256` 校验和。
