#!/usr/bin/env python3
"""
MicCast 产品图标生成器。

设计：圆角方形渐变底（品牌蓝 → 青绿）+ 白色麦克风 + 向右扩散的声波弧，
表达「把声音渡到电脑上」。图形刻意保持极简，保证 16px 下仍可辨识。

用法：
    python tools/make-icon.py

产物：
    AppScope/resources/base/media/app_icon.png       512x512
    entry/src/main/resources/base/media/icon.png     512x512
    windows/MicCastReceiver/app.ico                  16/24/32/48/64/128/256
    docs/icon-preview.png                            预览拼图
"""

from __future__ import annotations

import os
import math
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

BRAND_FROM = (47, 107, 255)    # #2F6BFF
BRAND_TO = (18, 165, 148)     # #12A594
CANVAS = 1024

ss = 4                         # 超采样倍数，画完缩小得到平滑边缘


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def make_background(size: int, radius_ratio: float = 0.225) -> Image.Image:
    """对角渐变 + 圆角方形遮罩。"""
    grad = Image.new("RGB", (size, size))
    px = grad.load()
    for y in range(size):
        for x in range(size):
            # 45° 对角方向：左上到右下
            t = (x + y) / (2.0 * (size - 1))
            px[x, y] = lerp(BRAND_FROM, BRAND_TO, t)

    mask = Image.new("L", (size, size), 0)
    md = ImageDraw.Draw(mask)
    r = int(size * radius_ratio)
    md.rounded_rectangle([0, 0, size - 1, size - 1], radius=r, fill=255)

    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(grad, (0, 0), mask)
    return out


