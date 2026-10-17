# -*- coding: utf-8 -*-
"""开关两套图标：
on  = 直接复制现有 monitor.ico（保留手工简化的 16/32 版）
off = 从 monitor.ico 逐尺寸取原画，去色压暗（小尺寸画斜杠会糊，所以不画）
"""
import io, os, shutil, struct
from PIL import Image, ImageDraw

DIR = os.path.dirname(os.path.abspath(__file__))   # 按脚本自身位置，拷到哪都能重新生成
BASE = os.path.join(DIR, "monitor.ico")


def to_off(im, mul=0.55):
    im = im.convert("RGBA")
    a = im.split()[3]
    gray = im.convert("RGB").convert("L").convert("RGB")
    dark = gray.point(lambda v: int(v * mul))
    return Image.merge("RGBA", (*dark.split(), a))


def pack(path, frames):
    """手写 ICO：条目里放 PNG（Vista 之后的资源管理器都认）。"""
    entries = []
    for s, im in frames:
        bio = io.BytesIO()
        im.save(bio, "PNG", optimize=True)
        entries.append((s, bio.getvalue()))
    hdr = struct.pack("<HHH", 0, 1, len(entries))
    off = 6 + 16 * len(entries)
    dirb = body = b""
    for s, data in entries:
        b = 0 if s >= 256 else s
        dirb += struct.pack("<BBBBHHII", b, b, 0, 0, 1, 32, len(data), off)
        off += len(data)
        body += data
    with open(path, "wb") as f:
        f.write(hdr + dirb + body)
    print("wrote", path, [(s, len(d)) for s, d in entries])


ico = Image.open(BASE)
avail = sorted(ico.ico.sizes(), key=lambda t: -t[0])
print("monitor.ico sizes:", avail)
SIZES = [w for w, h in avail]

shutil.copyfile(BASE, os.path.join(DIR, "monitor-on.ico"))
print("copied -> monitor-on.ico")

frames = [(w, to_off(ico.ico.getimage((w, h)))) for w, h in avail]
pack(os.path.join(DIR, "monitor-off.ico"), frames)

frames[0][1].save(os.path.join(DIR, "monitor-off.png"))
ico.ico.getimage((SIZES[0], SIZES[0])).save(os.path.join(DIR, "monitor-on.png"))

sheet = Image.new("RGB", (700, 340), (245, 245, 245))
d = ImageDraw.Draw(sheet)
x = 15
for name, src in (("on", ico), ("off", Image.open(os.path.join(DIR, "monitor-off.ico")))):
    big = src.ico.getimage((SIZES[0], SIZES[0])).convert("RGBA")
    sheet.paste(big, (x, 20), big)
    xx = x
    for s in (48, 32, 16):
        if (s, s) not in avail:
            continue
        im = src.ico.getimage((s, s)).convert("RGBA")
        sheet.paste(im, (xx, 300 - s), im)
        xx += s + 14
    d.text((x, 282), name, fill=(0, 0, 0))
    x += 340
sheet.save(os.path.join(DIR, "_icon_sheet.png"))   # 对照图落在包内，不再写死用户目录
print('sheet ok')
