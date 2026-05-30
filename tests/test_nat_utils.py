import struct

from nat_utils import MAGIC_COOKIE, parse_stun_binding_response


def test_parse_xor_mapped_address_response():
    transaction_id = bytes(range(1, 13))
    mapped_ip = "203.0.113.9"
    mapped_port = 54320
    ip_bytes = bytes([203, 0, 113, 9])
    cookie_bytes = struct.pack("!I", MAGIC_COOKIE)
    xored_ip = bytes(value ^ cookie_bytes[index] for index, value in enumerate(ip_bytes))
    xored_port = mapped_port ^ (MAGIC_COOKIE >> 16)
    attr_value = b"\x00\x01" + struct.pack("!H", xored_port) + xored_ip
    attr = struct.pack("!HH", 0x0020, len(attr_value)) + attr_value
    response = struct.pack("!HHI12s", 0x0101, len(attr), MAGIC_COOKIE, transaction_id) + attr

    assert parse_stun_binding_response(response, transaction_id) == (mapped_ip, mapped_port)
