#!/usr/bin/env python3
"""anydrop.py 的测试。

运行：
    python capabilities/anydrop/scripts/tests/test_anydrop.py

零依赖：只用标准库 unittest 与一个 stdlib http.server 假服务。

临时文件放在仓库内的 ``.local-dev/python-tests/``（已 gitignore），与
``tests/AnyDrop.Tests/TestPaths.cs`` 的做法一致 —— 不用系统临时目录。
"""

from __future__ import annotations

import contextlib
import hashlib
import http.server
import inspect
import io
import json
import os
import shutil
import socket
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from unittest import mock

SCRIPTS_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS_DIR))

import anydrop  # noqa: E402  （必须在 sys.path 调整之后导入）


def repo_root() -> Path | None:
    """向上找到含 .git 的仓库根；不是在仓库里跑就返回 None。"""
    for candidate in Path(__file__).resolve().parents:
        if (candidate / ".git").exists():
            return candidate
    return None


def scratch_root() -> Path:
    """测试临时目录的根。

    优先放进仓库内的 .local-dev/（已 gitignore）——理由与
    tests/AnyDrop.Tests/TestPaths.cs 一致：本机沙箱下系统临时目录不可写。

    找不到 .git 时（这份 skill 被复制到 ~/.dsh/skills/ 单独使用就是这种情况）
    **退回能力目录自身**：无论装在哪里，本文件都在 ``<能力目录>/scripts/tests/`` 下，
    所以 parents[2] 恒为能力目录。绝不落到用户 home 或配置目录里去建目录。
    """
    root = repo_root()
    base = root if root is not None else Path(__file__).resolve().parents[2]
    target = base / ".local-dev" / "python-tests"
    target.mkdir(parents=True, exist_ok=True)
    return target


class ScratchCase(unittest.TestCase):
    """要建临时目录的用例都继承它：目录用完即删，不留失败现场。"""

    def scratch(self) -> Path:
        path = Path(tempfile.mkdtemp(dir=scratch_root(), prefix="case-"))
        self.addCleanup(shutil.rmtree, path, ignore_errors=True)
        return path


# ------------------------------------------------------------------ 假连接


class FakeResponse:
    def __init__(self, status: int, body: bytes) -> None:
        self.status = status
        self._body = body

    def read(self) -> bytes:
        return self._body


class FakeConnection:
    """记录请求、返回预置响应；用来断言我们发出的请求形状。"""

    def __init__(self, status: int = 201, body: bytes = b"{}", raise_on_send: OSError | None = None):
        self.status = status
        self.body = body
        self.raise_on_send = raise_on_send
        self.request_line: tuple[str, str] | None = None
        self.headers: list[tuple[str, str]] = []
        self.sent = b""
        self.ended = False
        self.closed = False

    def putrequest(self, method: str, target: str, **kwargs) -> None:
        self.request_line = (method, target)

    def putheader(self, name: str, value: str) -> None:
        self.headers.append((name, value))

    def endheaders(self) -> None:
        self.ended = True

    def send(self, data: bytes) -> None:
        if self.raise_on_send is not None:
            raise self.raise_on_send
        self.sent += data

    def getresponse(self) -> FakeResponse:
        return FakeResponse(self.status, self.body)

    def close(self) -> None:
        self.closed = True

    def header(self, name: str) -> str | None:
        for key, value in self.headers:
            if key.lower() == name.lower():
                return value
        return None


def ok_payload(sha: str, size: int) -> bytes:
    return json.dumps(
        {
            "id": "K7F3ABCDEFGHIJKLMNOPQRSTUV",
            "url": f"https://fshake.com/drop/v1/blobs/K7F3ABCDEFGHIJKLMNOPQRSTUV",
            "sha256": sha,
            "size": size,
            "expiresAt": "2026-11-05T07:00:00Z",
        }
    ).encode()


def fake_factory(conn: FakeConnection):
    return lambda endpoint, timeout: conn


def digest(value: str | None) -> str:
    """只用于比较密钥是否一致。

    这条用例读的是**真实上传密钥**；unittest 在断言失败时会把两边的值整个打进
    输出，那等于把密钥写进日志。所以比较摘要而不是原值 —— 强度不变，失败也不泄漏。
    """
    return hashlib.sha256(b"<none>" if value is None else value.encode("utf-8")).hexdigest()


# ------------------------------------------------------------------ 假服务


