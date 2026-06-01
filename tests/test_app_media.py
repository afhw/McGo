from app_media import BackgroundMusicController


def test_background_music_controller_is_lazy_without_qt_player():
    controller = BackgroundMusicController()

    controller.stop()
    controller.pause()

    assert controller.is_created is False
