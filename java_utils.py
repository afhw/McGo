import os
import subprocess


def find_java_paths():
    """搜索并返回找到的 Java 可执行文件路径列表。"""
    java_paths = []

    # --- 在常见位置搜索 ---
    common_paths = [
        # Windows
        r"C:\Program Files\Java",
        r"C:\Program Files (x86)\Java",
        # Linux/macOS
        "/usr/bin",
        "/usr/local/bin",
    ]
    for path in common_paths:
        java_paths.extend(find_java_in_directory(path))

    # --- 检查环境变量 ---
    java_home = os.environ.get("JAVA_HOME")
    if java_home:
        java_paths.append(os.path.join(java_home, "bin", "java"))

    return java_paths


def find_java_in_directory(directory):
    """在指定目录及其子目录中搜索 Java 可执行文件。"""
    java_paths = []
    for root, _, files in os.walk(directory):
        for file in files:
            if file in ("java.exe", "java"):
                java_paths.append(os.path.join(root, file))
    return java_paths


def get_java_version(java_path):
    """获取指定 Java 可执行文件的版本信息。"""
    try:
        result = subprocess.run(
            [java_path, "-version"], capture_output=True, text=True
        )
        if result.returncode == 0:
            return result.stderr  # 版本信息通常在 stderr 中
        else:
            return None
    except FileNotFoundError:
        return None