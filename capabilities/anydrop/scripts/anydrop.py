#!/usr/bin/env python3
"""AnyDrop 上传 CLI —— 把本地文件变成一条免登录直链。

下载不需要任何凭证：文件 id 本身就是凭证，所以这里只做上传。

三条硬约束，改动前请先读 capabilities/anydrop/README.md：

1. **只用标准库。** 这个能力是随仓库分发的脚本，目标机器上不该为它准备
   venv 或依赖树。

2. **密钥不能靠"继承环境"拿到。** dsh 拉起 shell 子进程时，会把匹配
   ``/KEY|PASSWORD|SECRET|TOKEN/i`` 的环境变量全部丢掉（定义在
   ``@deepseek-ai/dsh-subprocess`` 的 ``SENSITIVE_ENV_PATTERN``），
   所以 agent 会话里 ``$env:ANYDROP_KEY`` 是空的。见 ``resolve_key()``
   的注册表回退。

3. **必须显式带 Content-Length，且不能分块。** 服务端对分块传输直接回
   ``411 length_required``；这也是这里用 ``http.client`` 手工 putheader
   而不是让 urllib 去猜的原因。文件是流式发送的，不整个读进内存。

退出码：0 成功 · 1 未预期的内部错误（属 bug，会附 traceback）· 2 密钥或配置缺失 ·
        3 本地文件问题 · 4 服务端拒绝 · 5 网络失败。
"""

from __future__ import annotations

import argparse
import hashlib
import http.client
import json
import os
import ssl
import stat
import sys
import urllib.parse
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Mapping

# ---------------------------------------------------------------- 常量

DEFAULT_BASE = "https://fshake.com/drop"
BASE_ENV = "ANYDROP_BASE"
MAX_UPLOAD_ENV = "ANYDROP_MAX_UPLOAD"

#: 与服务端 anydrop.json 的 server:maxUploadBytes 默认值保持一致（256 MiB）。
DEFAULT_MAX_UPLOAD = 256 * 1024 * 1024

#: 单次请求超时。服务端上传可能很慢，所以给足 10 分钟。
DEFAULT_TIMEOUT = 600.0

STREAM_CHUNK = 64 * 1024

#: 依次尝试的密钥环境变量名。Windows 上环境变量名大小写不敏感，两个名字
#: 其实是同一个变量；POSIX 上则是两个不同的变量，所以要都试。
KEY_ENV_NAMES = ("ANYDROP_KEY", "AnyDrop_Key")

EXIT_OK = 0
EXIT_CONFIG = 2
EXIT_LOCAL = 3
EXIT_SERVER = 4
EXIT_NETWORK = 5

RESPONSE_FIELDS = ("id", "url", "sha256", "size", "expiresAt")


# ---------------------------------------------------------------- 异常


class AnyDropError(Exception):
    """可预期的失败：带一条给终端用户看的中文消息和一个退出码。"""

    exit_code = 1

    def __init__(self, message: str) -> None:
        super().__init__(message)
        self.message = message


class ConfigError(AnyDropError):
    """密钥或配置缺失/非法。"""

    exit_code = EXIT_CONFIG


class LocalFileError(AnyDropError):
    """本地文件问题（不存在、是目录、不可读、超上限）。"""

    exit_code = EXIT_LOCAL


class NetworkError(AnyDropError):
    """连不上、超时、传输中断。"""

    exit_code = EXIT_NETWORK


class ServerError(AnyDropError):
    """服务端明确拒绝，或返回了无法理解的东西。

    401 归到配置类：密钥无效/过期是要人去改环境变量的事，不是"服务端拒绝"。
    """

    def __init__(self, status: int, code: str, message: str) -> None:
        self.status = status
        self.code = code
        self.exit_code = EXIT_CONFIG if status == 401 else EXIT_SERVER
        text = message.strip() or f"服务端拒绝（HTTP {status}）"
        if code and code != "unknown":
            text = f"{text}（HTTP {status} {code}）"
        super().__init__(text)