class RecordingHandler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_POST(self) -> None:  # noqa: N802 （http.server 的约定命名）
        server = self.server
        length = self.headers.get("Content-Length")
        server.observed.append(
            {
                "path": self.path,
                "content_length": length,
                "transfer_encoding": self.headers.get("Transfer-Encoding"),
                "authorization": self.headers.get("Authorization"),
                "x_filename": self.headers.get("X-Filename"),
                "idempotency_key": self.headers.get("Idempotency-Key"),
            }
        )
        size = int(length) if length else 0
        body = self.rfile.read(size) if size else b""
        server.body = body
        server.sha = hashlib.sha256(body).hexdigest()
        status, payload = server.responder(server)
        data = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *args) -> None:  # 别往 stderr 打字
        return


class LocalServer:
    """一个真实的本地 AnyDrop 替身，用来验证请求确实按 HTTP 语义发出。"""

    def __init__(self) -> None:
        self.httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), RecordingHandler)
        self.httpd.daemon_threads = True
        self.httpd.observed = []
        self.httpd.body = b""
        self.httpd.sha = ""
        self.httpd.responder = lambda srv: (
            201,
            {
                "id": "K7F3ABCDEFGHIJKLMNOPQRSTUV",
                "url": "https://fshake.com/drop/v1/blobs/K7F3ABCDEFGHIJKLMNOPQRSTUV",
                "sha256": srv.sha,
                "size": len(srv.body),
                "expiresAt": "2026-11-05T07:00:00Z",
            },
        )
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        self.thread.start()

    @property
    def base(self) -> str:
        return f"http://127.0.0.1:{self.httpd.server_address[1]}/drop"

    @property
    def observed(self) -> list[dict]:
        return self.httpd.observed

    def close(self) -> None:
        self.httpd.shutdown()
        self.httpd.server_close()
        self.thread.join(timeout=5)


# ------------------------------------------------------------------ 1 正常流程


class TestHappyPath(ScratchCase):
    def test_上传成功返回服务端载荷(self):
        data = b"hello anydrop \xe4\xbd\xa0\xe5\xa5\xbd"
        conn = FakeConnection(201, ok_payload(hashlib.sha256(data).hexdigest(), len(data)))
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "report.md"
            path.write_bytes(data)
            local = anydrop.precheck_file(str(path))
            result = anydrop.upload_file(
                local,
                endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
                key="secret-key",
                conn_factory=fake_factory(conn),
            )
        self.assertEqual("K7F3ABCDEFGHIJKLMNOPQRSTUV", result["id"])
        self.assertTrue(result["url"].endswith("/v1/blobs/K7F3ABCDEFGHIJKLMNOPQRSTUV"))
        self.assertEqual(hashlib.sha256(data).hexdigest(), result["sha256"])
        self.assertEqual(len(data), result["size"])

    def test_上传发出的是原始字节而不是_multipart(self):
        data = b"\x00\x01\x02binary"
        conn = FakeConnection(201, ok_payload(hashlib.sha256(data).hexdigest(), len(data)))
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "blob.bin"
            path.write_bytes(data)
            anydrop.upload_file(
                anydrop.precheck_file(str(path)),
                endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
                key="k",
                conn_factory=fake_factory(conn),
            )
        self.assertEqual(data, conn.sent)
        self.assertNotIn("multipart", conn.header("Content-Type") or "")

    def test_请求方法是_POST_且打到_v1_blobs(self):
        conn = FakeConnection(201, ok_payload(hashlib.sha256(b"x").hexdigest(), 1))
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "a.txt"
            path.write_bytes(b"x")
            anydrop.upload_file(
                anydrop.precheck_file(str(path)),
                endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
                key="k",
                conn_factory=fake_factory(conn),
            )
        self.assertEqual(("POST", "/drop/v1/blobs"), conn.request_line)

    def test_连接用完即关(self):
        conn = FakeConnection(201, ok_payload(hashlib.sha256(b"x").hexdigest(), 1))
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "a.txt"
            path.write_bytes(b"x")
            anydrop.upload_file(
                anydrop.precheck_file(str(path)),
                endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
                key="k",
                conn_factory=fake_factory(conn),
            )
        self.assertTrue(conn.closed)


# ------------------------------------------------------------------ 2 异常流程


