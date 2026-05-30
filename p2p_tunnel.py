import argparse
import asyncio
import json
import logging
import secrets
import string
import threading
from dataclasses import dataclass, field


logger = logging.getLogger(__name__)


MAX_JSON_LINE = 16 * 1024
BUFFER_SIZE = 64 * 1024


def generate_room_code(length=8):
    alphabet = string.ascii_uppercase + string.digits
    return "".join(secrets.choice(alphabet) for _ in range(length))


@dataclass
class P2PTunnelConfig:
    mode: str
    relay_host: str = "flyliq.cn"
    relay_port: int = 10721
    room: str = ""
    secret: str = ""
    local_host: str = "127.0.0.1"
    local_port: int = 25565


async def _read_json(reader, timeout=None):
    if timeout:
        line = await asyncio.wait_for(reader.readline(), timeout=timeout)
    else:
        line = await reader.readline()
    if not line:
        raise ConnectionError("connection closed")
    if len(line) > MAX_JSON_LINE:
        raise ValueError("json line is too large")
    return json.loads(line.decode("utf-8"))


async def _write_json(writer, payload):
    writer.write(json.dumps(payload, separators=(",", ":")).encode("utf-8") + b"\n")
    await writer.drain()


def _close_writer(writer):
    if writer and not writer.is_closing():
        writer.close()


async def _wait_closed(writer):
    if not writer:
        return
    try:
        await writer.wait_closed()
    except Exception:
        pass


async def _pipe(reader, writer):
    try:
        while True:
            data = await reader.read(BUFFER_SIZE)
            if not data:
                break
            writer.write(data)
            await writer.drain()
    finally:
        _close_writer(writer)


async def _bridge(reader_a, writer_a, reader_b, writer_b):
    task_a = asyncio.create_task(_pipe(reader_a, writer_b))
    task_b = asyncio.create_task(_pipe(reader_b, writer_a))
    done, pending = await asyncio.wait({task_a, task_b}, return_when=asyncio.FIRST_COMPLETED)
    for task in pending:
        task.cancel()
    await asyncio.gather(*pending, return_exceptions=True)
    await asyncio.gather(*done, return_exceptions=True)
    _close_writer(writer_a)
    _close_writer(writer_b)
    await asyncio.gather(_wait_closed(writer_a), _wait_closed(writer_b), return_exceptions=True)


@dataclass
class PendingGuest:
    connection_id: str
    future: asyncio.Future
    done: asyncio.Future


@dataclass
class RelayRoom:
    room: str
    secret: str
    control_reader: asyncio.StreamReader
    control_writer: asyncio.StreamWriter
    pending: dict = field(default_factory=dict)


