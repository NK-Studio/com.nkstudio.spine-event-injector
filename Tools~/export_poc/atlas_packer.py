"""첨부가 쓰는 이미지를 한 장(또는 여러 장)의 PNG로 묶고 .atlas 텍스트를 쓴다. 표준 라이브러리만 쓴다.

Spine 기본 팩 설정과 같게 맞춘 것: 여백 2픽셀(가장자리 포함), 투명한 테두리 잘라내기, 회전 허용, 미리 곱한 알파(pma),
영역 이름 정렬, atlas 줄 형식(bounds / offsets / rotate:90).
배치(어느 영역이 어디에 놓이는가)는 Spine과 같지 않을 수 있다. 런타임은 atlas에 적힌 좌표를 쓰므로 결과에는 영향이 없다.
"""
import os
import struct
import zlib

from json_writer import natural

PADDING = 2
MAX_SIZE = 4096
ALPHA_THRESHOLD = 3
SCORE_SHIFT = 16
BEST_NONE = 1 << 40


class AtlasError(Exception):
    pass


class AtlasSettings:
    """Spine 텍스처 패커 설정 중 PoC가 다루는 것. 기본값은 지금까지의 출력과 같다."""

    def __init__(self, pma=True, bleed=False, alias=False, max_size=MAX_SIZE, ext='.atlas'):
        self.pma, self.bleed, self.alias, self.max_size, self.ext = pma, bleed, alias, max_size, ext


# ---------------------------------------------------------------- PNG