class TestFailurePaths(ScratchCase):
    def _upload_with(self, status: int, body: bytes, key: str = "k"):
        conn = FakeConnection(status, body)
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        return anydrop.upload_file(
            anydrop.precheck_file(str(path)),
            endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
            key=key,
            conn_factory=fake_factory(conn),
        )

    def test_401_归到配置类退出码(self):
        with self.assertRaises(anydrop.ServerError) as ctx:
            self._upload_with(401, b'{"error":{"code":"invalid_token","message":"\xe5\xaf\x86\xe9\x92\xa5\xe6\x97\xa0\xe6\x95\x88"}}')
        self.assertEqual(anydrop.EXIT_CONFIG, ctx.exception.exit_code)
        self.assertEqual(401, ctx.exception.status)

    def test_413_归到服务端退出码且保留中文文案(self):
        with self.assertRaises(anydrop.ServerError) as ctx:
            self._upload_with(413, '{"error":{"code":"file_too_large","message":"文件超过上限"}}'.encode())
        self.assertEqual(anydrop.EXIT_SERVER, ctx.exception.exit_code)
        self.assertIn("文件超过上限", ctx.exception.message)

    def test_429_与_507_都不重试只报错(self):
        for status, code in ((429, "rate_limited"), (507, "quota_exceeded")):
            with self.subTest(status=status):
                with self.assertRaises(anydrop.ServerError) as ctx:
                    self._upload_with(status, f'{{"error":{{"code":"{code}","message":"拒绝"}}}}'.encode())
                self.assertEqual(anydrop.EXIT_SERVER, ctx.exception.exit_code)

    def test_服务端返回非_json_时不假装成功(self):
        with self.assertRaises(anydrop.ServerError) as ctx:
            self._upload_with(201, b"<html>502 Bad Gateway</html>")
        self.assertEqual("bad_response", ctx.exception.code)

    def test_响应缺少_id_或_url_时报错(self):
        with self.assertRaises(anydrop.ServerError) as ctx:
            self._upload_with(201, b'{"sha256":"abc"}')
        self.assertIn("id", ctx.exception.message)

    def test_sha256_不一致时报错且附上直链(self):
        with self.assertRaises(anydrop.IntegrityError) as ctx:
            self._upload_with(201, ok_payload("deadbeef", 1))
        # 上传其实已经落盘了：不许只报失败而不给 id/直链
        self.assertIn("K7F3ABCDEFGHIJKLMNOPQRSTUV", ctx.exception.message)
        self.assertIn("https://fshake.com/drop/v1/blobs/", ctx.exception.message)
        self.assertEqual(anydrop.EXIT_SERVER, ctx.exception.exit_code)

    def test_读取响应时断线归为网络问题(self):
        conn = FakeConnection(201, b"{}")

        def broken_getresponse():
            raise OSError("connection reset")

        conn.getresponse = broken_getresponse  # type: ignore[method-assign]
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "a.txt"
            path.write_bytes(b"x")
            with self.assertRaises(anydrop.NetworkError):
                anydrop.upload_file(
                    anydrop.precheck_file(str(path)),
                    endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
                    key="k",
                    conn_factory=fake_factory(conn),
                )

    def test_发送中断归为网络问题(self):
        conn = FakeConnection(201, b"{}", raise_on_send=OSError("broken pipe"))
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "a.txt"
            path.write_bytes(b"xxx")
            with self.assertRaises(anydrop.NetworkError):
                anydrop.upload_file(
                    anydrop.precheck_file(str(path)),
                    endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
                    key="k",
                    conn_factory=fake_factory(conn),
                )

    def test_连接失败归为网络问题(self):
        def boom(endpoint, timeout):
            raise OSError("connection refused")

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "a.txt"
            path.write_bytes(b"x")
            with self.assertRaises(anydrop.NetworkError) as ctx:
                anydrop.upload_file(
                    anydrop.precheck_file(str(path)),
                    endpoint=anydrop.parse_endpoint("http://127.0.0.1:9/drop"),
                    key="k",
                    conn_factory=boom,
                )
        self.assertEqual(anydrop.EXIT_NETWORK, ctx.exception.exit_code)

    def test_连接阶段_dns_失败归为网络问题而不是未捕获异常(self):
        # 连接是惰性的：真实 DNS 失败发生在 endheaders()，不在 conn_factory()，
        # 所以只有 conn_factory 外面那层 except OSError 接不住它；
        # socket.gaierror 又是 OSError 而不是 HTTPException，HTTPException 那层也接不住。
        # 这里用注入的方式精确复现"endheaders() 抛 OSError"这一条路径。
        conn = FakeConnection(201, b"{}")

        def dns_failure():
            raise socket.gaierror(11001, "getaddrinfo failed")

        conn.endheaders = dns_failure  # type: ignore[method-assign]
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        with self.assertRaises(anydrop.NetworkError) as ctx:
            anydrop.upload_file(
                anydrop.precheck_file(str(path)),
                endpoint=anydrop.parse_endpoint("https://nonexistent-host.invalid/drop"),
                key="k",
                conn_factory=fake_factory(conn),
            )
        self.assertEqual(anydrop.EXIT_NETWORK, ctx.exception.exit_code)

    def test_本地文件打不开时归为本地问题而不是网络问题(self):
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        local = anydrop.precheck_file(str(path))
        path.unlink()  # 预检之后文件消失：模拟竞态
        with self.assertRaises(anydrop.LocalFileError):
            anydrop.upload_file(
                local,
                endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
                key="k",
                conn_factory=fake_factory(FakeConnection()),
            )

    def test_文件在预检后变短时报本地错误而不是等满超时(self):
        # 日志轮转/截断就是这种场景。不检测的话会按 Content-Length 声明 100 却只发 10，
        # 服务端一直等 → 客户端卡满 DEFAULT_TIMEOUT，而且会被错报成网络问题。
        tmp = self.scratch()
        path = tmp / "shrink.bin"
        path.write_bytes(b"A" * 100)
        local = anydrop.precheck_file(str(path))
        path.write_bytes(b"A" * 10)
        conn = FakeConnection(201, ok_payload("x", 10))
        with self.assertRaises(anydrop.LocalFileError) as ctx:
            anydrop.upload_file(
                local,
                endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
                key="k",
                conn_factory=fake_factory(conn),
            )
        self.assertEqual(anydrop.EXIT_LOCAL, ctx.exception.exit_code)
        self.assertIn("变短", ctx.exception.message)
        self.assertEqual(10, len(conn.sent), "只应发出实际读到的字节")

    def test_文件在预检后变长时会中止以免上传截断版本(self):
        # 只发预检时的那 10 字节本身是一个"合法"请求，服务端返回的 sha256
        # 正是这 10 字节的哈希 —— 完整性校验会通过。所以必须主动发现文件变长了，
        # 否则会静默交付一个被截断的版本。
        tmp = self.scratch()
        path = tmp / "grow.bin"
        path.write_bytes(b"A" * 10)
        local = anydrop.precheck_file(str(path))
        path.write_bytes(b"A" * 20)
        conn = FakeConnection(201, ok_payload("x", 10))
        with self.assertRaises(anydrop.LocalFileError) as ctx:
            anydrop.upload_file(
                local,
                endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
                key="k",
                conn_factory=fake_factory(conn),
            )
        self.assertEqual(anydrop.EXIT_LOCAL, ctx.exception.exit_code)
        self.assertIn("追加写入", ctx.exception.message)
        self.assertEqual(10, len(conn.sent))


