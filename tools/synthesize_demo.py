"""Render UnoPlay's original 16-beat FM groove. Requires Python 3 and ffmpeg."""
from array import array
from math import exp, pi, sin, tanh
from pathlib import Path
import random
import subprocess
import sys

rate = 44100
beat = 60 / 110
rng = random.Random(1997)
bass = (38, 38, 41, 45, 36, 36, 41, 43, 34, 34, 38, 41, 36, 36, 43, 40)
lead = (62, 69, 72, 69, 65, 69, 74, 72, 60, 67, 72, 67, 64, 67, 72, 76,
        58, 65, 69, 65, 62, 65, 70, 69, 60, 67, 70, 67, 64, 67, 72, 69)
samples = array("h")
for i in range(int(rate * beat * 16)):
    t = i / rate
    step = int(t / beat)
    phase = t - step * beat
    half_step = int(2 * t / beat)
    arp_time = t - half_step * beat / 2
    hz = lambda midi: 440 * 2 ** ((midi - 69) / 12)
    root = 2 * pi * hz(bass[step]) * phase
    pluck = 2 * pi * hz(lead[half_step]) * arp_time
    bass_note = .30 * sin(root + .8 * sin(2 * root) * exp(-10 * phase)) * exp(-4 * phase)
    pulse = .36 * sin(2 * pi * 48 * phase + 16 * (1 - exp(-36 * phase))) * exp(-15 * phase)
    voice = .15 * sin(pluck + 1.6 * sin(2 * pluck) * exp(-19 * arp_time)) * exp(-9 * arp_time)
    noise = rng.uniform(-1, 1)
    hats = .075 * noise * exp(-76 * arp_time)
    snare = (.14 * noise + .045 * sin(2 * pi * 170 * phase)) * exp(-23 * phase) if step % 2 else 0
    fade = min(1, t * 80, (beat * 16 - t) * 5)
    for pan in (.87, 1.12) if half_step % 2 else (1.12, .87):
        samples.append(int(27000 * fade * tanh(bass_note + pulse + voice * pan + hats + snare)))
if sys.byteorder != "little":
    samples.byteswap()
dest = Path(__file__).resolve().parents[1] / "src/UnoPlay.App/Assets/Media/first-light.mp3"
subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "s16le", "-ar", str(rate),
                "-ac", "2", "-i", "pipe:0", "-codec:a", "libmp3lame", "-b:a", "128k",
                "-metadata", "artist=UnoPlay", "-metadata", "title=First Light", str(dest)],
               input=samples.tobytes(), check=True)
