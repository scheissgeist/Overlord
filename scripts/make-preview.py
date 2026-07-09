from PIL import Image
from pathlib import Path

src = Path(r"E:\Overlord\docs\images\overlord-host-ui.png")
out_repo = Path(r"E:\Overlord\About\Preview.png")
out_repo.parent.mkdir(parents=True, exist_ok=True)

im = Image.open(src).convert("RGB")
target_w, target_h = 1280, 720
src_w, src_h = im.size
target_ratio = target_w / target_h
src_ratio = src_w / src_h
if src_ratio > target_ratio:
    new_w = int(src_h * target_ratio)
    left = (src_w - new_w) // 2
    im = im.crop((left, 0, left + new_w, src_h))
else:
    new_h = int(src_w / target_ratio)
    top = (src_h - new_h) // 2
    im = im.crop((0, top, src_w, top + new_h))
im = im.resize((target_w, target_h), Image.Resampling.LANCZOS)

im.save(out_repo, format="PNG", optimize=True)
size = out_repo.stat().st_size
print(f"PNG size={size} bytes")
if size >= 1_000_000:
    im2 = im.quantize(colors=256, method=Image.Quantize.MEDIANCUT)
    im2.save(out_repo, format="PNG", optimize=True)
    size = out_repo.stat().st_size
    print(f"Quantized PNG size={size} bytes")
if size >= 1_000_000:
    raise SystemExit(f"Preview still too large: {size}")
print(f"Wrote {out_repo}")