# ------------------------------------------------------------------ 3 边界条件


class TestBoundaries(ScratchCase):
    def test_零字节文件合法(self):
        tmp = self.scratch()
        path = tmp / "empty.bin"
        path.write_bytes(b"")
        local = anydrop.precheck_file(str(path))
        self.assertEqual(0, local.size)
        conn = FakeConnection(201, ok_payload(hashlib.sha256(b"").hexdigest(), 0))
        result = anydrop.upload_file(
            local,
            endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
            key="k",
            conn_factory=fake_factory(conn),
        )
        self.assertEqual(0, result["size"])
        self.assertEqual("0", conn.header("Content-Length"))

    def test_恰好等于上限允许(self):
        tmp = self.scratch()
        path = tmp / "exact.bin"
        path.write_bytes(b"a" * 10)
        local = anydrop.precheck_file(str(path), max_upload=10)
        self.assertEqual(10, local.size)

    def test_超过上限一个字节被拒(self):
        tmp = self.scratch()
        path = tmp / "over.bin"
        path.write_bytes(b"a" * 11)
        with self.assertRaises(anydrop.LocalFileError) as ctx:
            anydrop.precheck_file(str(path), max_upload=10)
        self.assertEqual(anydrop.EXIT_LOCAL, ctx.exception.exit_code)

    def test_文件名超长不截断由服务端处理(self):
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        long_name = "n" * 500
        local = anydrop.precheck_file(str(path), filename=long_name)
        self.assertEqual(500, len(local.filename))

    def test_可选文件名覆盖生效(self):
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        self.assertEqual("b.txt", anydrop.precheck_file(str(path), filename="b.txt").filename)

    def test_空文件名被拒(self):
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        with self.assertRaises(anydrop.LocalFileError):
            anydrop.precheck_file(str(path), filename="   ")

    def test_空路径被拒(self):
        with self.assertRaises(anydrop.LocalFileError):
            anydrop.precheck_file("   ")

    def test_极长幂等键被原样发出_截断是服务端的职责(self):
        # 服务端在 BlobEndpoints.cs 里做 idempotencyKey[..128]；客户端不截断，
        # 本用例只证明"原样发出"，不假装验证了服务端的截断。
        conn = FakeConnection(201, ok_payload(hashlib.sha256(b"x").hexdigest(), 1))
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        key = "i" * 300
        anydrop.upload_file(
            anydrop.precheck_file(str(path)),
            endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
            key="k",
            idempotency_key=key,
            conn_factory=fake_factory(conn),
        )
        self.assertEqual(key, conn.header("Idempotency-Key"))


# ------------------------------------------------------------------ 4 极端输入


