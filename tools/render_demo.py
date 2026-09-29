"""Render an explicitly labeled replay of actual Windows E2E evidence to MP4.

pip install Pillow imageio-ffmpeg
python tools/render_demo.py artifacts/evidence/demo-events.json artifacts/demo.mp4
The video uses real recorded commands/results; it never invents missing steps.
"""
import argparse
import json
from pathlib import Path
import subprocess
import textwrap

from PIL import Image, ImageDraw, ImageFont
import imageio_ffmpeg


def render(source, destination):
    events = json.loads(Path(source).read_text(encoding="utf-8-sig"))
    fonts = ["/System/Library/Fonts/Menlo.ttc", "C:/Windows/Fonts/consola.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf"]
    font_path = next((x for x in fonts if Path(x).exists()), None)
    font = ImageFont.truetype(font_path, 21) if font_path else ImageFont.load_default(size=21)
    title_font = ImageFont.truetype(font_path, 28) if font_path else ImageFont.load_default(size=28)
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    process = subprocess.Popen([imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-f", "rawvideo", "-vcodec", "rawvideo", "-pix_fmt", "rgb24", "-s", "1440x900", "-r", "2", "-i", "-", "-an", "-c:v", "libx264", "-preset", "fast", "-crf", "23", "-pix_fmt", "yuv420p", str(destination)], stdin=subprocess.PIPE, stderr=subprocess.DEVNULL)
    for index, event in enumerate(events):
        output = json.dumps(event["result"], indent=2, ensure_ascii=False)
        # Output volume test records byte counts instead of giant output. Other
        # long results are paginated, never replaced with fabricated summaries.
        lines = []
        for line in ("$ " + event["command"] + "\n\n" + output).splitlines():
            lines.extend(textwrap.wrap(line, width=102, replace_whitespace=False, drop_whitespace=False) or [""])
        pages = [lines[i:i+24] for i in range(0, len(lines), 24)]
        for page_no, page in enumerate(pages):
            frame = Image.new("RGB", (1440, 900), "#0c1220")
            draw = ImageDraw.Draw(frame)
            draw.text((42, 30), "SQUASH  /  Windows execution demo", font=title_font, fill="#f1f5f9")
            draw.text((42, 82), "Replay of recorded Windows CI results · no simulated execution", font=font, fill="#94a3b8")
            draw.line((42, 125, 1398, 125), fill="#27354c", width=2)
            draw.text((42, 148), f"{index+1:02}  {event['title']}", font=title_font, fill="#5eead4")
            for i, line in enumerate(page):
                draw.text((42, 210 + i*25), line, font=font, fill="#cbd5e1" if i > 0 or page_no else "#fbbf24")
            footer = f"Captured at +{event['elapsedMs']/1000:.1f}s   |   Page {page_no+1}/{len(pages)}   |   Source: demo-events.json"
            draw.text((42, 850), footer, font=font, fill="#94a3b8")
            duration = max(8, min(18, len(page) // 2))
            for _ in range(duration * 2):
                process.stdin.write(frame.tobytes())
    process.stdin.close()
    if process.wait() != 0:
        raise RuntimeError("Video encoding failed")
    print(destination)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source")
    parser.add_argument("destination")
    args = parser.parse_args()
    render(args.source, args.destination)
