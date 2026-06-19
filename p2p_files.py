import asyncio
import json
import os
import socket
import threading
import time

from capsule_utils import capsule_missing_files, capsule_target_path, file_sha256
from p2p_tunnel import McgoP2PTunnel, P2PTunnelConfig

try:
    from qt_compat import QObject, Signal
except ModuleNotFoundError:
    class QObject:
        def __init__(self, *args, **kwargs):
            super().__init__()

    class _DummySignal:
        def __init__(self, *args, **kwargs):
            pass

        def emit(self, *args, **kwargs):
            pass

    Signal = _DummySignal


FILE_ROOM_SUFFIX = "-FILES"
FILE_BUFFER_SIZE = 256 * 1024


def file_room(room):
    return f"{str(room or '').strip()}{FILE_ROOM_SUFFIX}"


def pick_local_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


async def _read_json_line(reader, timeout=20):
    line = await asyncio.wait_for(reader.readline(), timeout=timeout)
    if not line:
        raise ConnectionError("connection closed")
    return json.loads(line.decode("utf-8"))


async def _write_json_line(writer, payload):
    writer.write(json.dumps(payload, separators=(",", ":")).encode("utf-8") + b"\n")
    await writer.drain()


class CapsuleFileHost:
    def __init__(self, runtime_dir, p2p_config, status_callback=None, failed_callback=None):
        self.runtime_dir = os.path.abspath(runtime_dir)
        self.p2p_config = p2p_config
        self.status_callback = status_callback or (lambda _message: None)
        self.failed_callback = failed_callback or (lambda _message: None)
        self.thread = None
        self.loop = None
        self.stop_event = None
        self.server = None
        self.tunnel = None
        self._tunnel_failed_message = ""
        self._tunnel_ready = False

    def start(self):
        if self.thread and self.thread.is_alive():
            return
        self.thread = threading.Thread(target=self._thread_main, name="McGoCapsuleFileHost", daemon=True)
        self.thread.start()

    def stop(self):
        if self.loop and self.stop_event:
            self.loop.call_soon_threadsafe(self.stop_event.set)
        if self.server:
            self.loop.call_soon_threadsafe(self.server.close)
        if self.tunnel:
            self.tunnel.stop()

    def is_running(self):
        return bool(self.thread and self.thread.is_alive())

    def _thread_main(self):
        try:
            asyncio.run(self._run())
        except Exception as exc:
            self.failed_callback(str(exc))

    async def _run(self):
        self.loop = asyncio.get_running_loop()
        self.stop_event = asyncio.Event()
        self._tunnel_failed_message = ""
        self._tunnel_ready = False
        local_port = pick_local_port()
        self.server = await asyncio.start_server(self._handle_client, "127.0.0.1", local_port)
        room = file_room(self.p2p_config.room)
        file_config = P2PTunnelConfig(
            mode="host",
            relay_host=self.p2p_config.relay_host,
            relay_port=self.p2p_config.relay_port,
            room=room,
            secret=self.p2p_config.secret,
            local_host="127.0.0.1",
            local_port=local_port,
            udp_enabled=self.p2p_config.udp_enabled,
        )
        self.tunnel = McgoP2PTunnel(
            file_config,
            status_callback=lambda message: self._on_tunnel_status(room, message),
            failed_callback=self._on_tunnel_failed,
        )
        self.tunnel.start()
        await self._wait_for_tunnel_ready(room)
        async with self.server:
            await self.stop_event.wait()
        self.server.close()
        await self.server.wait_closed()
        if self.tunnel:
            self.tunnel.stop()
        self.status_callback("胶囊补包服务已停止。")

    def _on_tunnel_status(self, room, message):
        if str(message).startswith(f"房间 {room} 已连接"):
            self._tunnel_ready = True
            return
        self.status_callback(f"胶囊补包：{message}")

    def _on_tunnel_failed(self, message):
        self._tunnel_failed_message = str(message)
        self.failed_callback(self._tunnel_failed_message)
        if self.loop and self.stop_event:
            self.loop.call_soon_threadsafe(self.stop_event.set)

    async def _wait_for_tunnel_ready(self, room, timeout=12):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline and not self.stop_event.is_set():
            if self._tunnel_failed_message:
                return
            if self._tunnel_ready:
                self.status_callback(f"胶囊补包服务已启动：房间 {room}")
                return
            await asyncio.sleep(0.05)
        if not self.stop_event.is_set() and not self._tunnel_failed_message:
            self._on_tunnel_failed("胶囊补包通道连接超时")

    async def _handle_client(self, reader, writer):
        try:
            request = await _read_json_line(reader)
            if request.get("type") != "get":
                await _write_json_line(writer, {"type": "error", "message": "unknown request"})
                return
            relative = request.get("path", "")
            expected_sha256 = str(request.get("sha256", ""))
            path = capsule_target_path(self.runtime_dir, relative)
            if not os.path.isfile(path):
                await _write_json_line(writer, {"type": "error", "message": "file not found"})
                return
            actual_sha256 = file_sha256(path)
            if expected_sha256 and actual_sha256 != expected_sha256:
                await _write_json_line(writer, {"type": "error", "message": "file sha256 mismatch on host"})
                return
            size = os.path.getsize(path)
            await _write_json_line(writer, {"type": "ok", "size": size, "sha256": actual_sha256})
            with open(path, "rb") as handle:
                for chunk in iter(lambda: handle.read(FILE_BUFFER_SIZE), b""):
                    writer.write(chunk)
                    await writer.drain()
        except Exception as exc:
            try:
                await _write_json_line(writer, {"type": "error", "message": str(exc)})
            except Exception:
                pass
        finally:
            writer.close()
            try:
                await writer.wait_closed()
            except Exception:
                pass


