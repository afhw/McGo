import argparse
import asyncio
import json
import logging
import secrets
import string
import struct
import threading
import time
from dataclasses import dataclass, field


logger = logging.getLogger(__name__)


MAX_JSON_LINE = 16 * 1024
BUFFER_SIZE = 64 * 1024
DEFAULT_RELAY_RATE_LIMIT_KBPS = 8192
UDP_PAYLOAD_SIZE = 1000
UDP_PUNCH_TIMEOUT = 8.0
UDP_RESEND_TIMEOUT = 0.35
UDP_MAX_RETRIES = 24
UDP_MAGIC = b"MGU1"
UDP_HEADER = struct.Struct("!4sBBH8sIIH")
UDP_SYN = 1
UDP_SYN_ACK = 2
UDP_DATA = 3
UDP_ACK = 4
UDP_FIN = 5
UDP_KEEPALIVE = 6


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
    udp_enabled: bool = True


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


async def _pipe(reader, writer, rate_limit_bps=0):
    sent = 0
    started = time.monotonic()
    try:
        while True:
            data = await reader.read(BUFFER_SIZE)
            if not data:
                break
            writer.write(data)
            await writer.drain()
            if rate_limit_bps and rate_limit_bps > 0:
                sent += len(data)
                expected_elapsed = sent / rate_limit_bps
                delay = expected_elapsed - (time.monotonic() - started)
                if delay > 0:
                    await asyncio.sleep(delay)
    finally:
        _close_writer(writer)


async def _bridge(reader_a, writer_a, reader_b, writer_b, rate_limit_bps=0):
    task_a = asyncio.create_task(_pipe(reader_a, writer_b, rate_limit_bps))
    task_b = asyncio.create_task(_pipe(reader_b, writer_a, rate_limit_bps))
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
    udp_endpoints: dict = field(default_factory=dict)
    pending: dict = field(default_factory=dict)


class P2PRelayServer:
    def __init__(self, rate_limit_bps=0):
        self.rooms = {}
        self.lock = asyncio.Lock()
        self.rate_limit_bps = max(0, int(rate_limit_bps or 0))

    async def serve(self, host="0.0.0.0", port=10721):
        loop = asyncio.get_running_loop()
        udp_transport, _ = await loop.create_datagram_endpoint(
            lambda: RelayUdpProtocol(self),
            local_addr=(host, port),
        )
        server = await asyncio.start_server(self.handle_client, host, port)
        addresses = ", ".join(str(sock.getsockname()) for sock in server.sockets or [])
        logger.info("McGo P2P relay listening on TCP/UDP %s", addresses)
        try:
            async with server:
                await server.serve_forever()
        finally:
            udp_transport.close()

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
            elif role == "guest_probe":
                await self._handle_guest_probe(reader, writer, hello)
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
            await _bridge(reader, writer, host_reader, host_writer, self.rate_limit_bps)
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

    async def _handle_guest_probe(self, reader, writer, hello):
        room_id = _clean_room(hello.get("room", ""))
        room = await self._get_authorized_room(room_id, hello.get("secret", ""))
        if not room:
            await _write_json(writer, {"type": "error", "message": "room not found or secret mismatch"})
            return
        await _write_json(
            writer,
            {
                "type": "ok",
                "room": room_id,
                "udp_endpoints": room.udp_endpoints,
                "relay_rate_limit_bps": self.rate_limit_bps,
            },
        )

    async def _get_authorized_room(self, room_id, secret):
        async with self.lock:
            room = self.rooms.get(room_id)
        if not room:
            return None
        expected = room.secret or ""
        if expected and str(secret or "") != expected:
            return None
        return room

    async def handle_udp_register(self, payload, address, transport):
        room_id = _clean_room(payload.get("room", ""))
        side = str(payload.get("side", "")).strip()
        if side not in ("host", "guest"):
            _udp_send_json(transport, address, {"type": "udp_error", "message": "invalid side"})
            return
        room = await self._get_authorized_room(room_id, payload.get("secret", ""))
        if not room:
            _udp_send_json(transport, address, {"type": "udp_error", "message": "room not found or secret mismatch"})
            return
        endpoint = {"host": address[0], "port": int(address[1])}
        peer_side = "guest" if side == "host" else "host"
        async with self.lock:
            room.udp_endpoints[side] = endpoint
            peer = room.udp_endpoints.get(peer_side)
        _udp_send_json(
            transport,
            address,
            {
                "type": "udp_registered",
                "room": room_id,
                "side": side,
                "endpoint": endpoint,
                "peer": peer,
            },
        )