class TestExtremeInputs(ScratchCase):
    def test_中文文件名走查询参数且不放进头部(self):
        conn = FakeConnection(201, ok_payload(hashlib.sha256(b"x").hexdigest(), 1))
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        anydrop.upload_file(
            anydrop.precheck_file(str(path), filename="报告 1.txt"),
            endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
            key="k",
            conn_factory=fake_factory(conn),
        )
        method, target = conn.request_line
        self.assertEqual("POST", method)
        self.assertTrue(target.startswith("/drop/v1/blobs?name="), target)
        self.assertIn("%E6%8A%A5%E5%91%8A", target)  # "报告"
        self.assertIn("%20", target)  # 空格
        self.assertIsNone(conn.header("X-Filename"))

    def test_含换行的文件名不会被塞进_http_头(self):
        # CR/LF 进头部就是 header 注入；必须改走查询参数。
        conn = FakeConnection(201, ok_payload(hashlib.sha256(b"x").hexdigest(), 1))
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        anydrop.upload_file(
            anydrop.precheck_file(str(path), filename="evil\r\nX-Injected: 1.txt"),
            endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
            key="k",
            conn_factory=fake_factory(conn),
        )
        self.assertIsNone(conn.header("X-Filename"))
        self.assertIsNone(conn.header("X-Injected"))
        self.assertIn("?name=", conn.request_line[1])

    def test_emoji_文件名走查询参数(self):
        conn = FakeConnection(201, ok_payload(hashlib.sha256(b"x").hexdigest(), 1))
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        anydrop.upload_file(
            anydrop.precheck_file(str(path), filename="📦report.txt"),
            endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
            key="k",
            conn_factory=fake_factory(conn),
        )
        self.assertIn("?name=", conn.request_line[1])

    def test_路径穿越式文件名不改变请求路径(self):
        # 文件名是数据、不是路径：无论内容如何，请求行必须停在 /v1/blobs。
        # 剥掉路径分隔符是服务端的职责（见 BlobEndpoints.SanitizeFileName），
        # 这里只保证客户端不会把它拼进 URL。
        conn = FakeConnection(201, ok_payload(hashlib.sha256(b"x").hexdigest(), 1))
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        anydrop.upload_file(
            anydrop.precheck_file(str(path), filename="../../etc/passwd"),
            endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
            key="k",
            conn_factory=fake_factory(conn),
        )
        method, target = conn.request_line
        self.assertEqual("POST", method)
        self.assertEqual("/drop/v1/blobs", target)
        self.assertEqual("../../etc/passwd", conn.header("X-Filename"))

    def test_密钥绝不进入_url_或查询串(self):
        conn = FakeConnection(201, ok_payload(hashlib.sha256(b"x").hexdigest(), 1))
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        anydrop.upload_file(
            anydrop.precheck_file(str(path)),
            endpoint=anydrop.parse_endpoint("https://fshake.com/drop"),
            key="TOPSECRETKEY",
            conn_factory=fake_factory(conn),
        )
        self.assertNotIn("TOPSECRETKEY", conn.request_line[1])
        self.assertEqual("Bearer TOPSECRETKEY", conn.header("Authorization"))


# ------------------------------------------------------------------ 5 核心业务逻辑


