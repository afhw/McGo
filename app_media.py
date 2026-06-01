import os


class BackgroundMusicController:
    def __init__(self, parent=None):
        self.parent = parent
        self.player = None
        self.audio_output = None
        self.url_type = None

    @property
    def is_created(self):
        return self.player is not None

    def ensure_player(self):
        if self.player is None or self.audio_output is None:
            from PyQt6.QtCore import QUrl
            from PyQt6.QtMultimedia import QAudioOutput, QMediaPlayer

            self.url_type = QUrl
            self.player = QMediaPlayer(self.parent)
            self.audio_output = QAudioOutput(self.parent)
            self.player.setAudioOutput(self.audio_output)
        return self.player

    def play_file(self, path, volume):
        player = self.ensure_player()
        self.audio_output.setVolume(max(0, min(100, volume)) / 100)
        player.setSource(self.url_type.fromLocalFile(os.path.abspath(path)))
        player.play()

    def stop(self):
        if self.player is not None:
            self.player.stop()

    def pause(self):
        if self.player is not None:
            self.player.pause()
