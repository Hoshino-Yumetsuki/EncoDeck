# /// script
# requires-python = ">=3.11"
# dependencies = ["pillow==11.3.0", "numpy==2.2.6", "scipy==1.15.3", "opencv-python-headless==4.12.0.88"]
# ///
"""Rebuild reviewed official photos: uv run tools/assets/make_moondrop_artwork.py.

Source URLs, background seeds and reviewed floor zones/polygons live in
Assets/Devices/moondrop-artwork.json. Connected masks and narrow contour
refinement preserve real white material; this is not a global white color key.
Originals and dark/light/checkerboard review sheets go under artifacts/.
Manufacturer photographs are NOT covered by the source-code license.
"""
import hashlib
import json
from pathlib import Path
from urllib.request import Request, urlopen

import cv2
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / 'src/EncoDeck.App/Assets/Devices/moondrop-artwork.json'
STAGE = ROOT / 'artifacts/moondrop-artwork'
DEST = ROOT / 'src/EncoDeck.App/Assets/Devices'


def cutout(image, entry):
    rgba = np.array(image.convert('RGBA'))
    if rgba[:, :, 3].min() < 255:
        return Image.fromarray(rgba), 'preserved-source-alpha'
    rgb = rgba[:, :, :3]
    white = rgb.min(axis=2) >= entry.get('white_floor', 247)
    for x0, y0, x1, y1, floor in entry.get('background_zones', []):
        white[y0:y1,x0:x1] |= rgb[y0:y1,x0:x1].min(axis=2) >= floor
    labels, _ = ndimage.label(white)
    selected = set(np.concatenate((labels[0], labels[-1], labels[:, 0], labels[:, -1]))) - {0}
    for x, y in entry.get('background_seeds', []):
        label = int(labels[round(y * (image.height - 1)), round(x * (image.width - 1))])
        if not label:
            raise ValueError(f"Background seed is not white: {entry['model_id']} {x,y}")
        selected.add(label)
    background = np.isin(labels, list(selected))
    for x0, y0, x1, y1, floor in entry.get('background_zones', []):
        background[y0:y1,x0:x1] |= rgb[y0:y1,x0:x1].min(axis=2) >= floor
    # Source-specific ground reflections are background, not product material.
    erase = Image.new('1', image.size)
    for polygon in entry.get('background_polygons', []):
        ImageDraw.Draw(erase).polygon(polygon, fill=1)
    background |= np.array(erase, dtype=bool)
    foreground = ~background
    if entry.get('foreground_roundrects'):
        # These two simple studio views have visible capsule/case contours;
        # traced source geometry avoids interpreting JPEG blocks as product.
        contour = Image.new('L', image.size)
        draw = ImageDraw.Draw(contour)
        for x0, y0, x1, y1, radius in entry['foreground_roundrects']:
            draw.rounded_rectangle((x0,y0,x1,y1),radius,fill=255)
        for polygon in entry.get('foreground_polygons', []):
            draw.polygon(polygon,fill=255)
        foreground &= np.array(contour) > 127
    components, _ = ndimage.label(foreground)
    sizes = np.bincount(components.ravel())
    foreground &= sizes[components] >= 20  # JPEG ringing islands, not real detached earbuds.
    foreground = ndimage.binary_closing(foreground, iterations=1)
    if entry.get('refine_grabcut'):
        # Learn local foreground/background colors only in an uncertain
        # edge band. Interior product pixels, including white fabric, stay fixed.
        inside = ndimage.distance_transform_edt(foreground)
        outside = ndimage.distance_transform_edt(~foreground)
        trimap = np.where(foreground, cv2.GC_PR_FGD, cv2.GC_PR_BGD).astype('uint8')
        band = entry.get('contour_band', 6)
        trimap[inside > band] = cv2.GC_FGD
        trimap[outside > band] = cv2.GC_BGD
        for x0, y0, x1, y1, floor in entry.get('background_zones', []):
            tile = trimap[y0:y1,x0:x1]
            tile[rgb[y0:y1,x0:x1].min(axis=2) >= floor] = cv2.GC_BGD
        trimap[np.array(erase, dtype=bool)] = cv2.GC_BGD
        cv2.setRNGSeed(0)
        cv2.grabCut(rgb.copy(),trimap,None,np.zeros((1,65)),np.zeros((1,65)),5,cv2.GC_INIT_WITH_MASK)
        foreground = (trimap == cv2.GC_FGD) | (trimap == cv2.GC_PR_FGD)
    distance = ndimage.distance_transform_edt(foreground)
    alpha = ndimage.gaussian_filter(foreground.astype(float), 0.55)
    core = distance >= 3
    _, nearest = ndimage.distance_transform_edt(~core, return_indices=True)
    reference = rgb[nearest[0], nearest[1]].astype(float)
    delta = 255-reference
    energy = np.sum(delta*delta, axis=2)
    coverage = np.sum((255-rgb.astype(float))*delta, axis=2) / np.maximum(energy, 1)
    # A local white-matte equation removes pale JPEG fringes on dark surfaces.
    # Low-contrast white fabric instead keeps geometric coverage and real texture.
    boundary = (distance < 3) & (energy > 1200)
    alpha[boundary] = np.minimum(alpha[boundary], np.clip(coverage[boundary], 0, 1))
    alpha[alpha < 0.025] = 0
    alpha[alpha > 0.975] = 1
    rgba[:, :, 3] = np.round(alpha * 255).astype('uint8')
    edge = (alpha > 0) & (alpha < 1)
    a = alpha[edge, None]
    rgba[:, :, :3][edge] = np.clip((rgb[edge].astype(float) - 255 * (1-a)) / a, 0, 255).astype('uint8')
    rgba[alpha == 0] = 0
    return Image.fromarray(rgba), 'seeded-connected-white-local-matting'


