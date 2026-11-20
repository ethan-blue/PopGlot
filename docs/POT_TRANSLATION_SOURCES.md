# Pot 翻译源核查与独立适配

日期：2026-11-20。参考仓库 `pot-app/pot-desktop`，master 提交 `594d32ede96acd106b0256deaa8bb440ffcdff40`。本次由主线程直接完成，未调用子代理。

## 结论与许可边界

参考现成翻译器的接口分类、请求协议和配置体验是正确方向，但不能把源码里“有一个适配器”等同于接口今天仍可用。Pot 仓库许可为 GPL-3.0，PopGlot 当前为 MIT；本次没有复制/移植 Pot 模块，也没有引入其代码依赖，而是根据公开 HTTP 协议事实独立实现兼容请求。

## 来源盘点

| Pot 适配器 | 实际来源与鉴权 | 本轮结果 | PopGlot 处理 |
| --- | --- | --- | --- |
| Google | Google 官网 `translate.google.com/translate_a/single`，GTX 请求，无应用密钥，可自定义主机 | 固定句取得正确中文译文，单次独立请求约 346 ms | 补入同一 Google 提供方的官网节点，保留既有两台主机和长文 POST |
| Yandex | `translate.yandex.net/api/v1/tr.json/translate`；form POST，移动客户端协议标识，无密钥 | 两个不同固定句均返回正确中文，独立请求约 2968 / 2556 ms | 新增手动可选 Yandex；不自动扩展既有 Auto 的接收方集合 |
| DeepL 免费网页协议 | `www2.deepl.com/jsonrpc` | 返回“旧版客户端请升级”的多语言告知，不是原文的译文。即使其中含中文，也不能当作翻译成功 | 不纳入稳定免费线路 |
| Lingva | `lingva.pot-app.com/api/v1/...`，Pot 运营的中转站点 | 当前网络连接失败，未取得译文 | 不把别人运营的不可用站点直接作为稳定内置源 |
| 百度 | `fanyi-api.baidu.com/api/trans/vip/translate`，AppID + Secret + 请求签名 | 未取得用户百度应用凭据，未做真实翻译成功宣称 | 属于专用签名适配器，不是一个通用免费 Key；本轮未冒充已接入 |
| 有道官方 | `openapi.youdao.com/api`，应用 ID/密钥与 v3 签名 | 无用户有道应用凭据 | 与现有“有道词典”是两种接口，不混称 |
| 彩云 | `api.interpreter.caiyunai.com/v1/translator`，`x-authorization` Token，请求与响应均有数组 | 协议已核对；无可用账户 Token，不能宣称公网生成成功 | 提供显式需 Token 的可编辑 JSON 模板 |
| DeepL 官方 | 官方 API Free/Pro，Authorization 密钥；与网页匿名接口不同 | 协议已核对；无用户官方密钥 | 提供显式需 Key 的 API Free 模板，可自改 Pro 地址；模板明确自动检测原文 |
| OpenAI/其他 AI | 用户填写地址、路径、模型、密钥；支持流式和自定义 prompt/参数 | 源码中没有可继承给 PopGlot 的免费通用 AI 账号/Key | 保留已有 AI 独立页和兼容协议；不能用换一个客户端解决无效凭据 |

## 新增适配范围

- `YandexPublicTranslation`：独立构造 form POST、自动识别/中文语言映射、响应 code 与 text 双重校验；正文不写进 URL；不把简体输出冒充繁体目标。
- Yandex 复用统一授权、取消、30 秒读取预算、限流冷却、缓存与每小时公共发送预算。设置和菜单可手动选用，也可主动测速。
- 既有设置数值保持兼容，枚举仅追加。Auto 不因本次升级或一次手动成功而偷偷把文本发给新提供方。
- Google 官网节点沿用 Google 提供方身份、接收方披露、冷却粘性与长文正文 POST；不跨提供商重发。
- 自定义 API 增加 DeepL 官方、彩云模板按钮。只填表单，不保存、不发请求、不内置密钥，切换地址不继承未保存的密钥。

## 验证状态

Python 探测仅用于发现接口，随后已完成实际应用路径验证：

- Google 官网节点两个不同固定句：294 / 168 ms，各 1 次请求，均返回对应中文译文；2499 字符固定重复句样例 171 ms、1 次 POST。不是一般文章时延承诺，可能受到上游缓存影响。
- Yandex 两个不同固定句：1186 / 371 ms，各 1 次 POST，均取得对应中文译文；无用户文本或密钥写入 URL。
- 新增协议、响应校验、授权撤销、缓存、Auto 接收方隔离及模板选择零联网/零保存回归。
- 完整 `scripts/verify.ps1` 退出码 0：Rust 286、PureTests 78、LogicTests 445、CliTests --ui 32，总计 841 项通过，0 失败。
- 已重建 0.1.43.0 候选便携包；包内 EXE 真实启动，快捷键注册成功，单次托盘可用耗时 651 ms。ZIP 内 486 个文件与清单逐一 SHA-256 复验通过，另记录 411 个源码输入哈希。
- 候选包：`artifacts/review-0.1.43/PopGlot-v0.1.43-win-x64-candidate.zip`，同目录有 `.sha256`；本次未创建公开标签/Release。
- 官方 DeepL/彩云模板的公网翻译仍需有效账户凭据。模板/本地协议通过不冒充公网模型成功，原 AI 连接的证书和 401 阻断没有因参考 Pot 而自动消失。

证据：`.alma/review-043-pot-targeted.log`、`review-043-pot-verify.log`、`review-043-pot-live.log`、`review-043-pot-package.log`；实际结果 JSON：`artifacts/review-0.1.43/live-results-public-system-selected.json`。

## 原始来源

- [Pot 翻译适配器目录](https://github.com/pot-app/pot-desktop/tree/594d32ede96acd106b0256deaa8bb440ffcdff40/src/services/translate)
- [Google 适配器](https://github.com/pot-app/pot-desktop/blob/594d32ede96acd106b0256deaa8bb440ffcdff40/src/services/translate/google/index.jsx)
- [Yandex 适配器](https://github.com/pot-app/pot-desktop/blob/594d32ede96acd106b0256deaa8bb440ffcdff40/src/services/translate/yandex/index.jsx)
- [DeepL 三种接入](https://github.com/pot-app/pot-desktop/blob/594d32ede96acd106b0256deaa8bb440ffcdff40/src/services/translate/deepl/index.jsx)
- [彩云协议](https://github.com/pot-app/pot-desktop/blob/594d32ede96acd106b0256deaa8bb440ffcdff40/src/services/translate/caiyun/index.jsx)