class TestCoreLogic(ScratchCase):
    def test_ascii_文件名走头部(self):
        self.assertFalse(anydrop.name_goes_in_query("report.md"))
        self.assertFalse(anydrop.name_goes_in_query("a b-c_1.txt"))

    def test_非_ascii_与含控制字符走查询参数(self):
        self.assertTrue(anydrop.name_goes_in_query("报告.txt"))
        self.assertTrue(anydrop.name_goes_in_query("a\nb.txt"))
        self.assertTrue(anydrop.name_goes_in_query("a\tb.txt"))

    def test_端点解析_https_默认端口与路径(self):
        ep = anydrop.parse_endpoint("https://fshake.com/drop")
        self.assertEqual("fshake.com", ep.host)
        self.assertEqual(443, ep.port)
        self.assertEqual("/drop", ep.base_path)
        self.assertTrue(ep.is_tls)

    def test_端点解析_http_显式端口(self):
        ep = anydrop.parse_endpoint("http://127.0.0.1:8790/drop")
        self.assertEqual(8790, ep.port)
        self.assertEqual("/drop", ep.base_path)
        self.assertFalse(ep.is_tls)

    def test_端点解析_末尾斜杠与空白容错(self):
        self.assertEqual("/drop", anydrop.parse_endpoint("  https://h/drop/  ").base_path)
        self.assertEqual("", anydrop.parse_endpoint("https://h").base_path)

    def test_端点解析_缺_scheme_或主机名报配置错(self):
        for bad in ("fshake.com/drop", "ftp://h/drop", "https://", ""):
            with self.subTest(bad=bad):
                with self.assertRaises(anydrop.ConfigError):
                    anydrop.parse_endpoint(bad)

    def test_端点解析_端口非法报配置错(self):
        with self.assertRaises(anydrop.ConfigError):
            anydrop.parse_endpoint("http://h:notaport/drop")

    def test_错误信封解析(self):
        self.assertEqual(
            ("invalid_token", "密钥无效"),
            anydrop.parse_error_envelope('{"error":{"code":"invalid_token","message":"密钥无效"}}'.encode()),
        )

    def test_错误信封解析_非_json_与非对象(self):
        self.assertEqual(("unknown", ""), anydrop.parse_error_envelope(b"<html>"))
        self.assertEqual(("unknown", ""), anydrop.parse_error_envelope(b"[]"))
        self.assertEqual(("unknown", ""), anydrop.parse_error_envelope(b"{}"))

    def test_密钥解析_环境变量优先于注册表(self):
        calls = []

        def registry():
            calls.append(1)
            return "from-registry"

        self.assertEqual("from-env", anydrop.resolve_key({"ANYDROP_KEY": "from-env"}, registry_lookup=registry))
        self.assertEqual([], calls, "环境变量命中时不该去读注册表")

    def test_密钥解析_兼容_AnyDrop_Key_这个名字(self):
        self.assertEqual(
            "legacy", anydrop.resolve_key({"AnyDrop_Key": "legacy"}, registry_lookup=lambda: None)
        )

    def test_密钥解析_环境变量缺失时改用回退结果(self):
        # 注意：这里注入的是假回退函数 —— 真实注册表读取由
        # test_注册表回退真的会执行且与直接读注册表一致 单独覆盖。
        self.assertEqual(
            "from-registry", anydrop.resolve_key({}, registry_lookup=lambda: "from-registry")
        )

    def test_密钥解析_空白值视为没有(self):
        self.assertEqual(
            "from-registry",
            anydrop.resolve_key({"ANYDROP_KEY": "   "}, registry_lookup=lambda: "from-registry"),
        )

    def test_密钥解析_都没有时报配置错(self):
        for registry_value in (None, "", "  "):
            with self.subTest(registry=registry_value):
                with self.assertRaises(anydrop.ConfigError) as ctx:
                    anydrop.resolve_key({}, registry_lookup=lambda: registry_value)
                self.assertEqual(anydrop.EXIT_CONFIG, ctx.exception.exit_code)

    def test_主机名含空白时报配置错而不是漏成未捕获异常(self):
        # http.client 的 InvalidURL 是 HTTPException，**不是** OSError：
        # 不提前拦就会穿透所有 except，变成 traceback + 退出码 1，
        # 而退出码表是对外契约（SKILL.md、README.md、本文件模块 docstring）。
        for bad in ("https://fl ask.com/drop", "https://ho\u3000st/drop"):
            with self.subTest(bad=bad):
                with self.assertRaises(anydrop.ConfigError) as ctx:
                    anydrop.parse_endpoint(bad)
                self.assertEqual(anydrop.EXIT_CONFIG, ctx.exception.exit_code)

    def test_主机名含空白时_main_给出干净错误而不是_traceback(self):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = anydrop.main(["upload", __file__, "--base", "https://fl ask.com/drop"])
        self.assertEqual(2, code)
        self.assertNotIn("Traceback", err.getvalue())

    def test_resolve_key_的默认回退指向真实注册表读取(self):
        # 这条钉住的是"默认参数真的绑上了 read_key_from_registry"：
        # 一旦脱钩，agent 会话里（环境变量被清洗）就再也拿不到密钥。
        signature = inspect.signature(anydrop.resolve_key)
        self.assertIs(
            anydrop.read_key_from_registry,
            signature.parameters["registry_lookup"].default,
        )

    def test_注册表回退真的会执行且与直接读注册表一致(self):
        # 其余四条回退测试都注入了 lambda，所以 read_key_from_registry 的函数体
        # （winreg 打开键、QueryValueEx、吞 OSError、strip）在本套件里从不执行，
        # 而它是 agent 会话里唯一能拿到密钥的通路。
        if os.name != "nt":
            self.assertIsNone(anydrop.read_key_from_registry())
            return

        import winreg

        expected = None
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as key:
                for name in anydrop.KEY_ENV_NAMES:
                    try:
                        value, _kind = winreg.QueryValueEx(key, name)
                    except OSError:
                        continue
                    if isinstance(value, str) and value.strip():
                        expected = value.strip()
                        break
        except OSError:
            expected = None

        actual = anydrop.read_key_from_registry()
        # 比摘要不比原值：断言失败时 unittest 会打印两边的值，而其中一边是真密钥。
        self.assertEqual(digest(expected), digest(actual))
        if actual is not None:
            # 用 assertTrue 而不是 assertEqual(actual, actual.strip())：后者失败时
            # 同样会把密钥打进输出。
            self.assertTrue(actual == actual.strip(), "取到的值必须已被 strip")

    def test_整数环境变量校验(self):
        os.environ["ANYDROP_TEST_INT"] = "abc"
        try:
            with self.assertRaises(anydrop.ConfigError):
                anydrop._int_from_env("ANYDROP_TEST_INT", 5)
        finally:
            del os.environ["ANYDROP_TEST_INT"]
        os.environ["ANYDROP_TEST_INT"] = "0"
        try:
            with self.assertRaises(anydrop.ConfigError):
                anydrop._int_from_env("ANYDROP_TEST_INT", 5)
        finally:
            del os.environ["ANYDROP_TEST_INT"]
        self.assertEqual(5, anydrop._int_from_env("ANYDROP_TEST_INT_MISSING", 5))


