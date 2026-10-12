# 0.1.17 账号直连交付记录

日期：2026-10-12。前置 0.1.16 已通过 GitHub Release 工作流并上传便携 ZIP 与 SHA256：[下载页](https://github.com/ethan-blue/PopGlot/releases/tag/v0.1.16)。0.1.17 从后续工作目录回收修复 27760d1 继续，保留已经发布的 CLI 功能。

## 当前实现

- **OMP 普通 API Key → 原生 HTTP**：连接 OMP、选择模型，确认导入后保存到 Windows 凭据库。只查询指定 provider、credential_type=api_key、未禁用的记录；OAuth 记录不读取。数据库只读打开，不迁移或改写 OMP 数据。支持 OpenAI、DeepSeek、zhipu-coding-plan 的已知端点；环境变量只作为选定提供方的 Key 回退。多个有效 Key 拒绝猜选。导入生成独立 API 配置，保留原 CLI 配置，后续使用原生请求和连接池。
- **ChatGPT 官方授权 → 公开 Responses HTTP**：官方开源本机应用的动态注册入口 dynamic_agent_client，应用名称 PopGlot，稳定匿名 host ID。系统浏览器授权，回调使用持有端口的 127.0.0.1 TCP 监听器；验证 state、PKCE、nonce、RS256/JWKS 签名、issuer、audience、到期时间和 subject；仅在直连 scope 获准后保存。返回的 issued client_id 与账号绑定，重登录不允许换绑。
- **账号隔离和生命周期**：每个注册分别保存基本信息与 DPAPI 当前用户加密凭据；刷新按注册串行，持久化轮换后的 refresh_token，使用非秘密修订号防止旧刷新覆盖新登录。退出取消该账号的作业、尝试撤销官方会话、清除本机 token，保留注册映射和 host ID。其他账号不受影响。
- **统一路线**：新 chatgpt-direct 执行类型有独立账号/模型字段；不借用 API Key 槽位，不走 CLI，不把默认原生设置偷偷回退为另一引擎。主翻译、要点和 OCR 后文字使用同一 FFI 文本作业与提示词。直连报告实际 HTTP 状态、阶段计时；CLI 仍使用独立诊断，不伪造 HTTP 200。
- **取消和网络开关**：启动、提交和完成前检查策略，运行中周期检查；断开取消正在执行及已排队的请求。已交给远端的请求无法保证停止计费。网络禁用不发 token/model/inference 请求；本机退出即使撤销失败也清除本机凭据。

## 文件入口

| 文件（apps/PopGlot.Windows/ 下） | 职责 |
| --- | --- |
| Services/AiConnections/ChatGptOAuth.cs | 官方授权、回调、身份校验、刷新和退出 |
| Services/AiConnections/ChatGptAccountStore.cs | 独立注册、稳定主机标识、原子写入和 DPAPI 凭据 |
| Services/AiConnections/ChatGptDirectBackend.cs、DirectHttp.cs | 固定公开 HTTP 端点、连接复用、响应上限、流式终态 |
| Services/AiConnections/OmpApiKeyImporter.cs | 确认后的指定提供方 API Key 导入 |
| Services/AiConnections/CliTranslationExecutor.cs | 共用 FFI 文本作业和取消；按类型选择 CLI 或直连后端 |
| Services/ProfileManager.cs、ProfileVerification.cs | 配置、切换、凭据/验证身份和原生路线防误用 |
| Services/TranslationCoordinator.cs、RouteCapabilityService.cs | 主翻译、要点与 OCR 后文字派发及能力说明 |
| Sections/ServicesSection.DirectConnections.cs、ServicesSection.xaml | 登录、模型、测试、保存、断开、退出、导入确认 |

## 本机证据

| 检查 | 结果 | 证据文件 |
| --- | --- | --- |
| CLI/直连授权/FFI/路线/界面与真实导入 | **19 通过，0 失败** | artifacts/cli-repair/v17-final-tests.log |
| 纯逻辑 | **47 通过，0 失败** | artifacts/cli-repair/v17-pure.log |
| 全量 Windows 逻辑 | **318 通过，0 失败** | artifacts/cli-repair/v17-logic-full.log |
| 实际导入后翻译 | **通过**，zhipu-coding-plan / glm-4.7，executor=UserProvider | 固定 Hello world，首文字 799ms；本机隔离数据目录与内存测试凭据库，OMP 源数据只读。 |
| 实际 WPF 控件操作 | **通过**，账号模型/测试/保存切换/断开 | artifacts/cli-repair/direct-panel.png；使用模拟账号与服务，不是真实账号截图。 |
| 真实 ChatGPT 浏览器授权、计划资格与推理 | **未验证** | 用户须在官方页面亲自完成身份与授权；未把模拟令牌称为真实登录。 |
| 旧机器和完整截屏取图/OCR | **未验证** | OCR 后文字路线已接入；本次不声称整个截图操作实机通过。 |

799ms 是一次短句样本，不能与昨天的其他服务响应直接作为受控性能对照；原生 GLM 翻译路线已关闭可见思考，与 CLI 推理配置也不同。能确认的是此请求经过原生 HTTP，翻译时没有 OMP 启动/会话准备开销。

全量检查起初与另一个测试共享 WPF obj 输出，发生中间文件竞争；串行重新执行后 318 项全部通过。最终发布仍须 GitHub 独立环境的全套门禁，不以软件渲染截图或旧 DONE 文档代替。

## 使用与失败处理

1. 设置 → 翻译引擎 → 连接本机工具，顶部选择 Continue with ChatGPT，在官方浏览器中授权后返回；刷新模型、测试翻译、保存并使用直连。
2. 使用 OMP API Key：先连接 OMP、选择支持的提供方模型，勾选导入说明，再点「导入所选 OMP 模型的 API 直连」。新 API 引擎尚未测试，需在引擎列表中测试。
3. 多 Key、OAuth、远程 broker、自定义配置不自动猜选；保留 CLI 或手工添加 API 引擎。导入后的 Key 是独立副本，OMP 日后更换 Key 不会自动同步，用户应重新导入或编辑 API 引擎。
4. ChatGPT 未给计划直连 scope、模型/额度不允许或官方服务拒绝时明确失败，不使用 Codex 的现有 token 来绕过。可切回已有 CLI/API 引擎。
5. 断开只禁用引擎，账号保留；退出账号清除 PopGlot 的 token，不登出 Codex 或 OMP。若官方撤销失败，应在 ChatGPT 管理页面撤销相应授权。

## 可复测命令

```powershell
dotnet run --project tests/PopGlot.Windows.CliTests -c Release -- --ui
# 此命令只对已确认支持的 OMP 智谱普通 API Key 做固定测试句，使用其额度：
dotnet run --project tests/PopGlot.Windows.CliTests -c Release -- --live-import
dotnet run --project tests/PopGlot.Windows.PureTests -c Release
dotnet run --project tests/PopGlot.Windows.LogicTests -c Release
./scripts/publish-package.ps1
```

不要并行构建引用同一 WPF 项目的多个测试；全量 LogicTests 的实例检查不得绕过，也不得为测试结束用户正在运行的 PopGlot。

## 官方接口依据

- [动态注册与登录](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)
- [公开 Responses 模型与推理](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
- [账号、刷新、退出](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions)
- [OpenID 配置与签名密钥地址](https://auth.openai.com/.well-known/openid-configuration)
- [OMP 18.2.11 普通 API Key 存储格式](https://github.com/can1357/oh-my-pi/blob/v18.2.11/packages/ai/src/auth/sqlite-credential-store.ts)

本版未收集或迁移 Codex/Claude Code 订阅 token。支持的普通 API Key 导入是界面明确确认后的独立操作，不属于离线入口检测。
