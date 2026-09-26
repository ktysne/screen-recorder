"""assets の透過素材から exe とウィンドウに使う app.ico を作る。

使い方: python tools/make-app-icon.py (Pillow が必要)
"""
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "assets" / "screen-recorder-a1-transparent.png"
OUTPUT = ROOT / "src" / "ScreenRecorder.App" / "app.ico"
SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
# 図柄を正方形の中央に置き、縁に小さな余白を残す。
MARGIN_RATIO = 0.03


def square_canvas(image: Image.Image) -> Image.Image:
    cropped = image.crop(image.getbbox())
    side = round(max(cropped.size) * (1 + 2 * MARGIN_RATIO))
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(cropped, ((side - cropped.width) // 2, (side - cropped.height) // 2), cropped)
    return canvas


def main() -> None:
    master = square_canvas(Image.open(SOURCE).convert("RGBA"))
    # 各サイズを元の大きさから直接縮小する。Pillow に任せると最大の 1 枚から段階的に縮めて細い線がぼやける。
    frames = [master.resize((size, size), Image.LANCZOS) for size in SIZES]
    frames[-1].save(OUTPUT, format="ICO", sizes=[(size, size) for size in SIZES], append_images=frames[:-1])


if __name__ == "__main__":
    main()
