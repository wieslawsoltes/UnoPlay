"""Run under xvfb-run with a published linux-x64 UnoPlay.App executable.

Requires ffmpeg, ImageMagick, xdotool, Pillow, libvlc-dev, vlc-plugin-base,
vlc-plugin-video-output, an audio device (or virtual PulseAudio sink), and Uno X11 dependencies.
"""
from argparse import ArgumentParser
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Thread
import os
import subprocess
import tempfile
import time

from PIL import Image


def run(*args):
    return subprocess.check_output(args, timeout=30, text=True).strip()


def capture(window, path):
    run("import", "-window", window, str(path))
    with Image.open(path) as frame:
        return frame.convert("RGB")


def pixels(frame):
    return frame.get_flattened_data() if hasattr(frame, "get_flattened_data") else frame.getdata()


def saturated_pixels(frame):
    return sum(max(c) - min(c) > 135 and max(c) > 190 for c in pixels(frame))


def main():
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("--screenshots", type=Path)
    args = parser.parse_args()

    with tempfile.TemporaryDirectory(prefix="unoplay-x11-") as tmp:
        work = Path(tmp)
        shots = args.screenshots or work
        shots.mkdir(parents=True, exist_ok=True)
        run("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            "testsrc2=size=240x160:rate=15:duration=20", "-c:v", "libx264", "-pix_fmt",
            "yuv420p", "-movflags", "+faststart", str(work / "video.mp4"))
        config = work / "config"
        (config / "vlc").mkdir(parents=True)
        (config / "vlc" / "vlcrc").write_text("[core]\nvout=xcb_x11\n[avcodec]\navcodec-hw=none\n")
        env = os.environ.copy()
        for key in tuple(env):
            if key.lower() in {"http_proxy", "https_proxy", "all_proxy"}:
                del env[key]  # Fixture traffic is confined to the loopback address.
        env.update(LIBGL_ALWAYS_SOFTWARE="1", XDG_CONFIG_HOME=str(config),
                   XDG_DATA_HOME=str(work / "data"), XDG_STATE_HOME=str(work / "state"))

        handler = partial(SimpleHTTPRequestHandler, directory=tmp)
        with ThreadingHTTPServer(("127.0.0.1", 0), handler) as server:
            Thread(target=server.serve_forever, daemon=True).start()
            app = subprocess.Popen([str(args.executable.resolve())], env=env)
            try:
                window = run("xdotool", "search", "--sync", "--onlyvisible", "--name", "^UnoPlay$").splitlines()[0]
                run("xdotool", "windowfocus", window)
                time.sleep(1.5)
                idle = capture(window, shots / "idle.png")
                run("xdotool", "key", "x")
                run("xdotool", "mousemove", "--window", window, "4", "4")
                deadline = time.monotonic() + 7
                # Count the bright green/yellow analyzer bars. This also
                # ensures a real decoded signal reached the skinned display.
                idle_green = sum(g > 175 and g > r * 1.3 and b < 65 for r, g, b in pixels(idle))
                while time.monotonic() < deadline:
                    first = capture(window, shots / "playing.png")
                    lit = sum(g > 175 and g > r * 1.3 and b < 65 for r, g, b in pixels(first))
                    if lit > idle_green + 30:
                        break
                    time.sleep(.2)
                else:
                    raise AssertionError("Bundled MP3 did not produce a visible spectrum")
                run("xdotool", "key", "ctrl+l")
                time.sleep(.5)
                run("xdotool", "type", "--delay", "5", f"http://127.0.0.1:{server.server_port}/video.mp4")
                run("xdotool", "key", "Return")
                threshold = saturated_pixels(idle) + 1500
                deadline = time.monotonic() + 12
                while time.monotonic() < deadline:
                    time.sleep(.3)
                    if saturated_pixels(capture(window, shots / "video.png")) > threshold:
                        break
                else:
                    raise AssertionError("No decoded color bars on the native video surface")
                assert app.poll() is None
                print("Uno desktop X11 launch, MP3 transport, Open URL and native MP4 video passed.")
            finally:
                server.shutdown()
                app.terminate()
                try:
                    app.wait(timeout=4)
                except subprocess.TimeoutExpired:
                    app.kill()
                    app.wait()


if __name__ == "__main__":
    main()
