import secrets
import socket
import struct


MAGIC_COOKIE = 0x2112A442
DEFAULT_STUN_SERVERS = (
    ("stun.miwifi.com", 3478),
    ("stun.l.google.com", 19302),
    ("stun.cloudflare.com", 3478),
    ("stun.ekiga.net", 3478),
)


class StunError(RuntimeError):
    pass


def _build_binding_request(transaction_id):
    return struct.pack("!HHI12s", 0x0001, 0, MAGIC_COOKIE, transaction_id)


def _parse_address_attribute(attr_type, attr_value, transaction_id):
    if len(attr_value) < 8:
        return None
    family = attr_value[1]
    if family != 0x01:
        return None

    port = struct.unpack("!H", attr_value[2:4])[0]
    address_bytes = attr_value[4:8]
    if attr_type == 0x0020:
        port ^= MAGIC_COOKIE >> 16
        cookie_bytes = struct.pack("!I", MAGIC_COOKIE)
        address_bytes = bytes(item ^ cookie_bytes[index] for index, item in enumerate(address_bytes))
    elif attr_type != 0x0001:
        return None

    return socket.inet_ntoa(address_bytes), port


def parse_stun_binding_response(data, transaction_id):
    if len(data) < 20:
        raise StunError("STUN 响应过短")

    message_type, message_length, magic_cookie, response_transaction_id = struct.unpack("!HHI12s", data[:20])
    if message_type != 0x0101:
        raise StunError("不是 STUN Binding Success 响应")
    if magic_cookie != MAGIC_COOKIE or response_transaction_id != transaction_id:
        raise StunError("STUN 事务 ID 不匹配")

    mapped_address = None
    end = min(len(data), 20 + message_length)
    offset = 20
    while offset + 4 <= end:
        attr_type, attr_length = struct.unpack("!HH", data[offset:offset + 4])
        offset += 4
        attr_value = data[offset:offset + attr_length]
        offset += attr_length + ((4 - attr_length % 4) % 4)
        if attr_type in (0x0001, 0x0020):
            address = _parse_address_attribute(attr_type, attr_value, transaction_id)
            if address:
                mapped_address = address
                if attr_type == 0x0020:
                    break

    if not mapped_address:
        raise StunError("STUN 响应中没有公网映射地址")
    return mapped_address


def _query_stun(sock, server, timeout):
    host, port = server
    last_error = None
    for family, _, _, _, address in socket.getaddrinfo(host, port, socket.AF_INET, socket.SOCK_DGRAM):
        try:
            sock.settimeout(timeout)
            sock.connect(address)
            transaction_id = secrets.token_bytes(12)
            sock.send(_build_binding_request(transaction_id))
            deadline = sock.gettimeout() or timeout
            while True:
                data = sock.recv(2048)
                try:
                    mapped_address = parse_stun_binding_response(data, transaction_id)
                    return {
                        "server": f"{host}:{port}",
                        "local": sock.getsockname(),
                        "mapped": mapped_address,
                    }
                except StunError as exc:
                    last_error = exc
                    if deadline <= 0:
                        raise
        except Exception as exc:
            last_error = exc
            continue
    raise StunError(str(last_error) if last_error else f"{host}:{port} 无响应")


def detect_nat_type(stun_servers=DEFAULT_STUN_SERVERS, timeout=2.5):
    samples = []
    errors = []
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(("0.0.0.0", 0))
        for server in stun_servers:
            try:
                samples.append(_query_stun(sock, server, timeout))
            except Exception as exc:
                errors.append(f"{server[0]}:{server[1]} - {exc}")

    if not samples:
        return {
            "nat_type": "UDP 不可达或被防火墙阻止",
            "quality": "blocked",
            "summary": "无法从 STUN 服务器取得公网映射地址。",
            "details": "\n".join(errors) if errors else "所有 STUN 探测均超时。",
            "samples": [],
        }

    first = samples[0]
    local_ip, local_port = first["local"]
    mapped_ip, mapped_port = first["mapped"]
    mapped_addresses = [sample["mapped"] for sample in samples]
    mapped_ips = {address[0] for address in mapped_addresses}
    mapped_ports = {address[1] for address in mapped_addresses}

    if local_ip == mapped_ip and local_port == mapped_port:
        nat_type = "开放网络或无 NAT"
        quality = "open"
        summary = "本机 UDP 地址与公网映射一致，联机连通性通常最好。"
    elif len(samples) == 1:
        nat_type = "NAT 后网络（类型待确认）"
        quality = "unknown"
        summary = "已取得公网映射，但只有一个 STUN 样本，无法判断映射是否会随目标变化。"
    elif len(mapped_ips) == 1 and len(mapped_ports) == 1:
        nat_type = "端口保持型 NAT"
        quality = "good"
        summary = "多个 STUN 服务器返回相同公网端口，通常适合 UDP 打洞。"
    elif len(mapped_ips) == 1:
        nat_type = "对称 NAT 或地址相关映射"
        quality = "strict"
        summary = "同一本地端口访问不同目标时公网端口会变化，直连成功率较低，建议使用中继。"
    else:
        nat_type = "多出口 NAT"
        quality = "strict"
        summary = "不同 STUN 服务器看到的公网 IP 不一致，建议使用中继联机。"

    detail_lines = [
        f"本地 UDP：{local_ip}:{local_port}",
        f"公网映射：{mapped_ip}:{mapped_port}",
    ]
    for sample in samples:
        sample_local = sample["local"]
        sample_mapped = sample["mapped"]
        detail_lines.append(
            f"{sample['server']} -> {sample_mapped[0]}:{sample_mapped[1]} "
            f"(本地 {sample_local[0]}:{sample_local[1]})"
        )
    if errors:
        detail_lines.append("失败节点：" + "；".join(errors))

    return {
        "nat_type": nat_type,
        "quality": quality,
        "summary": summary,
        "details": "\n".join(detail_lines),
        "samples": samples,
    }
