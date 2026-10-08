# AnyDrop AI 能力

把本地文件变成一条免登录直链的 agent 能力。一个目录装两样东西：

```
SKILL.md            给模型看的「何时用、怎么用、边界在哪」
scripts/anydrop.py  真正干活的 CLI（纯标准库）
scripts/tests/      它的测试
```

## 为什么是 CLI 而不是 MCP

这是**有意推迟 MCP 之后的形态**。当时的判断：

- 这个能力只有**一个**动作（上传一个文件、拿回一条链接），为它引官方 `mcp` SDK 要拉进
  ~15 个包（含 `pydantic-core`、`cryptography`、`pywin32` 三个原生编译件和一整套
  HTTP 服务器栈），而本项目 README 的卖点恰恰是"不依赖 Docker、外部数据库、云端对象存储"。
- **CLI 在任何有 shell 的 agent 里都能用**（DSH、Claude Code、Codex、Cursor……），
  而 MCP 要在每个工具里各配一遍。
- MCP 唯一不可替代的优势是"配置里的 `env:` 能绕过凭证清洗"，以及原生工具schema；
  这两点等真正需要时再补不迟——届时在 `scripts/` 旁加一个薄包装调用**同一份 core**即可。

## 用法

```bash
python capabilities/anydrop/scripts/anydrop.py upload <文件路径> [--name N] [--idempotency-key K] [--base URL] [--timeout 秒]
```

- 成功：stdout **恰好一行** JSON —— `{id,url,sha256,size,expiresAt}`；退出码 `0`。
- 失败：stdout 为空，原因写 stderr；退出码 `1` 未预期的内部错误（属 bug，附 traceback）·
  `2` 配置 · `3` 本地文件 · `4` 服务端拒绝 · `5` 网络。

环境变量：`ANYDROP_BASE`（端点，默认 `https://fshake.com/drop`）·
`ANYDROP_MAX_UPLOAD`（本地预检上限，默认 256 MiB）。

## 密钥从哪来

按顺序找：

1. 环境变量 `ANYDROP_KEY` → `AnyDrop_Key`（Windows 上环境变量名大小写不敏感，两个名字是同一个变量）；
2. 回退读 Windows 注册表 `HKCU\Environment` 下的同名值。

**第 2 条是必须的，不是保险。** harness 在拉起 shell 子进程时会统一清洗环境变量：

```js
// @deepseek-ai/dsh-subprocess
const SENSITIVE_ENV_PATTERN = /KEY|PASSWORD|SECRET|TOKEN/i;
```

凡是命中这个模式的名字**一律不转发**给子进程（目的是不让 harness 自己的 API key
隐式泄漏给任意子进程），而 `ANYDROP_KEY` 和 `AnyDrop_Key` 都命中。所以在 agent
会话里 `$env:ANYDROP_KEY` 是**空的**，只有注册表这条不受进程环境清洗影响的路能走通。

读取时**只取自己这两个名字，绝不枚举整个键**——那个键下还躺着别的凭据，整份读出来
就等于把它们泄漏进日志。`read_key_from_registry()` 的注释里写了这一点，别改。

> ⚠️ 注册表回退是 **Windows 专有**。Linux/macOS 上、且是在 harness 内调用时，
> 需要另加密钥文件回退（尚未实现，见「已知局限」）。

## 设计里几条承重约束

| 约束 | 为什么 |
|---|---|
| 只用标准库 | 随仓库分发，目标机器不该为它准备 venv |
| `http.client` 手工 `putheader`，不用 `urllib` | 必须**显式带 Content-Length**；服务端对分块传输直接回 411 |
| 流式发送 + 边发边算 sha256 | 上限 256 MiB，不能整个读进内存；单次读取同时完成发送与校验 |
| 非 ASCII 或**含控制字符**的文件名走 `?name=` | HTTP 头是 Latin-1；把 CR/LF 放进头就是 header 注入 |
| 服务端返回的 sha256 与本地不符时报错**并附上 id/直链** | 上传其实已经落盘了，只报失败会让用户既失败又丢链接 |

## 测试

```bash
python capabilities/anydrop/scripts/tests/test_anydrop.py
```

零依赖（标准库 `unittest` + 一个 stdlib `http.server` 假服务）。临时文件写在仓库内
`.local-dev/python-tests/`（已 gitignore），与 `tests/AnyDrop.Tests/TestPaths.cs` 的做法一致
——不用系统临时目录。

覆盖：正常流程 · 异常流程（各错误码与断线）· 边界（0 字节、恰好上限、超限）·
极端输入（中文/emoji/换行/路径穿越式文件名、超长幂等键）· 核心逻辑（端点解析、
密钥优先级、信封解析）· 真实 HTTP 交互（Content-Length、无分块、字节一致）·
CLI 契约（stdout 一行、退出码、密钥不落输出）。

## 将来要接 MCP 时

在 `scripts/` 旁加 `mcp/server.py`，绕同一份 core 包一层即可。配置片段（DSH 的
profile patch，**当前刻意未启用**）：

```yaml
- id: mcp-anydrop
  name: '@deepseek-ai/dsh-mcp-client'
  config:
    serverName: anydrop
    transport: stdio
    command: python
    args: ['C:\\Running\\AnyDrop\\capabilities\\anydrop\\mcp\\server.py']
    # 承重：stdio 子进程的环境同样被清洗，密钥必须显式传，否则会得到 401
    env:
      ANYDROP_KEY: !!js process.env.ANYDROP_KEY
    # 默认 60 秒对 256 MiB 上传太小
    toolCallTimeoutMs: 600000
```

注意：`mcp` SDK 已进入 v2，高层类是 `MCPServer`（`from mcp.server import MCPServer`），
`from mcp.server.fastmcp import FastMCP` 在 v2 里是只抛 `ModuleNotFoundError` 的墓碑模块。

## 启用这个 skill

它**不会**被自动加载。要启用，把本目录的父目录加进 agent 的技能根即可——本目录根上就是
`SKILL.md`，本身就是一个合法的 skill bundle：

- DSH：`customSkillDirs: ["<仓库>/capabilities"]`（或复制/链接到 `~/.dsh/skills`）
- Claude Code：`~/.claude/skills/` 或项目的 `.claude/skills/`

## 已知局限

- 注册表回退仅 Windows；其他平台在 harness 内调用会以退出码 2 失败（需要加密钥文件回退）。
- 不做断点续传、不做自动重试（幂等键语义除外）——服务端也不支持。
- 不做删除/改期：服务端**没有**面向密钥的此类接口，只能管理员在面板操作。
- MCP 形态尚未实现（见上）。
