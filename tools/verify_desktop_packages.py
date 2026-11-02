"""Check self-contained launchers and native architectures in the release archives."""
from pathlib import Path
import struct
import sys
import tarfile
import zipfile

PREFIX = "UnoPlay/"
PAYLOAD = {"UnoPlay.App.dll", "UnoPlay.App.runtimeconfig.json", "media/first-light.mp3"}
ARCHIVES = {
    "UnoPlay-win-x64.zip": ("UnoPlay.App.exe", "coreclr.dll"),
    "UnoPlay-linux-x64.tar.gz": ("UnoPlay.App", "libcoreclr.so"),
    "UnoPlay-osx-arm64.tar.gz": ("UnoPlay.App", "libcoreclr.dylib"),
}


def verify(folder):
    if {path.name for path in folder.iterdir()} != set(ARCHIVES):
        raise ValueError("Expected exactly one Windows, Linux and macOS archive")
    for name, (launcher, runtime) in ARCHIVES.items():
        path = folder / name
        required = {PREFIX + file for file in PAYLOAD | {runtime, launcher}}
        if name.endswith(".zip"):
            with zipfile.ZipFile(path) as archive:
                files = {info.filename.replace("\\", "/"): info for info in archive.infolist() if info.file_size}
                if not required <= files.keys():
                    raise ValueError(f"{name} lacks {required - files.keys()}")
                with archive.open(files[PREFIX + launcher]) as app:
                    header = app.read(64)
                    if header[:2] != b"MZ":
                        raise ValueError(f"Invalid PE file in {name}")
                    app.seek(struct.unpack_from("<I", header, 60)[0])
                    if app.read(6) != b"PE\0\0\x64\x86":
                        raise ValueError(f"Expected x64 PE launcher in {name}")
        else:
            with tarfile.open(path, "r:gz") as archive:
                for item in archive:
                    if not item.isfile() or not item.size or item.name not in required:
                        continue
                    required.remove(item.name)
                    if item.name == PREFIX + launcher:
                        if not item.mode & 0o111:
                            raise ValueError(f"Missing executable permission in {name}")
                        header = archive.extractfile(item).read(20)
                        if "linux" in name:
                            valid = header[:6] == b"\x7fELF\x02\x01" and header[18:20] == b"\x3e\x00"
                        else:
                            valid = header[:8] == b"\xcf\xfa\xed\xfe\x0c\x00\x00\x01"
                        if not valid:
                            raise ValueError(f"Unexpected native launcher architecture in {name}")
                if required:
                    raise ValueError(f"{name} lacks {required}")
        print(f"Verified {name} ({path.stat().st_size:,} bytes)")


if __name__ == "__main__":
    verify(Path(sys.argv[1]))
