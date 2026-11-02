"""Emit a minimal classic skin with one recognizable BGR color for browser upload tests."""
import io
import struct
import sys
import zipfile

width, height = 275, 116
row = bytes((0x3a, 0x88, 0xdd)) * width
row += bytes(-len(row) % 4)
pixels = row * height
bmp = (b"BM" + struct.pack("<IHHI", 54 + len(pixels), 0, 0, 54) +
       struct.pack("<IiiHHIIiiII", 40, width, height, 1, 24, 0, len(pixels), 0, 0, 0, 0) + pixels)
output = io.BytesIO()
with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
    archive.writestr("Nested/MAIN.BMP", bmp)
sys.stdout.buffer.write(output.getvalue())