def read_png(path):
    """→ (너비, 높이, RGBA bytes). 8·16비트, 그레이/RGB/팔레트/알파, 인터레이스 없음."""
    data = open(path, 'rb').read()
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        raise AtlasError(f'PNG가 아닙니다: {path}')
    p, idat, plte, trns = 8, bytearray(), None, None
    while p < len(data):
        n = struct.unpack('>I', data[p:p + 4])[0]
        kind, body = data[p + 4:p + 8], data[p + 8:p + 8 + n]
        p += 12 + n
        if kind == b'IHDR':
            w, h, depth, ctype, _, _, inter = struct.unpack('>IIBBBBB', body)
        elif kind == b'PLTE':
            plte = body
        elif kind == b'tRNS':
            trns = body
        elif kind == b'IDAT':
            idat += body
        elif kind == b'IEND':
            break
    if inter:
        raise AtlasError(f'인터레이스 PNG는 지원하지 않습니다: {path}')
    if depth not in (8, 16) and not (ctype == 3 and depth in (1, 2, 4)):
        raise AtlasError(f'{depth}비트 PNG는 지원하지 않습니다: {path}')
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ctype]
    bits = depth * channels
    bpp = max(1, bits // 8)
    stride = (w * bits + 7) // 8
    raw = zlib.decompress(bytes(idat))
    out = bytearray(w * h * 4)
    prev = bytearray(stride)
    q = 0
    for y in range(h):
        f = raw[q]
        line = bytearray(raw[q + 1:q + 1 + stride])
        q += 1 + stride
        if f == 1:
            for i in range(bpp, stride):
                line[i] = (line[i] + line[i - bpp]) & 255
        elif f == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 255
        elif f == 3:
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + (a + prev[i]) // 2) & 255
        elif f == 4:
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                b, c = prev[i], prev[i - bpp] if i >= bpp else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if pa <= pb and pa <= pc else b if pb <= pc else c
                line[i] = (line[i] + pr) & 255
        prev = line
        o = y * w * 4
        step = depth // 8 or 1
        for x in range(w):
            if ctype == 3:
                if depth == 8:
                    idx = line[x]
                else:
                    idx = (line[x * depth // 8] >> (8 - depth - (x * depth) % 8)) & ((1 << depth) - 1)
                out[o:o + 3] = plte[idx * 3:idx * 3 + 3]
                out[o + 3] = trns[idx] if trns is not None and idx < len(trns) else 255
            else:
                s = x * channels * step
                px = [line[s + c * step] for c in range(channels)]
                if ctype == 0:
                    out[o:o + 4] = bytes((px[0], px[0], px[0], 255))
                elif ctype == 2:
                    out[o:o + 4] = bytes((px[0], px[1], px[2], 255))
                elif ctype == 4:
                    out[o:o + 4] = bytes((px[0], px[0], px[0], px[1]))
                else:
                    out[o:o + 4] = bytes(px)
            o += 4
    return w, h, bytes(out)


def write_png(path, w, h, rgba):
    rows = b''.join(b'\0' + rgba[y * w * 4:(y + 1) * w * 4] for y in range(h))

    def chunk(kind, body):
        return struct.pack('>I', len(body)) + kind + body + struct.pack('>I', zlib.crc32(kind + body))
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
                + chunk(b'IDAT', zlib.compress(rows, 6)) + chunk(b'IEND', b''))


# ---------------------------------------------------------------- 이미지 처리

def bleed_rgb(w, h, pix, iterations=2):
    """완전 투명 픽셀의 RGB를 주변 8칸 평균으로 채운다(알파는 그대로). 잘라낸 뒤의 이미지에 2회 적용한다.
    한 회차 안에서는 앞서 채운 값을 쓰지 않고, 회차가 끝나야 다음 회차의 재료가 된다."""
    px = bytearray(pix)
    blank = bytearray(1 if a == 0 else 0 for a in px[3::4])
    fresh = [k for k, b in enumerate(blank) if not b]

    def around(k):
        x, y = k % w, k // w
        for ny in range(max(y - 1, 0), min(y + 2, h)):
            for nx in range(max(x - 1, 0), min(x + 2, w)):
                if nx != x or ny != y:
                    yield ny * w + nx

    for _ in range(iterations):
        targets = {n for k in fresh for n in around(k) if blank[n]}
        changed = []
        for k in targets:
            r = g = b = c = 0
            for n in around(k):
                if not blank[n]:
                    j = n * 4
                    r += px[j]
                    g += px[j + 1]
                    b += px[j + 2]
                    c += 1
            changed.append((k, r // c, g // c, b // c))
        for k, r, g, b in changed:
            j = k * 4
            px[j], px[j + 1], px[j + 2] = r, g, b
            blank[k] = 0
        fresh = [item[0] for item in changed]
        if not fresh:
            break
    return bytes(px)


def alias_pixels(pix):
    """에일리어스 비교용: 완전 투명 픽셀의 RGB는 보이지 않으므로 지운다."""
    b = bytearray(pix)
    for i in range(3, len(b), 4):
        if b[i] == 0:
            b[i - 3] = b[i - 2] = b[i - 1] = 0
    return bytes(b)


class Region:
    def __init__(self, name, w, h, rgba, strip=True, settings=None):
        settings = settings or AtlasSettings()
        self.name = name
        self.alias_of = None
        self.orig_w, self.orig_h = w, h
        # 투명 테두리 잘라내기(알파 3 이하, Spine과 같음). 메시 이미지는 UV가 전체 이미지 기준이라 자르지 않는다.
        left, top, right, bottom = w, h, -1, -1
        if strip:
            for y in range(h):
                row = rgba[y * w * 4 + 3:(y + 1) * w * 4:4]
                hit = [i for i, a in enumerate(row) if a > ALPHA_THRESHOLD]
                if not hit:
                    continue
                top = min(top, y)
                bottom = y
                left, right = min(left, hit[0]), max(right, hit[-1])
            if right < 0:
                left, top, right, bottom = 0, 0, 0, 0
        else:
            left, top, right, bottom = 0, 0, w - 1, h - 1
        self.left, self.top = left, top
        self.w, self.h = right - left + 1, bottom - top + 1
        self.bottom_off = h - 1 - bottom
        straight = b''.join(rgba[(y * w + left) * 4:(y * w + right + 1) * 4] for y in range(top, bottom + 1))
        self.key = (left, top, self.w, self.h, w, h, alias_pixels(straight)) if settings.alias else None
        if settings.pma:
            pix = bytearray(self.w * self.h * 4)
            for o in range(0, len(straight), 4):
                r, g, b, a = straight[o:o + 4]
                pix[o:o + 4] = bytes((r * a // 255, g * a // 255, b * a // 255, a))   # pma(버림, Spine과 같음)
            self.pix = bytes(pix)
        elif settings.bleed:                # 블리드는 PMA를 끌 때만 의미가 있다(Spine도 PMA 켜짐에서는 결과가 같다)
            self.pix = bleed_rgb(self.w, self.h, straight)
        else:
            self.pix = straight
        self.page = self.x = self.y = 0
        self.rotated = False

    @property
    def stripped(self):
        return (self.w, self.h) != (self.orig_w, self.orig_h)


def pack_page(regions, width, height):
    """MaxRects(짧은 변 맞춤). 놓인 것 [(영역, x, y, 회전)]과 못 놓은 것을 돌려준다. 좌표·크기에 여백 포함."""
    free = [(PADDING, PADDING, width - PADDING, height - PADDING)]
    placed, left = [], []
    for r in regions:
        w0, h0 = r.w + PADDING, r.h + PADDING
        best = BEST_NONE
        for f in free:
            fw, fh = f[2], f[3]
            if w0 <= fw and h0 <= fh:   # 점수 (짧은 변 여유, 긴 변 여유, 회전)을 정수 하나로 합쳐 비교한다
                dw, dh = fw - w0, fh - h0
                key = ((dw << SCORE_SHIFT | dh) if dw < dh else (dh << SCORE_SHIFT | dw)) << 1
                if key < best:
                    best, spot = key, f
            if h0 <= fw and w0 <= fh:
                dw, dh = fw - h0, fh - w0
                key = ((dw << SCORE_SHIFT | dh) if dw < dh else (dh << SCORE_SHIFT | dw)) << 1 | 1
                if key < best:
                    best, spot = key, f
            if not best:                # 빈틈 없이 맞는 자리는 더 찾지 않는다
                break
        if best == BEST_NONE:
            left.append(r)
            continue
        rot = bool(best & 1)
        x, y = spot[0], spot[1]
        w, h = (h0, w0) if rot else (w0, h0)
        placed.append((r, x, y, rot))
        right, bottom = x + w, y + h
        # 겹치는 빈칸만 쪼갠다. 안 겹친 빈칸끼리는 이미 서로를 포함하지 않으므로 새 조각만 포함 검사를 한다.
        new, pieces = [], []
        for f in free:
            fx, fy, fw, fh = f
            fr, fb = fx + fw, fy + fh
            if x >= fr or right <= fx or y >= fb or bottom <= fy:
                new.append(f)
                continue
            for piece in ((fx, fy, x - fx, fh) if x > fx else None,
                          (right, fy, fr - right, fh) if right < fr else None,
                          (fx, fy, fw, y - fy) if y > fy else None,
                          (fx, bottom, fw, fb - bottom) if bottom < fb else None):
                if piece is not None:
                    pieces.append(len(new))
                    new.append(piece)
        if pieces:
            is_piece = set(pieces)
            drop = set()
            for i in pieces:
                ax, ay, aw, ah = a = new[i]
                ar, ab = ax + aw, ay + ah
                for j, b in enumerate(new):
                    if (ax >= b[0] and ay >= b[1] and ar <= b[0] + b[2] and ab <= b[1] + b[3]
                            and i != j and (a != b or i > j or j not in is_piece)):
                        drop.add(i)
                        break
            if drop:
                new = [a for i, a in enumerate(new) if i not in drop]
        free = new
    return placed, left


def pack(regions, max_size=MAX_SIZE):
    """여러 폭을 시험해 넓이가 가장 작은 배치를 고른다. 한 장에 안 들어가면 페이지를 나눈다."""
    order = sorted(regions, key=lambda r: (-max(r.w, r.h), -min(r.w, r.h), r.name))
    pages = []
    todo = order
    while todo:
        area = sum((r.w + PADDING) * (r.h + PADDING) for r in todo)
        widest = max(max(r.w, r.h) + 2 * PADDING for r in todo)
        side = max(widest, int(area ** 0.5))
        best = None
        for wide in sorted({min(max_size, max(widest, int(side * f))) for f in
                            (0.8, 0.9, 1.0, 1.1, 1.2, 1.35, 1.5, 1.75, 2.0, 2.5, 3.0)}):
            placed, left = pack_page(todo, wide, max_size)
            if not placed:
                continue
            used_w = max(x + (r.h if rot else r.w) for r, x, y, rot in placed) + PADDING
            used_h = max(y + (r.w if rot else r.h) for r, x, y, rot in placed) + PADDING
            key = (len(left), used_w * used_h)
            if best is None or key < best[0]:
                best = (key, placed, left, used_w, used_h)
        if best is None:
            raise AtlasError(f'{max_size}픽셀 페이지에 들어가지 않는 이미지가 있습니다: {todo[0].name}')
        _, placed, left, w, h = best
        for r, x, y, rot in placed:
            r.page, r.x, r.y, r.rotated = len(pages), x, y, rot
        pages.append((w, h, [p[0] for p in placed]))
        todo = left
    return pages


def blit(page_w, page_h, regs):
    out = bytearray(page_w * page_h * 4)
    for r in regs:
        if not r.rotated:
            for y in range(r.h):
                d = ((r.y + y) * page_w + r.x) * 4
                out[d:d + r.w * 4] = r.pix[y * r.w * 4:(y + 1) * r.w * 4]
        else:       # 90도 회전: 원본 (x, y) → 페이지 (r.x + y, r.y + w - 1 - x)
            for y in range(r.h):
                for x in range(r.w):
                    s = (y * r.w + x) * 4
                    d = ((r.y + r.w - 1 - x) * page_w + r.x + y) * 4
                    out[d:d + 4] = r.pix[s:s + 4]
    return bytes(out)


# ---------------------------------------------------------------- 진입점

def region_names(model):
    """첨부가 쓰는 이미지 이름(경로가 있으면 경로). 정점이 없는 첨부는 빼고 region·mesh만."""
    names, mesh = set(), set()
    for s in model['skins']:
        for _, key, a in s['attachments']:
            if a['type'] in ('region', 'mesh', 'linkedmesh'):
                n = a.get('path') or a['name'] or key
                q = a.get('sequence')
                frames = [n + str(q['start'] + i).zfill(q['digits']) for i in range(q['count'])] if q else [n]
                for f in frames:
                    names.add(f)
                    if a['type'] != 'region':
                        mesh.add(f)
    return sorted(names, key=natural), mesh


def write_atlas(model, images_dir, out_dir, name, settings=None):
    """→ (atlas 경로, [png 경로…]). 빠진 이미지는 오류로 알린다."""
    settings = settings or AtlasSettings()
    regions = []
    missing = []
    names, mesh = region_names(model)
    for rn in names:
        path = os.path.join(images_dir, rn + '.png')
        if not os.path.exists(path):
            missing.append(rn)
            continue
        regions.append(Region(rn, *read_png(path), strip=rn not in mesh, settings=settings))
    if missing:
        raise AtlasError(f'이미지 폴더 {images_dir}에 없는 이미지: ' + ', '.join(missing))
    if not regions:
        raise AtlasError('묶을 이미지가 없습니다')
    packed, aliases = regions, {}
    if settings.alias:                  # 같은 그림은 한 자리에: 이름순 첫 영역을 싣고 나머지는 그 자리를 함께 쓴다
        first, packed = {}, []
        for r in regions:
            rep = first.setdefault(r.key, r)
            if rep is r:
                packed.append(r)
            else:
                r.alias_of = rep
                aliases.setdefault(id(rep), []).append(r)
    pages = pack(packed, settings.max_size)
    for rs in aliases.values():
        for r in rs:
            r.page, r.x, r.y, r.rotated = r.alias_of.page, r.alias_of.x, r.alias_of.y, r.alias_of.rotated
    lines, pngs = [], []
    for i, (w, h, regs) in enumerate(pages):
        fn = f'{name}.png' if len(pages) == 1 else f'{name}{i + 1}.png'
        if i:                           # 페이지 사이에는 빈 줄이 있어야 런타임이 새 페이지로 읽는다
            lines.append('')
        write_png(os.path.join(out_dir, fn), w, h, blit(w, h, regs))
        pngs.append(os.path.join(out_dir, fn))
        lines += [fn, f'size:{w},{h}', 'filter:Linear,Linear'] + (['pma:true'] if settings.pma else [])
        for r in sorted(regs + [a for r in regs for a in aliases.get(id(r), [])], key=lambda r: natural(r.name)):
            lines += [r.name, f'bounds:{r.x},{r.y},{r.w},{r.h}']
            if r.stripped:
                lines.append(f'offsets:{r.left},{r.bottom_off},{r.orig_w},{r.orig_h}')
            if r.rotated:
                lines.append('rotate:90')
    atlas = os.path.join(out_dir, name + settings.ext)
    with open(atlas, 'w') as f:
        f.write('\n'.join(lines) + '\n')
    return atlas, pngs