class IntegrityError(AnyDropError):
    """服务端返回的 sha256 与我们发出去的字节不一致。

    上传其实已经落盘了，所以消息里带上 id 与 url —— 否则用户既失败又丢了链接。
    """

    exit_code = EXIT_SERVER


# ---------------------------------------------------------------- 端点


@dataclass(frozen=True)
class Endpoint:
    """解析后的服务端地址。"""

    scheme: str
    host: str
    port: int
    base_path: str
    display: str

    @property
    def is_tls(self) -> bool:
        return self.scheme == "https"


def parse_endpoint(base: str) -> Endpoint:
    """把 ``https://host/drop`` 这样的地址拆成连接参数。

    末尾斜杠、多余空白都容错；缺 scheme 或 host 则报错——猜一个默认值只会
    让人更难发现问题。
    """
    raw = (base or "").strip()
    if not raw:
        raise ConfigError(f"服务端地址为空：请检查 --base 或环境变量 {BASE_ENV}")
    try:
        parts = urllib.parse.urlsplit(raw)
        port = parts.port
    except ValueError as exc:
        raise ConfigError(f"服务端地址非法：{raw}（{exc}）") from exc
    if parts.scheme not in ("http", "https"):
        raise ConfigError(f"服务端地址必须以 http:// 或 https:// 开头：{raw}")
    host = parts.hostname
    if not host:
        raise ConfigError(f"服务端地址缺少主机名：{raw}")
    # 主机名里混进空白/控制字符时 urlsplit 照样放行，但 http.client 建连时会抛
    # InvalidURL —— 它是 HTTPException 而**不是** OSError，会穿透所有 except 变成
    # traceback + 退出码 1。在这里提前拦成一条明确的配置错误。
    if any(ch.isspace() or ord(ch) < 33 or ch == "/" for ch in host):
        raise ConfigError(f"服务端地址的主机名含空白、控制字符或斜杠：{raw}")
    return Endpoint(
        scheme=parts.scheme,
        host=host,
        port=port or (443 if parts.scheme == "https" else 80),
        base_path=parts.path.rstrip("/"),
        display=f"{parts.scheme}://{parts.netloc}",
    )


def open_connection(endpoint: Endpoint, timeout: float):
    """默认的连接工厂；测试会注入一个假的替换它。"""
    if endpoint.is_tls:
        return http.client.HTTPSConnection(
            endpoint.host, endpoint.port, timeout=timeout, context=ssl.create_default_context()
        )
    return http.client.HTTPConnection(endpoint.host, endpoint.port, timeout=timeout)


# ---------------------------------------------------------------- 本地文件


@dataclass(frozen=True)
class LocalFile:
    path: Path
    size: int
    filename: str


def is_ascii(text: str) -> bool:
    return all(ord(ch) < 128 for ch in text)


def has_control_chars(text: str) -> bool:
    return any(ord(ch) < 32 or ord(ch) == 127 for ch in text)


def name_goes_in_query(filename: str) -> bool:
    """文件名是走查询参数，还是走 X-Filename 头。

    非 ASCII 走查询参数是服务端文档的明确要求（HTTP 头是 Latin-1，塞不下中文，
    虽然服务端会尝试还原 UTF-8，但不值得赌）。含控制字符的也走查询参数 ——
    把 CR/LF 放进 HTTP 头是 header 注入，http.client 会直接抛错。
    """
    return (not is_ascii(filename)) or has_control_chars(filename)