def _read_socket_line(sock):
    chunks = []
    while True:
        data = sock.recv(1)
        if not data:
            raise ConnectionError("connection closed")
        if data == b"\n":
            break
        chunks.append(data)
        if len(chunks) > 16 * 1024:
            raise RuntimeError("response line is too large")
    return json.loads(b"".join(chunks).decode("utf-8"))


def _connect_with_retry(host, port, timeout=12):
    deadline = time.time() + timeout
    last_error = None
    while time.time() < deadline:
        try:
            return socket.create_connection((host, port), timeout=2)
        except OSError as exc:
            last_error = exc
            time.sleep(0.15)
    raise RuntimeError(f"无法连接胶囊补包通道：{last_error}")


def pull_capsule_files(runtime_dir, files, p2p_config, progress_callback=None, status_callback=None, should_stop=None):
    runtime_dir = os.path.abspath(runtime_dir)
    missing = capsule_missing_files(runtime_dir, files)
    if not missing:
        return {"downloaded": 0, "skipped": len(files or []), "total": 0}
    should_stop = should_stop or (lambda: False)

    join_port = pick_local_port()
    file_config = P2PTunnelConfig(
        mode="join",
        relay_host=p2p_config.relay_host,
        relay_port=p2p_config.relay_port,
        room=file_room(p2p_config.room),
        secret=p2p_config.secret,
        local_host="127.0.0.1",
        local_port=join_port,
        udp_enabled=p2p_config.udp_enabled,
    )
    tunnel = McgoP2PTunnel(file_config, status_callback=status_callback or (lambda _message: None))
    tunnel.start()
    downloaded = 0
    total_bytes = 0
    try:
        for index, item in enumerate(missing, start=1):
            if should_stop():
                raise RuntimeError("胶囊补包已取消")
            relative = item.get("path", "")
            expected_sha256 = item.get("sha256", "")
            if status_callback:
                status_callback(f"正在补包 {index}/{len(missing)}：{relative}")
            target = capsule_target_path(runtime_dir, relative)
            temp_target = target + ".mcgo-download"
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with _connect_with_retry("127.0.0.1", join_port) as sock:
                sock.settimeout(2)
                request = json.dumps(
                    {"type": "get", "path": relative, "sha256": expected_sha256},
                    separators=(",", ":"),
                ).encode("utf-8") + b"\n"
                sock.sendall(request)
                response = _read_socket_line(sock)
                if response.get("type") != "ok":
                    raise RuntimeError(response.get("message", f"补包失败：{relative}"))
                size = int(response.get("size", 0))
                remaining = size
                with open(temp_target, "wb") as handle:
                    while remaining > 0:
                        if should_stop():
                            raise RuntimeError("胶囊补包已取消")
                        chunk = sock.recv(min(FILE_BUFFER_SIZE, remaining))
                        if not chunk:
                            raise ConnectionError(f"补包连接中断：{relative}")
                        handle.write(chunk)
                        remaining -= len(chunk)
                        total_bytes += len(chunk)
                actual_sha256 = file_sha256(temp_target)
                if expected_sha256 and actual_sha256 != expected_sha256:
                    raise RuntimeError(f"补包文件校验失败：{relative}")
                os.replace(temp_target, target)
            downloaded += 1
            if progress_callback:
                progress_callback(int(index / max(len(missing), 1) * 100))
        return {"downloaded": downloaded, "skipped": len(files or []) - len(missing), "total": len(missing), "bytes": total_bytes}
    finally:
        tunnel.stop()


class CapsuleFilePullWorker(QObject):
    progress = Signal(int)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, runtime_dir, files, p2p_config):
        super().__init__()
        self.runtime_dir = runtime_dir
        self.files = list(files or [])
        self.p2p_config = p2p_config
        self._stop_requested = False

    def request_stop(self):
        self._stop_requested = True

    def run(self):
        try:
            payload = pull_capsule_files(
                self.runtime_dir,
                self.files,
                self.p2p_config,
                progress_callback=self.progress.emit,
                status_callback=self.status.emit,
                should_stop=lambda: self._stop_requested,
            )
            self.finished.emit(payload)
        except Exception as exc:
            self.failed.emit(str(exc))
