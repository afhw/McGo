import asyncio
import contextlib
import random

from p2p_tunnel import McgoP2PTunnel, P2PRelayServer, P2PTunnelConfig


async def _pick_port():
    for _ in range(50):
        port = random.randint(21000, 45000)
        try:
            server = await asyncio.start_server(lambda _reader, _writer: None, "127.0.0.1", port)
            server.close()
            await server.wait_closed()
            return port
        except OSError:
            continue
    raise RuntimeError("no free local port")


async def _echo_handler(reader, writer):
    try:
        while True:
            data = await reader.read(65536)
            if not data:
                break
            writer.write(data[::-1])
            await writer.drain()
    finally:
        writer.close()
        with contextlib.suppress(Exception):
            await writer.wait_closed()


async def _wait_for(predicate, timeout=5):
    deadline = asyncio.get_running_loop().time() + timeout
    while asyncio.get_running_loop().time() < deadline:
        if predicate():
            return True
        await asyncio.sleep(0.05)
    return False


def test_udp_hole_punch_tunnel_carries_tcp_bytes():
    async def run():
        relay_port = await _pick_port()
        join_port = await _pick_port()
        echo_server = await asyncio.start_server(_echo_handler, "127.0.0.1", 0)
        echo_port = echo_server.sockets[0].getsockname()[1]

        relay = P2PRelayServer(rate_limit_bps=0)
        relay_task = asyncio.create_task(relay.serve("127.0.0.1", relay_port))
        await asyncio.sleep(0.2)

        host = McgoP2PTunnel(
            P2PTunnelConfig(
                mode="host",
                relay_host="127.0.0.1",
                relay_port=relay_port,
                room="TESTROOM",
                secret="secret",
                local_host="127.0.0.1",
                local_port=echo_port,
            )
        )
        guest = McgoP2PTunnel(
            P2PTunnelConfig(
                mode="join",
                relay_host="127.0.0.1",
                relay_port=relay_port,
                room="TESTROOM",
                secret="secret",
                local_host="127.0.0.1",
                local_port=join_port,
            )
        )

        try:
            host.start()
            await asyncio.sleep(0.4)
            guest.start()
            assert await _wait_for(guest.is_running)
            await asyncio.sleep(0.7)

            reader, writer = await asyncio.open_connection("127.0.0.1", join_port)
            payload = b"hello-through-udp"
            writer.write(payload)
            await writer.drain()
            data = await asyncio.wait_for(reader.readexactly(len(payload)), timeout=10)
            assert data == payload[::-1]
            writer.close()
            await writer.wait_closed()
        finally:
            host.stop()
            guest.stop()
            echo_server.close()
            await echo_server.wait_closed()
            relay_task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await relay_task

    asyncio.run(run())