class RelayUdpProtocol(asyncio.DatagramProtocol):
    def __init__(self, relay):
        self.relay = relay
        self.transport = None

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, address):
        try:
            payload = json.loads(data.decode("utf-8"))
        except Exception:
            return
        if payload.get("type") != "udp_register":
            return
        asyncio.create_task(self.relay.handle_udp_register(payload, address, self.transport))


def _udp_send_json(transport, address, payload):
    transport.sendto(json.dumps(payload, separators=(",", ":")).encode("utf-8"), address)


def _pack_udp_frame(kind, stream_id, token, seq=0, ack=0, payload=b""):
    payload = payload or b""
    token = (token or b"")[:8].ljust(8, b"\0")
    return UDP_HEADER.pack(UDP_MAGIC, kind, 0, stream_id, token, seq, ack, len(payload)) + payload


def _unpack_udp_frame(data):
    if len(data) < UDP_HEADER.size:
        return None
    magic, kind, _flags, stream_id, token, seq, ack, length = UDP_HEADER.unpack(data[:UDP_HEADER.size])
    if magic != UDP_MAGIC or length != len(data) - UDP_HEADER.size:
        return None
    return {
        "kind": kind,
        "stream_id": stream_id,
        "token": token,
        "seq": seq,
        "ack": ack,
        "payload": data[UDP_HEADER.size:],
    }


class UdpReliableStream:
    def __init__(self, peer, address, stream_id, token, initiator=False):
        self.peer = peer
        self.address = address
        self.stream_id = stream_id
        self.token = token
        self.initiator = initiator
        self.send_seq = 1
        self.expected_seq = 1
        self.pending_acks = {}
        self.incoming = asyncio.Queue()
        self.closed = False

    def key(self):
        return self.stream_id, self.token

    async def connect(self):
        future = asyncio.Future()
        self.pending_acks[0] = future
        for _attempt in range(UDP_MAX_RETRIES):
            self.peer.send_frame(self.address, UDP_SYN, self.stream_id, self.token, seq=0)
            try:
                await asyncio.wait_for(asyncio.shield(future), UDP_RESEND_TIMEOUT)
                self.pending_acks.pop(0, None)
                return True
            except asyncio.TimeoutError:
                continue
        self.pending_acks.pop(0, None)
        return False

    async def write(self, data):
        offset = 0
        while offset < len(data) and not self.closed:
            chunk = data[offset:offset + UDP_PAYLOAD_SIZE]
            await self._send_reliable(UDP_DATA, chunk)
            offset += len(chunk)

    async def close(self):
        if self.closed:
            return
        self.closed = True
        try:
            await self._send_reliable(UDP_FIN, b"")
        except Exception:
            pass

    async def read(self):
        item = await self.incoming.get()
        return item

    async def _send_reliable(self, kind, payload):
        seq = self.send_seq
        self.send_seq += 1
        future = asyncio.Future()
        self.pending_acks[seq] = future
        for _attempt in range(UDP_MAX_RETRIES):
            self.peer.send_frame(self.address, kind, self.stream_id, self.token, seq=seq, payload=payload)
            try:
                await asyncio.wait_for(asyncio.shield(future), UDP_RESEND_TIMEOUT)
                self.pending_acks.pop(seq, None)
                return
            except asyncio.TimeoutError:
                continue
        self.pending_acks.pop(seq, None)
        raise TimeoutError("UDP stream ack timeout")

    def handle_frame(self, frame, address):
        kind = frame["kind"]
        seq = frame["seq"]
        ack = frame["ack"]
        if kind == UDP_SYN:
            self.peer.send_frame(address, UDP_SYN_ACK, self.stream_id, self.token, ack=seq)
            return
        if kind == UDP_SYN_ACK:
            future = self.pending_acks.get(0)
            if future and not future.done():
                future.set_result(True)
            return
        if kind == UDP_ACK:
            future = self.pending_acks.get(ack)
            if future and not future.done():
                future.set_result(True)
            return
        if kind == UDP_DATA:
            if seq == self.expected_seq:
                self.expected_seq += 1
                self.incoming.put_nowait(frame["payload"])
            self.peer.send_frame(address, UDP_ACK, self.stream_id, self.token, ack=seq)
            return
        if kind == UDP_FIN:
            self.peer.send_frame(address, UDP_ACK, self.stream_id, self.token, ack=seq)
            self.closed = True
            self.incoming.put_nowait(b"")