def review_sheet(items, output):
    width, cell, row_height = 1200, 200, 240
    sheet = Image.new('RGB', (width, len(items) * row_height), '#d0d0d0')
    draw = ImageDraw.Draw(sheet)
    for row, (entry, original, result) in enumerate(items):
        for col, color in enumerate(('white', '#20232a', 'checker')):
            x, y = col * 400, row * row_height
            original_thumb = original.convert('RGBA'); original_thumb.thumbnail((190,205))
            result_thumb = result.copy(); result_thumb.thumbnail((190,205))
            for offset, thumb in ((0, original_thumb), (200, result_thumb)):
                tile = Image.new('RGBA', (200,210), color if color != 'checker' else '#cccccc')
                if color == 'checker':
                    td = ImageDraw.Draw(tile)
                    for cy in range(0,210,12):
                        for cx in range(0,200,12):
                            if (cx//12+cy//12)%2: td.rectangle((cx,cy,cx+11,cy+11), fill='#f0f0f0')
                tile.alpha_composite(thumb, ((200-thumb.width)//2,(210-thumb.height)//2))
                sheet.paste(tile.convert('RGB'),(x+offset,y))
            draw.text((x+5,y+214), entry['model_id']+' | original / alpha',fill='black')
    sheet.save(output)


def main():
    manifest = json.loads(MANIFEST.read_text(encoding='utf-8'))
    (STAGE/'originals').mkdir(parents=True, exist_ok=True)
    DEST.mkdir(parents=True, exist_ok=True)
    items, stats = [], []
    for entry in manifest['images']:
        original_path = STAGE/'originals'/f"{entry['model_id']}.source"
        if not original_path.exists():
            request = Request(entry['image_url'], headers={'User-Agent':'EncoDeck asset preparation'})
            with urlopen(request, timeout=60) as response:
                original_path.write_bytes(response.read())
        data = original_path.read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if entry.get('source_sha256') and digest != entry['source_sha256']:
            raise ValueError(f"Official source changed; review before updating: {entry['model_id']}")
        original = Image.open(original_path)
        result, method = cutout(original, entry)
        box = result.getbbox()
        if not box: raise ValueError(f"Empty cutout: {entry['model_id']}")
        result = result.crop(box)
        result.thumbnail((464,464), Image.Resampling.LANCZOS)
        canvas = Image.new('RGBA',(512,512))
        canvas.alpha_composite(result,((512-result.width)//2,(512-result.height)//2))
        target = DEST/f"{entry['model_id']}.png"
        canvas.save(target, optimize=True)
        alpha = np.array(canvas)[:,:,3]
        assert alpha.min() == 0 and alpha.max() == 255
        assert not np.any(alpha[:24]) and not np.any(alpha[-24:])
        assert not np.any(alpha[:,:24]) and not np.any(alpha[:,-24:])
        if entry['model_id'] in ('moondrop-edge', 'moondrop-edge2', 'moondrop-voyager', 'moondrop-nekocake', 'moondrop-space-travel-2', 'moondrop-pill', 'moondrop-golden-ages-2'):
            preview = Image.new('RGB',(1536,512))
            for column, color in enumerate(('#202124','white','#cccccc')):
                tile = Image.new('RGBA',canvas.size,color)
                if column == 2:
                    td = ImageDraw.Draw(tile)
                    for y in range(0,512,16):
                        for x in range(0,512,16):
                            if (x//16+y//16)%2: td.rectangle((x,y,x+15,y+15),fill='#eeeeee')
                tile.alpha_composite(canvas)
                preview.paste(tile.convert('RGB'),(column*512,0))
            preview.save(STAGE/f"{entry['model_id']}-full-review.png")
            if entry['model_id'] == 'moondrop-edge':
                preview.crop((40,20,500,240)).resize((1380,660)).save(STAGE/'edge-enlarged-dark.png')
        stats.append({'model_id':entry['model_id'],'source_size':original.size,'source_sha256':digest,'output_size':canvas.size,'output_bytes':target.stat().st_size,'transparent_pixels':int((alpha==0).sum()),'opaque_pixels':int((alpha==255).sum()),'partial_alpha_pixels':int(((alpha>0)&(alpha<255)).sum()),'method':method})
        items.append((entry,original,canvas))
        print(json.dumps(stats[-1]))
    review_sheet(items,STAGE/'contact-sheet.png')
    review_sheet(items[:2],STAGE/'edge-review.png')
    (STAGE/'alpha-statistics.json').write_text(json.dumps(stats,indent=2)+'\n',encoding='utf-8')


if __name__ == '__main__':
    main()