class P2PRelayServer:
    def __init__(self):
        self.rooms = {}
        self.lock = asyncio.Lock()

    async def serve(self, host="0.0.0.0", port=10721):
        server = await asyncio.start_server(self.handle_client, host, port)
        addresses = ", ".join(str(sock.getsockname()) for sock in server.sockets or [])
        logger.info("McGo P2P relay listening on %s", addresses)
        async with server:
            await server.serve_forever()

    async def handle_client(self, reader, writer):
        peer = writer.get_extra_info("peername")
        try:
            hello = await _read_json(reader, timeout=10)
            role = hello.get("role", "")
            if role == "host_control":
                await self._handle_host_control(reader, writer, hello)
            elif role == "host_data":
                await self._handle_host_data(reader, writer, hello)
            elif role == "guest_data":
                await self._handle_guest_data(reader, writer, hello)
            else:
                await _write_json(writer, {"type": "error", "message": "unknown role"})
        except Exception as exc:
            logger.debug("relay client %s closed: %s", peer, exc)
        finally:
            _close_writer(writer)
            await _wait_closed(writer)

    async def _handle_host_control(self, reader, writer, hello):
        room_id = _clean_room(hello.get("room", ""))
        if not room_id:
            await _write_json(writer, {"type": "error", "message": "room is required"})
            return
        room = RelayRoom(
            room=room_id,
            secret=str(hello.get("secret", "")),
            control_reader=reader,
            control_writer=writer,
        )
        async with self.lock:
            if room_id in self.rooms:
                await _write_json(writer, {"type": "error", "message": "room already exists"})
                return
            self.rooms[room_id] = room
        await _write_json(writer, {"type": "ok", "room": room_id})
        logger.info("host registered room=%s", room_id)
        try:
            while await reader.readline():
                pass
        finally:
            async with self.lock:
                existing = self.rooms.get(room_id)
                if existing is room:
                    self.rooms.pop(room_id, None)
            for pending in list(room.pending.values()):
                if not pending.future.done():
                    pending.future.set_exception(ConnectionError("host disconnected"))
                if not pending.done.done():
                    pending.done.set_result(True)
            logger.info("host disconnected room=%s", room_id)

    async def _handle_guest_data(self, reader, writer, hello):
        room_id = _clean_room(hello.get("room", ""))
        room = await self._get_authorized_room(room_id, hello.get("secret", ""))
        if not room:
            await _write_json(writer, {"type": "error", "message": "room not found or secret mismatch"})
            return
        loop = asyncio.get_running_loop()
        connection_id = secrets.token_hex(8)
        pending = PendingGuest(connection_id, loop.create_future(), loop.create_future())
        async with self.lock:
            room.pending[connection_id] = pending
        try:
            await _write_json(room.control_writer, {"type": "connect", "connection_id": connection_id})
            host_reader, host_writer = await asyncio.wait_for(pending.future, timeout=20)
            await _write_json(writer, {"type": "ok", "connection_id": connection_id})
            await _bridge(reader, writer, host_reader, host_writer)
        finally:
            async with self.lock:
                room.pending.pop(connection_id, None)
            if not pending.done.done():
                pending.done.set_result(True)

    async def _handle_host_data(self, reader, writer, hello):
        room_id = _clean_room(hello.get("room", ""))
        connection_id = str(hello.get("connection_id", ""))
        room = await self._get_authorized_room(room_id, hello.get("secret", ""))
        if not room:
            await _write_json(writer, {"type": "error", "message": "room not found or secret mismatch"})
            return
        pending = room.pending.get(connection_id)
        if not pending or pending.future.done():
            await _write_json(writer, {"type": "error", "message": "connection request expired"})
            return
        await _write_json(writer, {"type": "ok", "connection_id": connection_id})
        pending.future.set_result((reader, writer))
        await pending.done

    async def _get_authorized_room(self, room_id, secret):
        async with self.lock:
            room = self.rooms.get(room_id)
        if not room:
            return None
        expected = room.secret or ""
        if expected and str(secret or "") != expected:
            return None
        return room