# ------------------------------------------------------------------ 6 模块间交互（真实 HTTP）


class TestAgainstRealHttpServer(ScratchCase):
    def setUp(self):
        self.server = LocalServer()
        self.addCleanup(self.server.close)

    def test_请求带_content_length_且不分块(self):
        data = b"payload-" + b"z" * 5000
        tmp = self.scratch()
        path = tmp / "payload.bin"
        path.write_bytes(data)
        anydrop.upload_file(
            anydrop.precheck_file(str(path)),
            endpoint=anydrop.parse_endpoint(self.server.base),
            key="k",
        )
        seen = self.server.observed[0]
        self.assertEqual(str(len(data)), seen["content_length"])
        self.assertIsNone(seen["transfer_encoding"], "不得使用分块传输（服务端会回 411）")
        self.assertEqual("Bearer k", seen["authorization"])
        self.assertEqual(data, self.server.httpd.body, "服务端收到的字节必须与文件一致")

    def test_非_ascii_文件名在真实请求里走查询串(self):
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        anydrop.upload_file(
            anydrop.precheck_file(str(path), filename="报告.txt"),
            endpoint=anydrop.parse_endpoint(self.server.base),
            key="k",
        )
        seen = self.server.observed[0]
        self.assertTrue(seen["path"].startswith("/drop/v1/blobs?name="), seen["path"])
        self.assertIsNone(seen["x_filename"])

    def test_ascii_文件名在真实请求里走头部(self):
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        anydrop.upload_file(
            anydrop.precheck_file(str(path), filename="report.md"),
            endpoint=anydrop.parse_endpoint(self.server.base),
            key="k",
        )
        seen = self.server.observed[0]
        self.assertEqual("report.md", seen["x_filename"])
        self.assertNotIn("?", seen["path"])

    def test_幂等键在真实请求里透传(self):
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        anydrop.upload_file(
            anydrop.precheck_file(str(path)),
            endpoint=anydrop.parse_endpoint(self.server.base),
            key="k",
            idempotency_key="idem-123",
        )
        self.assertEqual("idem-123", self.server.observed[0]["idempotency_key"])

    def test_零字节文件在真实请求里也是合法上传(self):
        tmp = self.scratch()
        path = tmp / "empty.bin"
        path.write_bytes(b"")
        result = anydrop.upload_file(
            anydrop.precheck_file(str(path)),
            endpoint=anydrop.parse_endpoint(self.server.base),
            key="k",
        )
        self.assertEqual(0, result["size"])
        self.assertEqual(b"", self.server.httpd.body)


# ------------------------------------------------------------------ 7 回归守卫 / CLI 契约