def draw_mic(d: ImageDraw.ImageDraw, size: int) -> None:
    """白色麦克风：胶囊 + U 形托 + 立杆 + 底座。"""
    s = size / 1024.0

    cx = int(392 * s)
    cap_top = int(240 * s)
    cap_bottom = int(520 * s)
    cap_w = int(190 * s)

    white = (255, 255, 255, 255)

    # 胶囊体
    d.rounded_rectangle(
        [cx - cap_w // 2, cap_top, cx + cap_w // 2, cap_bottom],
        radius=cap_w // 2,
        fill=white,
    )

    # U 形托：半径要让弧底明显低于胶囊底，才能看出是「托住」而不是侧面伸出
    ring_w = int(44 * s)
    ring_r = int(155 * s)
    ring_cy = int(375 * s)
    d.arc(
        [cx - ring_r, ring_cy - ring_r, cx + ring_r, ring_cy + ring_r],
        start=25, end=155,
        fill=white, width=ring_w,
    )

    # 立杆
    stem_top = ring_cy + ring_r - int(8 * s)
    stem_bottom = int(700 * s)
    d.rounded_rectangle(
        [cx - ring_w // 2, stem_top, cx + ring_w // 2, stem_bottom],
        radius=ring_w // 2,
        fill=white,
    )

    # 底座
    base_w = int(210 * s)
    base_y = int(715 * s)
    d.rounded_rectangle(
        [cx - base_w // 2, base_y, cx + base_w // 2, base_y + ring_w],
        radius=ring_w // 2,
        fill=white,
    )


def draw_waves(base: Image.Image, size: int) -> None:
    """右侧三道声波弧，越远越淡，表达「投射出去」。"""
    s = size / 1024.0
    cx = int(392 * s)
    cy = int(400 * s)

    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    radii = [250, 335, 420]
    alphas = [240, 180, 115]
    width = int(46 * s)

    for r_px, alpha in zip(radii, alphas):
        r = int(r_px * s)
        d.arc(
            [cx - r, cy - r, cx + r, cy + r],
            start=-48, end=48,
            fill=(255, 255, 255, alpha), width=width,
        )

    base.alpha_composite(layer)


def make_icon(size: int = CANVAS) -> Image.Image:
    """
    先在大画布上画内容，再自动裁剪到实际边界并居中贴回。
    这样不用手工调每个元素的坐标，留白天然均衡。
    """
    big = size * ss
    canvas = Image.new("RGBA", (big, big), (0, 0, 0, 0))

    layer = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    draw_mic(d, big)
    canvas.alpha_composite(layer)
    draw_waves(canvas, big)

    bbox = canvas.getbbox()
    if bbox is None:
        return make_background(size)

    content = canvas.crop(bbox)

    # 内容占画布 74%，四周留 13% 呼吸空间
    target = int(big * 0.74)
    scale = target / max(content.width, content.height)
    new_w = max(1, int(content.width * scale))
    new_h = max(1, int(content.height * scale))
    content = content.resize((new_w, new_h), Image.LANCZOS)

    img = make_background(big)
    img.alpha_composite(content, ((big - new_w) // 2, (big - new_h) // 2))
    return img.resize((size, size), Image.LANCZOS)


def save_png(img: Image.Image, path: str, size: int) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.resize((size, size), Image.LANCZOS).save(path, "PNG", optimize=True)
    print("  PNG %-58s %dx%d" % (os.path.relpath(path, ROOT), size, size))


def save_ico(img: Image.Image, path: str) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    # 16/24 太小，细节看不清，单独用「加粗版」重绘更清晰
    frames = []
    for w, h in sizes:
        if w <= 32:
            frames.append(make_small_variant(w))
        else:
            frames.append(img.resize((w, h), Image.LANCZOS))
    frames[-1].save(path, format="ICO", sizes=sizes)
    print("  ICO %-58s %s" % (os.path.relpath(path, ROOT), ",".join(str(w) for w, _ in sizes)))


def make_small_variant(size: int) -> Image.Image:
    """
    小尺寸专用：声波弧全部去掉，麦克风加粗并放大，
    否则 16px 下会糊成一团。
    """
    big = size * 8
    img = make_background(big, radius_ratio=0.24)

    s = big / 1024.0
    layer = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    white = (255, 255, 255, 255)

    cx = big // 2
    cap_w = int(300 * s)
    cap_top = int(210 * s)
    cap_bottom = int(650 * s)

    d.rounded_rectangle(
        [cx - cap_w // 2, cap_top, cx + cap_w // 2, cap_bottom],
        radius=cap_w // 2, fill=white,
    )

    ring_w = int(72 * s)
    ring_r = int(220 * s)
    ring_top = int(420 * s)
    d.arc(
        [cx - ring_r, ring_top - ring_r, cx + ring_r, ring_top + ring_r],
        start=15, end=165, fill=white, width=ring_w,
    )

    stem_top = ring_top + ring_r - int(10 * s)
    stem_bottom = int(850 * s)
    d.rounded_rectangle(
        [cx - ring_w // 2, stem_top, cx + ring_w // 2, stem_bottom],
        radius=ring_w // 2, fill=white,
    )

    img.alpha_composite(layer)
    return img.resize((size, size), Image.LANCZOS)


def make_preview(img: Image.Image, path: str) -> None:
    """预览图：多种尺寸并排，方便肉眼检查小图是否还认得出。"""
    sizes = [256, 128, 64, 48, 32, 16]
    pad = 24
    total_w = sum(sizes) + pad * (len(sizes) + 1)
    total_h = 256 + pad * 2
    canvas = Image.new("RGBA", (total_w, total_h), (24, 32, 48, 255))

    x = pad
    for s in sizes:
        thumb = img.resize((s, s), Image.LANCZOS)
        y = pad + (256 - s) // 2
        canvas.alpha_composite(thumb, (x, y))
        x += s + pad

    os.makedirs(os.path.dirname(path), exist_ok=True)
    canvas.save(path, "PNG")
    print("  PNG %-58s %dx%d" % (os.path.relpath(path, ROOT), total_w, total_h))


def make_mark(size: int = CANVAS) -> Image.Image:
    """
    透明底的纯白标记，用于放在渐变/彩色背景上（比如手机端 Hero）。
    带渐变底的完整图标贴到渐变底上会糊成一片，所以单独出这一版。
    """
    big = size * ss
    canvas = Image.new("RGBA", (big, big), (0, 0, 0, 0))

    layer = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    draw_mic(d, big)
    canvas.alpha_composite(layer)
    draw_waves(canvas, big)

    bbox = canvas.getbbox()
    if bbox is None:
        return canvas.resize((size, size), Image.LANCZOS)

    content = canvas.crop(bbox)
    target = int(big * 0.94)
    scale = target / max(content.width, content.height)
    new_w = max(1, int(content.width * scale))
    new_h = max(1, int(content.height * scale))
    content = content.resize((new_w, new_h), Image.LANCZOS)

    out = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    out.alpha_composite(content, ((big - new_w) // 2, (big - new_h) // 2))
    return out.resize((size, size), Image.LANCZOS)


def main() -> None:
    print("生成 MicCast 图标…")
    master = make_icon(CANVAS)

    save_png(master, os.path.join(ROOT, "AppScope/resources/base/media/app_icon.png"), 512)
    save_png(master, os.path.join(ROOT, "entry/src/main/resources/base/media/icon.png"), 512)
    save_png(make_mark(CANVAS), os.path.join(ROOT, "entry/src/main/resources/base/media/brand_mark.png"), 256)
    save_ico(master, os.path.join(ROOT, "windows/MicCastReceiver/app.ico"))
    make_preview(master, os.path.join(ROOT, "docs/icon-preview.png"))
    print("完成。")


if __name__ == "__main__":
    main()
