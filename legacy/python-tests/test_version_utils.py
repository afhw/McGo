from version_utils import launch_options_for_version


def test_launch_options_append_quick_play_singleplayer(tmp_path):
    settings = {
        "1.20.1": {
            "game_args": "--fullscreen",
            "quick_play_singleplayer": "My World",
        }
    }

    options = launch_options_for_version(str(tmp_path), settings, "1.20.1")

    assert options["extra_game_args"] == [
        "--fullscreen",
        "--quickPlaySingleplayer",
        "My World",
    ]


def test_launch_options_skip_empty_quick_play(tmp_path):
    settings = {"1.20.1": {"quick_play_singleplayer": ""}}

    options = launch_options_for_version(str(tmp_path), settings, "1.20.1")

    assert "--quickPlaySingleplayer" not in options["extra_game_args"]
