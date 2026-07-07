import asyncio
import contextlib
import json
import random

from capsule_utils import export_capsule, file_sha256, import_capsule
from p2p_files import CapsuleFileHost, pull_capsule_files
from p2p_tunnel import P2PRelayServer, P2PTunnelConfig


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


def write_version(game_dir, version_id):
    version_dir = game_dir / "versions" / version_id
    version_dir.mkdir(parents=True)
    (version_dir / f"{version_id}.json").write_text(
        json.dumps({"id": version_id, "mainClass": "net.minecraft.client.main.Main"}),
        encoding="utf-8",
    )
    return version_dir


def test_capsule_files_pull_missing_payload_by_manifest(tmp_path):
    async def run():
        relay_port = await _pick_port()
        room = "FILEPACK"
        relay = P2PRelayServer(rate_limit_bps=0)
        relay_task = asyncio.create_task(relay.serve("127.0.0.1", relay_port))
        await asyncio.sleep(0.2)

        host_game = tmp_path / "host"
        guest_game = tmp_path / "guest"
        version_id = "fabric-loader-0.16.0-1.20.1"
        host_runtime = write_version(host_game, version_id)
        write_version(guest_game, version_id)
        (host_runtime / "mods").mkdir()
        (host_runtime / "config").mkdir()
        (host_runtime / "mods" / "private.jar").write_bytes(b"private mod over p2p")
        (host_runtime / "config" / "private.json").write_text('{"p2p":true}', encoding="utf-8")

        capsule_path = tmp_path / "party.mcgo-capsule"
        host_settings = {version_id: {"alias": "Party Pack", "use_isolated_directory": True}}
        export_capsule(
            str(host_game),
            host_settings,
            version_id,
            str(capsule_path),
            p2p_info={"room": room, "relay_host": "127.0.0.1", "relay_port": relay_port},
        )
        imported_settings = {}
        imported = import_capsule(
            str(capsule_path),
            str(guest_game),
            imported_settings,
            target_version_id="ImportedParty",
        )

        host = CapsuleFileHost(
            str(host_runtime),
            P2PTunnelConfig(
                mode="host",
                relay_host="127.0.0.1",
                relay_port=relay_port,
                room=room,
                udp_enabled=False,
            ),
        )
        host.start()
        await asyncio.sleep(0.6)

        try:
            result = await asyncio.to_thread(
                pull_capsule_files,
                imported["runtime_dir"],
                imported["files"],
                P2PTunnelConfig(
                    mode="join",
                    relay_host="127.0.0.1",
                    relay_port=relay_port,
                    room=room,
                    udp_enabled=False,
                ),
            )
            assert result["downloaded"] == 2
            imported_runtime = guest_game / "versions" / "ImportedParty"
            private_mod = imported_runtime / "mods" / "private.jar"
            private_config = imported_runtime / "config" / "private.json"
            assert private_mod.read_bytes() == b"private mod over p2p"
            assert private_config.read_text(encoding="utf-8") == '{"p2p":true}'
            manifest_by_path = {item["path"]: item for item in imported["files"]}
            assert file_sha256(private_mod) == manifest_by_path["mods/private.jar"]["sha256"]
            assert file_sha256(private_config) == manifest_by_path["config/private.json"]["sha256"]
        finally:
            host.stop()
            if host.thread:
                host.thread.join(timeout=3)
            relay_task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await relay_task

    asyncio.run(run())