class UdpTunnelProtocol(asyncio.DatagramProtocol):
    def __init__(self, peer):
        self.peer = peer

    def connection_made(self, transport):
        self.peer.transport = transport

    def datagram_received(self, data, address):
        if data.startswith(UDP_MAGIC):
            frame = _unpack_udp_frame(data)
            if frame:
                self.peer.handle_frame(frame, address)
            return
        try:
            payload = json.loads(data.decode("utf-8"))
        except Exception:
            return
        if payload.get("type") == "udp_registered":
            self.peer.registration_queue.put_nowait(payload)


class UdpTunnelPeer:
    def __init__(self, config, side, status_callback, incoming_stream_callback=None):
        self.config = config
        self.side = side
        self.status_callback = status_callback
        self.incoming_stream_callback = incoming_stream_callback
        self.transport = None
        self.registration_queue = asyncio.Queue()
        self.streams = {}
        self.peer_endpoint = None
        self.register_task = None

    async def start(self):
        loop = asyncio.get_running_loop()
        self.transport, _ = await loop.create_datagram_endpoint(
            lambda: UdpTunnelProtocol(self),
            local_addr=("0.0.0.0", 0),
        )

    def close(self):
        if self.register_task:
            self.register_task.cancel()
        if self.transport:
            self.transport.close()

    def start_registration_loop(self):
        self.register_task = asyncio.create_task(self._registration_loop())

    async def _registration_loop(self):
        while True:
            try:
                peer = await self.register_once()
                if peer:
                    self.peer_endpoint = peer
                    self.transport.sendto(b'{"type":"udp_punch"}', peer)
            except Exception:
                pass
            await asyncio.sleep(1.0)

    async def register_until_peer(self, timeout=UDP_PUNCH_TIMEOUT):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            peer = await self.register_once()
            if peer:
                self.peer_endpoint = peer
                self.transport.sendto(b'{"type":"udp_punch"}', peer)
                return peer
            await asyncio.sleep(0.25)
        return None

    async def register_once(self):
        _udp_send_json(
            self.transport,
            (self.config.relay_host, self.config.relay_port),
            {
                "type": "udp_register",
                "room": self.config.room,
                "secret": self.config.secret,
                "side": self.side,
            },
        )
        try:
            response = await asyncio.wait_for(self.registration_queue.get(), 0.7)
        except asyncio.TimeoutError:
            return None
        peer = response.get("peer")
        if not peer:
            return None
        host = str(peer.get("host", "")).strip()
        port = _clean_port(peer.get("port", 0))
        if not host or not port:
            return None
        return host, port

    async def connect_stream(self):
        peer = await self.register_until_peer()
        if not peer:
            return None
        token = secrets.token_bytes(8)
        stream_id = secrets.randbelow(65535) + 1
        stream = UdpReliableStream(self, peer, stream_id, token, initiator=True)
        self.streams[stream.key()] = stream
        if await stream.connect():
            return stream
        self.streams.pop(stream.key(), None)
        return None

    def send_frame(self, address, kind, stream_id, token, seq=0, ack=0, payload=b""):
        if self.transport:
            self.transport.sendto(_pack_udp_frame(kind, stream_id, token, seq=seq, ack=ack, payload=payload), address)

    def handle_frame(self, frame, address):
        key = (frame["stream_id"], frame["token"])
        stream = self.streams.get(key)
        if not stream and self.side == "host" and frame["kind"] == UDP_SYN:
            stream = UdpReliableStream(self, address, frame["stream_id"], frame["token"], initiator=False)
            self.streams[key] = stream
            self.send_frame(address, UDP_SYN_ACK, stream.stream_id, stream.token, ack=frame["seq"])
            if self.incoming_stream_callback:
                asyncio.create_task(self.incoming_stream_callback(stream, address))
            return
        if stream:
            stream.address = address
            stream.handle_frame(frame, address)


