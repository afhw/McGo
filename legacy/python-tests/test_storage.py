import json

from secure_store import hydrate_accounts, redact_accounts
from storage_utils import save_json_atomic


def test_accounts_are_redacted_and_hydrated():
    accounts = [{"id": "1", "type": "microsoft", "refresh_token": "secret"}]
    redacted = redact_accounts(accounts)
    assert "refresh_token" not in redacted[0]
    assert redacted[0]["secure_tokens"]["refresh_token"] != "secret"
    assert hydrate_accounts(redacted)[0]["refresh_token"] == "secret"


def test_save_json_atomic_writes_valid_json(tmp_path):
    target = tmp_path / "settings.json"
    save_json_atomic(str(target), {"ok": True})
    assert json.loads(target.read_text(encoding="utf-8")) == {"ok": True}