class McgoP2PTunnel:
    def __init__(self, config, status_callback=None, stopped_callback=None, failed_callback=None):
        self.config = config
        self.status_callback = status_callback or (lambda _message: None)
        self.stopped_callback = stopped_callback or (lambda _message: None)
        self.failed_callback = failed_callback or (lambda _message: None)
        self.thread = None
        self.loop = None
        self.stop_event = None
        self.local_server = None
        self.control_writer = None
        self.data_tasks = set()

    def start(self):
        if self.thread and self.thread.is_alive():
            return
        self.thread = threading.Thread(target=self._thread_main, name="McGoP2PTunnel", daemon=True)
        self.thread.start()

    def stop(self):
        if not self.loop or not self.stop_event:
            return
        self.loop.call_soon_threadsafe(self.stop_event.set)
        if self.control_writer:
            self.loop.call_soon_threadsafe(_close_writer, self.control_writer)
        if self.local_server:
            self.loop.call_soon_threadsafe(self.local_server.close)

    def is_running(self):
        return bool(self.thread and self.thread.is_alive())

    def _thread_main(self):
        try:
            asyncio.run(self._run())
        except Exception as exc:
            logger.exception("P2P tunnel failed")
            self.failed_callback(str(exc))

    async def _run(self):
        self.loop = asyncio.get_running_loop()
        self.stop_event = asyncio.Event()
        try:
            if self.config.mode == "host":
                await self._run_host()
            elif self.config.mode == "join":
                await self._run_join()
            else:
                raise ValueError(f"unsupported tunnel mode: {self.config.mode}")
        finally:
            self.stopped_callback("P2P 隧道已停止。")

    async def _open_relay(self, role, **extra):
        reader, writer = await asyncio.open_connection(self.config.relay_host, self.config.relay_port)
        payload = {
            "role": role,
            "room": self.config.room,
            "secret": self.config.secret,
        }
        payload.update(extra)
        await _write_json(writer, payload)
        response = await _read_json(reader, timeout=20)
        if response.get("type") != "ok":
            _close_writer(writer)
            await _wait_closed(writer)
            raise RuntimeError(response.get("message", "relay rejected connection"))
        return reader, writer

    async def _run_host(self):
        reader, writer = await self._open_relay("host_control")
        self.control_writer = writer
        self.status_callback(
            f"房间 {self.config.room} 已连接到 {self.config.relay_host}:{self.config.relay_port}，等待加入者。"
        )
        control_task = asyncio.create_task(self._read_host_control(reader))
        stop_task = asyncio.create_task(self.stop_event.wait())
        done, pending = await asyncio.wait({control_task, stop_task}, return_when=asyncio.FIRST_COMPLETED)
        for task in pending:
            task.cancel()
        await asyncio.gather(*pending, return_exceptions=True)
        for task in done:
            exc = task.exception() if not task.cancelled() else None
            if exc and not self.stop_event.is_set():
                raise exc
        for task in list(self.data_tasks):
            task.cancel()
        await asyncio.gather(*self.data_tasks, return_exceptions=True)
        _close_writer(writer)
        await _wait_closed(writer)

    async def _read_host_control(self, reader):
        while not self.stop_event.is_set():
            message = await _read_json(reader)
            if message.get("type") == "connect":
                connection_id = str(message.get("connection_id", ""))
                task = asyncio.create_task(self._handle_host_data(connection_id))
                self.data_tasks.add(task)
                task.add_done_callback(self.data_tasks.discard)
            elif message.get("type") == "error":
                raise RuntimeError(message.get("message", "relay error"))

    async def _handle_host_data(self, connection_id):
        try:
            relay_reader, relay_writer = await self._open_relay("host_data", connection_id=connection_id)
            local_reader, local_writer = await asyncio.open_connection(self.config.local_host, self.config.local_port)
            self.status_callback(f"加入者已连接，正在转发到 {self.config.local_host}:{self.config.local_port}。")
            await _bridge(local_reader, local_writer, relay_reader, relay_writer)
            self.status_callback("一条 P2P 联机连接已断开。")
        except Exception as exc:
            self.status_callback(f"P2P 房主转发失败：{exc}")

    async def _run_join(self):
        server = await asyncio.start_server(self._handle_guest_local, self.config.local_host, self.config.local_port)
        self.local_server = server
        self.status_callback(
            f"加入隧道已就绪。在 Minecraft 中连接 {self.config.local_host}:{self.config.local_port}。"
        )
        async with server:
            await self.stop_event.wait()
        server.close()
        await server.wait_closed()

    async def _handle_guest_local(self, local_reader, local_writer):
        try:
            relay_reader, relay_writer = await self._open_relay("guest_data")
            self.status_callback(f"已通过房间 {self.config.room} 接入房主。")
            await _bridge(local_reader, local_writer, relay_reader, relay_writer)
            self.status_callback("一条本地 Minecraft 连接已断开。")
        except Exception as exc:
            self.status_callback(f"P2P 加入失败：{exc}")
            _close_writer(local_writer)
            await _wait_closed(local_writer)


def _clean_room(value):
    return "".join(ch for ch in str(value or "").strip() if ch.isalnum() or ch in "-_")[:64]


async def run_relay_server(host, port):
    relay = P2PRelayServer()
    await relay.serve(host, port)


def main_server(argv=None):
    parser = argparse.ArgumentParser(description="McGo P2P relay server")
    parser.add_argument("--host", default="0.0.0.0", help="listen address")
    parser.add_argument("--port", type=int, default=10721, help="listen TCP port")
    parser.add_argument("--log-level", default="INFO", help="logging level")
    args = parser.parse_args(argv)
    logging.basicConfig(
        level=getattr(logging, args.log_level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    asyncio.run(run_relay_server(args.host, args.port))