class TestCliContract(ScratchCase):
    def setUp(self):
        self.server = LocalServer()
        self.addCleanup(self.server.close)

    def _run(self, argv, env):
        out, err = io.StringIO(), io.StringIO()
        with mock.patch.dict(os.environ, env, clear=False):
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                code = anydrop.main(argv)
        return code, out.getvalue(), err.getvalue()

    def test_cli_成功时_stdout_恰好一行_json_且退出码0(self):
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"hello")
        code, out, err = self._run(
            ["upload", str(path), "--base", self.server.base], {"ANYDROP_KEY": "k"}
        )
        self.assertEqual(0, code, err)
        self.assertEqual(1, len(out.strip().splitlines()), f"stdout 必须恰好一行：{out!r}")
        payload = json.loads(out)
        self.assertEqual(
            sorted(anydrop.RESPONSE_FIELDS), sorted(payload.keys()), "只输出契约字段"
        )
        self.assertTrue(payload["url"].startswith("https://"))

    def test_cli_401_时退出码2_且写入_stderr_而不是_stdout(self):
        self.server.httpd.responder = lambda srv: (
            401,
            {"error": {"code": "invalid_token", "message": "密钥无效或已撤销"}},
        )
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        code, out, err = self._run(
            ["upload", str(path), "--base", self.server.base], {"ANYDROP_KEY": "k"}
        )
        self.assertEqual(2, code)
        self.assertEqual("", out.strip(), "失败时 stdout 必须是空的")
        self.assertIn("密钥无效", err)

    def test_cli_413_时退出码4(self):
        self.server.httpd.responder = lambda srv: (
            413,
            {"error": {"code": "file_too_large", "message": "文件超过上限（256 MiB）"}},
        )
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        code, out, err = self._run(
            ["upload", str(path), "--base", self.server.base], {"ANYDROP_KEY": "k"}
        )
        self.assertEqual(4, code)
        self.assertIn("超过上限", err)

    def test_cli_文件不存在时退出码3_且不发请求(self):
        code, out, err = self._run(
            ["upload", str(self.scratch() / "nope.bin"), "--base", self.server.base],
            {"ANYDROP_KEY": "k"},
        )
        self.assertEqual(3, code)
        self.assertIn("不存在", err)
        self.assertEqual([], self.server.observed, "本地预检失败就不该发出请求")

    def test_cli_本地目录被拒(self):
        tmp = self.scratch()
        code, out, err = self._run(
            ["upload", str(tmp), "--base", self.server.base], {"ANYDROP_KEY": "k"}
        )
        self.assertEqual(3, code)
        self.assertIn("目录", err)

    def test_cli_传入非法_base_时退出码2(self):
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        code, out, err = self._run(["upload", str(path), "--base", "nonsense"], {"ANYDROP_KEY": "k"})
        self.assertEqual(2, code)
        self.assertIn("http://", err)

    def test_密钥绝不出现在_stdout_或_stderr(self):
        secret = "SUPERSECRETVALUE123"
        self.server.httpd.responder = lambda srv: (
            500,
            {"error": {"code": "internal_error", "message": "服务内部错误"}},
        )
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        code, out, err = self._run(
            ["upload", str(path), "--base", self.server.base], {"ANYDROP_KEY": secret}
        )
        self.assertEqual(4, code)
        self.assertNotIn(secret, out)
        self.assertNotIn(secret, err)

    def test_基地址来自环境变量(self):
        tmp = self.scratch()
        path = tmp / "a.txt"
        path.write_bytes(b"x")
        code, out, err = self._run(
            ["upload", str(path)], {"ANYDROP_KEY": "k", "ANYDROP_BASE": self.server.base}
        )
        self.assertEqual(0, code, err)
        self.assertEqual(1, len(self.server.observed))

    def test_默认基地址指向生产端点(self):
        self.assertEqual("https://fshake.com/drop", anydrop.DEFAULT_BASE)

    def test_环境变量上限在_main_里真正生效(self):
        # ANYDROP_MAX_UPLOAD 决定"超限文件在发请求前就被拒"；它一旦失效，
        # 用户会为一个必然 413 的文件白传 256 MiB。
        tmp = self.scratch()
        path = tmp / "big.bin"
        path.write_bytes(b"x" * 64)
        code, out, err = self._run(
            ["upload", str(path), "--base", self.server.base],
            {"ANYDROP_KEY": "k", "ANYDROP_MAX_UPLOAD": "10"},
        )
        self.assertEqual(3, code, err)
        self.assertIn("超过上限", err)
        self.assertEqual([], self.server.observed, "预检失败就不该发出请求")

    def test_响应字段集合被冻结(self):
        # 契约守卫：改了 RESPONSE_FIELDS 就等于改了对外接口，必须是有意的。
        self.assertEqual(("id", "url", "sha256", "size", "expiresAt"), anydrop.RESPONSE_FIELDS)


class TestLocalFilePrecheck(ScratchCase):
    def test_符号链接到普通文件可用(self):
        tmp = self.scratch()
        target = tmp / "real.txt"
        target.write_bytes(b"data")
        link = tmp / "link.txt"
        try:
            os.symlink(target, link)
        except (OSError, NotImplementedError) as exc:
            self.skipTest(f"本机不允许创建符号链接：{exc}")
        if not os.path.isfile(link):
            self.skipTest("本机的符号链接指向不可读")
        local = anydrop.precheck_file(str(link))
        self.assertEqual(4, local.size)

    def test_符号链接到目录被拒(self):
        tmp = self.scratch()
        real_dir = tmp / "realdir"
        real_dir.mkdir()
        link = tmp / "dirlink"
        try:
            os.symlink(real_dir, link, target_is_directory=True)
        except (OSError, NotImplementedError) as exc:
            self.skipTest(f"本机不允许创建符号链接：{exc}")
        with self.assertRaises(anydrop.LocalFileError):
            anydrop.precheck_file(str(link))


if __name__ == "__main__":
    unittest.main(verbosity=2)