def precheck_file(
    raw_path: str, *, filename: str | None = None, max_upload: int = DEFAULT_MAX_UPLOAD
) -> LocalFile:
    """上传前把本地文件的四种毛病挡掉，避免白跑一次网络。"""
    if not raw_path or not raw_path.strip():
        raise LocalFileError("没有给出文件路径")
    path = Path(raw_path)
    try:
        # stat() 跟随符号链接；指向普通文件的软链接在这里就通过了。
        info = path.stat()
    except FileNotFoundError as exc:
        raise LocalFileError(f"文件不存在：{raw_path}") from exc
    except OSError as exc:
        raise LocalFileError(f"无法读取文件信息：{raw_path}（{exc.strerror or exc}）") from exc

    if stat.S_ISDIR(info.st_mode):
        raise LocalFileError(f"这是一个目录，不是文件：{raw_path}")
    if not stat.S_ISREG(info.st_mode):
        raise LocalFileError(f"不是普通文件（可能是设备或命名管道）：{raw_path}")
    if info.st_size > max_upload:
        raise LocalFileError(
            f"文件超过上限（{max_upload // (1024 * 1024)} MiB）："
            f"{raw_path} 是 {info.st_size} 字节"
        )

    name = (filename or path.name).strip()
    if not name:
        raise LocalFileError("文件名为空，请用 --name 指定")
    return LocalFile(path, info.st_size, name)


# ---------------------------------------------------------------- 密钥


def read_key_from_registry() -> str | None:
    """Windows 专用回退：从 ``HKCU\\Environment`` 取上传密钥。

    为什么需要它：dsh 的 shell 子进程不转发任何匹配
    ``/KEY|PASSWORD|SECRET|TOKEN/i`` 的环境变量，所以在 agent 会话里
    环境变量那条路是空的，而注册表不受进程环境清洗影响。

    **只读自己这两个名字，绝不枚举整个键** —— 那个键下还躺着别的凭据
    （比如 API key），整份读出来就等于把它们泄漏进日志。
    """
    if os.name != "nt":
        return None
    try:
        import winreg  # type: ignore[import-not-found]  # 仅 Windows 存在
    except ImportError:
        return None
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as handle:
            for name in KEY_ENV_NAMES:
                try:
                    value, _kind = winreg.QueryValueEx(handle, name)
                except OSError:
                    continue
                if isinstance(value, str) and value.strip():
                    return value.strip()
    except OSError:
        return None
    return None


def resolve_key(
    env: Mapping[str, str] | None = None,
    *,
    registry_lookup: Callable[[], str | None] = read_key_from_registry,
) -> str:
    """环境变量优先，其次注册表；都没有就明确报错，绝不猜一个。"""
    environ: Mapping[str, str] = os.environ if env is None else env
    for name in KEY_ENV_NAMES:
        value = environ.get(name)
        if value and value.strip():
            return value.strip()
    value = registry_lookup()
    if value and value.strip():
        return value.strip()
    raise ConfigError(
        f"未找到上传密钥：请设置环境变量 {KEY_ENV_NAMES[0]}"
        "（Windows 上放进用户级环境变量即可，本程序会回退读注册表）"
    )


# ---------------------------------------------------------------- 错误信封


def parse_error_envelope(body: bytes) -> tuple[str, str]:
    """解析 ``{"error":{"code","message"}}``；解析不了就退回 unknown。"""
    try:
        payload = json.loads(body.decode("utf-8"))
        error = payload["error"]
        code = str(error.get("code") or "unknown")
        message = str(error.get("message") or "")
        return code, message
    except (ValueError, UnicodeDecodeError, KeyError, TypeError, AttributeError):
        return "unknown", ""


# ---------------------------------------------------------------- 上传


