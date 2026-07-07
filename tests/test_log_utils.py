import logging

from log_utils import SafeRotatingFileHandler, consume_logging_notices


def test_rotating_log_permission_error_queues_notice(monkeypatch, tmp_path):
    log_path = tmp_path / "mcgo.log"
    log_path.write_text("x" * 32, encoding="utf-8")
    handler = SafeRotatingFileHandler(
        str(log_path),
        maxBytes=1,
        backupCount=1,
        encoding="utf-8",
    )

    monkeypatch.setattr(handler, "rotate", lambda *_args: (_ for _ in ()).throw(PermissionError("locked")))

    try:
        record = logging.LogRecord("mcgo-test", logging.INFO, __file__, 1, "hello", (), None)
        handler.emit(record)
    finally:
        handler.close()

    notices = consume_logging_notices()
    assert any("日志文件正在被占用" in notice for notice in notices)
    assert handler.rollover_blocked