async def _bridge_udp_stream(reader, writer, stream):
    async def tcp_to_udp():
        try:
            while True:
                data = await reader.read(BUFFER_SIZE)
                if not data:
                    break
                await stream.write(data)
        finally:
            await stream.close()

    async def udp_to_tcp():
        try:
            while True:
                data = await stream.read()
                if not data:
                    break
                writer.write(data)
                await writer.drain()
        finally:
            _close_writer(writer)

    task_a = asyncio.create_task(tcp_to_udp())
    task_b = asyncio.create_task(udp_to_tcp())
    done, pending = await asyncio.wait({task_a, task_b}, return_when=asyncio.FIRST_COMPLETED)
    for task in pending:
        task.cancel()
    await asyncio.gather(*pending, return_exceptions=True)
    await asyncio.gather(*done, return_exceptions=True)
    _close_writer(writer)
    await _wait_closed(writer)


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
        self.udp_peer = None

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
        if self.udp_peer:
            self.loop.call_soon_threadsafe(self.udp_peer.close)

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
        udp_hint = ""
        if self.config.udp_enabled:
            try:
                self.udp_peer = UdpTunnelPeer(
                    self.config,
                    "host",
                    self.status_callback,
                    incoming_stream_callback=self._handle_udp_host_stream,
                )
                await self.udp_peer.start()
                self.udp_peer.start_registration_loop()
                udp_hint = "，已开启 UDP 打洞优先"
            except Exception as exc:
                self.status_callback(f"UDP 打洞初始化失败，将只使用中继：{exc}")
                self.udp_peer = None
        self.status_callback(
            f"房间 {self.config.room} 已连接到 {self.config.relay_host}:{self.config.relay_port}{udp_hint}，等待加入者。"
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
        if self.udp_peer:
            self.udp_peer.close()
        _close_writer(writer)
        await _wait_closed(writer)

    async def _handle_udp_host_stream(self, stream, address):
        try:
            local_reader, local_writer = await asyncio.open_connection(self.config.local_host, self.config.local_port)
            self.status_callback(f"加入者已通过 UDP 打洞接入：{address[0]}:{address[1]}")
            await _bridge_udp_stream(local_reader, local_writer, stream)
            self.status_callback("一条 UDP 打洞联机连接已断开。")
        except Exception as exc:
            self.status_callback(f"UDP 打洞房主转发失败：{exc}")
            await stream.close()

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
        if self.config.udp_enabled:
            try:
                self.udp_peer = UdpTunnelPeer(self.config, "guest", self.status_callback)
                await self.udp_peer.start()
                self.status_callback("UDP 打洞已准备，连接游戏时会优先尝试直连。")
            except Exception as exc:
                self.status_callback(f"UDP 打洞初始化失败，将只使用中继：{exc}")
                self.udp_peer = None
        server = await asyncio.start_server(self._handle_guest_local, self.config.local_host, self.config.local_port)
        self.local_server = server
        self.status_callback(
            f"加入隧道已就绪。在 Minecraft 中连接 {self.config.local_host}:{self.config.local_port}。"
        )
        async with server:
            await self.stop_event.wait()
        server.close()
        await server.wait_closed()
        if self.udp_peer:
            self.udp_peer.close()

    async def _handle_guest_local(self, local_reader, local_writer):
        try:
            if self.udp_peer:
                self.status_callback("正在尝试 UDP 打洞连接房主...")
                stream = await self.udp_peer.connect_stream()
                if stream:
                    self.status_callback("已通过 UDP 打洞接入房主。")
                    await _bridge_udp_stream(local_reader, local_writer, stream)
                    self.status_callback("一条本地 Minecraft UDP 打洞连接已断开。")
                    return
            self.status_callback("UDP 打洞不可用，正在回退到中继。")
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


def _clean_port(value):
    try:
        port = int(value)
    except (TypeError, ValueError):
        return 0
    if 1 <= port <= 65535:
        return port
    return 0


async def run_relay_server(host, port, rate_limit_bps=0):
    relay = P2PRelayServer(rate_limit_bps=rate_limit_bps)
    await relay.serve(host, port)


def main_server(argv=None):
    parser = argparse.ArgumentParser(description="McGo P2P relay server")
    parser.add_argument("--host", default="0.0.0.0", help="listen address")
    parser.add_argument("--port", type=int, default=10721, help="listen TCP port")
    parser.add_argument(
        "--relay-rate-limit-kbps",
        type=int,
        default=DEFAULT_RELAY_RATE_LIMIT_KBPS,
        help="per-direction relay bandwidth limit in KiB/s; use 0 to disable",
    )
    parser.add_argument("--log-level", default="INFO", help="logging level")
    args = parser.parse_args(argv)
    logging.basicConfig(
        level=getattr(logging, args.log_level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    rate_limit_bps = max(0, int(args.relay_rate_limit_kbps or 0)) * 1024
    asyncio.run(run_relay_server(args.host, args.port, rate_limit_bps=rate_limit_bps))