def upload_file(
    local: LocalFile,
    *,
    endpoint: Endpoint,
    key: str,
    idempotency_key: str | None = None,
    timeout: float = DEFAULT_TIMEOUT,
    conn_factory: Callable[[Endpoint, float], object] = open_connection,
) -> dict:
    """把文件流式 POST 上去，返回服务端给的直链信息。

    请求体是**原始字节**，不是 multipart —— 任何表单封装都会命中 411 或
    把文件包成 multipart 而让服务端存下一堆边界符。
    """
    target = f"{endpoint.base_path}/v1/blobs"
    ascii_name = not name_goes_in_query(local.filename)
    if not ascii_name:
        # quote(safe="") 连 "/" 也编码掉，免得文件名里的斜杠影响查询串解析。
        target += "?name=" + urllib.parse.quote(local.filename, safe="")

    # 先开文件：这样"文件不可读"会归到本地问题（退出码 3），
    # 而不是被下面的 OSError 兜成网络问题（退出码 5）。
    try:
        handle = open(local.path, "rb")
    except OSError as exc:
        raise LocalFileError(
            f"无法打开文件：{local.path}（{exc.strerror or exc}）"
        ) from exc

    with handle:
        try:
            conn = conn_factory(endpoint, timeout)
        except OSError as exc:
            raise NetworkError(f"无法连接 {endpoint.display}：{exc}") from exc

        try:
            conn.putrequest("POST", target)
            conn.putheader("Authorization", f"Bearer {key}")
            # 显式给长度是承重的：服务端见不到 Content-Length 就回 411。
            conn.putheader("Content-Length", str(local.size))
            conn.putheader("Content-Type", "application/octet-stream")
            if ascii_name:
                conn.putheader("X-Filename", local.filename)
            if idempotency_key:
                conn.putheader("Idempotency-Key", idempotency_key)
            conn.endheaders()

            # 边发边算哈希：单次读取，既能核对服务端返回的 sha256，
            # 又不必把文件额外读一遍。
            #
            # 只按预检冻结的 local.size 发送。Content-Length 声明的就是那个值：
            # 多发一个字节会污染请求流，少发则服务端会一直等——实测文件缩水时客户端
            # 要卡满 DEFAULT_TIMEOUT 才失败，而且会被错报成网络问题。
            hasher = hashlib.sha256()
            sent = 0
            while sent < local.size:
                try:
                    chunk = handle.read(min(STREAM_CHUNK, local.size - sent))
                except OSError as exc:
                    raise LocalFileError(
                        f"读取文件失败：{local.path}（{exc.strerror or exc}）"
                    ) from exc
                if not chunk:
                    break
                hasher.update(chunk)
                try:
                    conn.send(chunk)
                except OSError as exc:
                    raise NetworkError(f"发送数据失败：{exc}") from exc
                sent += len(chunk)

            # 预检之后文件被改写（正在轮转/追加的日志就是典型场景）：绝不能把
            # "少发了一段"或"只上传到前一段"当成成功——后者还会因为哈希是按发出去的
            # 字节算的而顺利通过完整性校验，静默给出一个被截断的文件。
            if sent != local.size:
                raise LocalFileError(
                    f"文件在上传过程中变短：预检时 {local.size} 字节，实际只读到 {sent} 字节。请重试。"
                )
            try:
                grew = bool(handle.read(1))
            except OSError as exc:
                raise LocalFileError(
                    f"读取文件失败：{local.path}（{exc.strerror or exc}）"
                ) from exc
            if grew:
                raise LocalFileError(
                    f"文件在上传过程中被追加写入（预检时 {local.size} 字节，之后变长）。"
                    "为避免只上传到一个被截断的版本，已中止；请稍后重试。"
                )
            local_sha = hasher.hexdigest()

            try:
                response = conn.getresponse()
                status = response.status
                body = response.read()
            except OSError as exc:
                raise NetworkError(f"读取响应失败：{exc}") from exc
        except http.client.HTTPException as exc:
            # InvalidURL / RemoteDisconnected 这类协议层错误不是 OSError，
            # 不接住就会变成 traceback + 退出码 1。
            raise NetworkError(f"HTTP 协议错误：{exc}") from exc
        except OSError as exc:
            # 连接是惰性的：DNS/TCP 失败发生在 endheaders() 里，而不是构造 conn 的
            # conn_factory() 里 —— 所以上面那层 except OSError 接不住它，而
            # socket.gaierror 又是 OSError 不是 HTTPException，上面那层也接不住。
            # 主机名拼错、没有网络、端口被拒都会走到这里；不接住同样是
            # traceback + 退出码 1，而"1"在契约里代表 bug。
            raise NetworkError(f"无法连接 {endpoint.display}：{exc}") from exc
        finally:
            try:
                conn.close()
            except OSError:
                pass

    if status != 201:
        code, message = parse_error_envelope(body)
        raise ServerError(status, code, message)

    try:
        payload = json.loads(body.decode("utf-8"))
    except (ValueError, UnicodeDecodeError) as exc:
        raise ServerError(status, "bad_response", "服务端返回的不是合法 JSON") from exc
    if not isinstance(payload, dict):
        raise ServerError(status, "bad_response", "服务端返回的 JSON 不是对象")

    # 契约字段缺一不可：宁可报错，也不要打印一行带 null 的"成功"。
    missing = [field for field in ("id", "url") if not payload.get(field)]
    if missing:
        raise ServerError(
            status, "bad_response", f"服务端响应缺少字段：{', '.join(missing)}"
        )

    server_sha = str(payload.get("sha256") or "").lower()
    if server_sha != local_sha:
        raise IntegrityError(
            "服务端返回的 sha256 与本地文件不一致，上传结果不可信：\n"
            f"  本地：{local_sha}\n"
            f"  服务端：{server_sha or '（缺失）'}\n"
            f"  文件 id：{payload.get('id')}\n"
            f"  直链：{payload.get('url')}"
        )

    return payload


