import app_settings


def test_concise_download_error_extracts_404_filename():
    message = "下载失败 404 https://example.com/libraries/demo-1.0.jar；官方源 404 https://backup/demo-1.0.jar"

    result = app_settings.concise_download_error(message)

    assert result.startswith("demo-1.0.jar 在当前镜像源和官方源均不存在")


def test_read_download_options_clamps_numeric_values():
    previous = {section: dict(app_settings.config.items(section)) for section in app_settings.config.sections()}
    try:
        app_settings.config.clear()
        app_settings.config.add_section("DOWNLOAD")
        app_settings.config["DOWNLOAD"]["max_core_threads"] = "0"
        app_settings.config["DOWNLOAD"]["max_asset_threads"] = "-3"
        app_settings.config["DOWNLOAD"]["speed_limit_kbps"] = "-1"
        app_settings.config["DOWNLOAD"]["cache_strategy"] = "force"

        assert app_settings.read_download_options() == {
            "max_core_concurrency": 1,
            "max_asset_concurrency": 1,
            "speed_limit_kbps": 0,
            "cache_strategy": "force",
        }
    finally:
        app_settings.config.clear()
        for section, values in previous.items():
            app_settings.config.add_section(section)
            for key, value in values.items():
                app_settings.config[section][key] = value