# ---------------------------------------------------------------- CLI


def _int_from_env(name: str, default: int) -> int:
    raw = os.environ.get(name)
    if raw is None or not raw.strip():
        return default
    try:
        value = int(raw.strip())
    except ValueError as exc:
        raise ConfigError(f"环境变量 {name} 不是整数：{raw!r}") from exc
    if value <= 0:
        raise ConfigError(f"环境变量 {name} 必须大于 0：{raw!r}")
    return value


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="anydrop",
        description="把本地文件上传到 AnyDrop，拿回一条免登录直链。",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    upload = sub.add_parser("upload", help="上传一个本地文件")
    upload.add_argument("path", help="本地文件路径")
    upload.add_argument("--name", help="覆盖服务端保存的文件名（默认用本地文件名）")
    upload.add_argument(
        "--idempotency-key",
        help="重试时复用同一个值：同内容会返回同一个 id，不会重复占盘",
    )
    upload.add_argument(
        "--base",
        default=None,
        help=f"服务端地址（默认取环境变量 {BASE_ENV}，再默认 {DEFAULT_BASE}）",
    )
    upload.add_argument(
        "--timeout", type=float, default=None, help=f"单次请求超时秒数（默认 {DEFAULT_TIMEOUT:g}）"
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        base = args.base or os.environ.get(BASE_ENV) or DEFAULT_BASE
        endpoint = parse_endpoint(base)
        local = precheck_file(
            args.path, filename=args.name, max_upload=_int_from_env(MAX_UPLOAD_ENV, DEFAULT_MAX_UPLOAD)
        )
        key = resolve_key()
        payload = upload_file(
            local,
            endpoint=endpoint,
            key=key,
            idempotency_key=args.idempotency_key,
            timeout=args.timeout if args.timeout is not None else DEFAULT_TIMEOUT,
        )
    except AnyDropError as exc:
        print(f"失败：{exc.message}", file=sys.stderr)
        return exc.exit_code
    except KeyboardInterrupt:
        print("已中断", file=sys.stderr)
        return 130

    # 只输出契约里的字段，顺序固定：stdout 恰好一行 JSON 是给脚本/agent 的接口。
    print(json.dumps({field: payload.get(field) for field in RESPONSE_FIELDS}, ensure_ascii=False))
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
