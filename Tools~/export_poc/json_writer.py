"""모델 → Spine JSON 텍스트(4.0·4.1·4.2: 옛 목록형, 4.3: constraints 통합형).

Spine CLI가 내는 것과 글자 단위로 같게 쓰는 것이 목표다:
- 4.0~4.2는 값 종류마다 정해진 소수 자릿수로 반올림하고 끝의 0을 지운다.
- 4.3은 반올림하지 않고 float32의 가장 짧은 표기를 쓴다.
- 기본값과 같은 필드는 쓰지 않는다.
"""
import base64
import hashlib
import math
import struct
from fractions import Fraction

# 4.0~4.2: 값 종류별 소수 자릿수
DIGITS = {'pos': 2, 'scale': 4, 'mix': 3, 'time': 4, 'ctime': 3, 'uv': 5, 'weight': 5, 'deform': 5, 'color': None}


def f32(x):
    return struct.unpack('>f', struct.pack('>f', x))[0]


def _nat_cmp(a, b):
    """편집기의 이름 정렬: 글자 단위로 비교하되 양쪽이 숫자면 수로 비교(flare2 < flare10, muzzle-glow < muzzle01)."""
    i = j = 0
    while i < len(a) and j < len(b):
        if a[i].isdigit() and b[j].isdigit():
            i2, j2 = i, j
            while i2 < len(a) and a[i2].isdigit():
                i2 += 1
            while j2 < len(b) and b[j2].isdigit():
                j2 += 1
            x, y = int(a[i:i2]), int(b[j:j2])
            if x != y:
                return -1 if x < y else 1
            i, j = i2, j2
            continue
        if a[i] != b[j]:
            ca, cb = a[i].lower(), b[j].lower()
            if ca != cb:
                return -1 if ca < cb else 1
            return -1 if a[i] < b[j] else 1
        i += 1
        j += 1
    return (len(a) - i) - (len(b) - j)


def natural(s):
    import functools
    return _NatKey(s)


class _NatKey:
    __slots__ = ('s',)

    def __init__(self, s):
        self.s = s

    def __lt__(self, o):
        return _nat_cmp(self.s, o.s) < 0

    def __eq__(self, o):
        return self.s == o.s


def _ulps(a, b):
    """float32 표현 사이의 간격 수(편집기 계산 오차로 끝 비트만 다른 키는 같은 값으로 본다)."""
    def key(x):
        n = struct.unpack('>i', struct.pack('>f', x))[0]
        return n if n >= 0 else -2147483648 - n
    return abs(key(a) - key(b))


def legacy_properties(c):
    """4.2 이하 프로젝트의 변형 제약을 4.3 형식으로: 여섯 속성이 각자 자기 자신으로, mix가 0인 속성은 빠진다."""
    mixes = {'rotate': 'mixRotate', 'x': 'mixX', 'y': 'mixY', 'scaleX': 'mixScaleX', 'scaleY': 'mixScaleY', 'shearY': 'mixShearY'}
    return [(k, 0.0, [(k, 0.0, 1.0 if k in ('scaleX', 'scaleY') else 100.0, 1.0)]) for k, m in mixes.items() if c[m] != 0]


def same_key_stepped(k, nxt, nch):
    """값이 같은 두 키 사이를 계단으로 낼지. IK 키는 mix·softness만 보고 직선일 때만 계단이다(플래그는 비교하지 않는다).
    그 밖의 키는 값이 float32 1 간격 이내이고, 직선이거나 곡선 손잡이가 1e-5 이내로 평평하면 계단이다."""
    a, b = k['values'], nxt['values']
    if not a or len(a) != len(b):
        return False
    ik = len(a) == 5
    if nch in (4, 7):       # 색은 8비트 값으로 비교한다(알파 0과 0.0011은 같은 키다)
        if [int(round(x * 255)) for x in a] != [int(round(x * 255)) for x in b]:
            return False
    elif not all(abs(x - y) <= 1e-6 or _ulps(x, y) <= 1 for x, y in zip(a[:2] if ik else a, b[:2] if ik else b)):
        return False
    if k['curve'] == 'linear':
        return True
    if ik:
        return False
    return all(c < len(k['handles']) and c < len(nxt['handles']) and abs(k['handles'][c][3] - a[c]) <= 1e-5
               and abs(nxt['handles'][c][1] - b[c]) <= 1e-5 for c in range(nch))


class Num:
    """미리 문자열로 만든 숫자(JSON에 따옴표 없이 들어간다)."""
    __slots__ = ('s',)

    def __init__(self, s):
        self.s = s


def java_double(v):
    """Java Double.toString과 같은 표기(정수면 '.0'을 뗀 형태로 쓴다)."""
    if v == 0:
        return '0'
    if v == int(v) and abs(v) < 1e7:
        return str(int(v))
    r = repr(float(v))
    a = abs(v)
    if 1e-3 <= a < 1e7:
        if 'e' in r:
            r = f'{v:.{17}f}'.rstrip('0')
        return r
    # 과학 표기: 가수 d.ddd + 'E' + 지수
    m, e = f'{v:.17e}'.split('e')
    digits = repr(float(v)).replace('-', '')
    sig = digits.split('e')[0].replace('.', '').lstrip('0') or '0'
    sig = sig.rstrip('0') or '0'
    mant = sig[0] + '.' + (sig[1:] or '0')
    return ('-' if v < 0 else '') + mant + 'E' + str(int(e))


def round_half_up(x, digits):
    """Math.round(x * 10^d) / 10^d — 값은 float32, 곱셈은 double."""
    m = 10.0 ** digits
    return math.floor(f32(x) * m + 0.5) / m


def fmt_round(x, digits):
    return java_double(round_half_up(x, digits))


def _dragon(x, fm=Fraction(3, 4)):
    """4.3 편집기 숫자 표기에 가장 가까운 규칙: float32 이웃 간격의 3/4 안에서 가장 짧은 십진수(Dragon4 자유 형식)."""
    if x == 0:
        return '0'
    neg = x < 0
    x = abs(x)
    b = struct.unpack('>I', struct.pack('>f', x))[0]
    v = Fraction(x)
    up = Fraction(struct.unpack('>f', struct.pack('>I', b + 1))[0]) - v
    dn = v - Fraction(struct.unpack('>f', struct.pack('>I', b - 1))[0]) if b > 1 else up
    mm, mp = dn * fm * 2, up * fm * 2
    k = 0
    while Fraction(10) ** k <= v:
        k += 1
    while Fraction(10) ** (k - 1) > v:
        k -= 1
    s = Fraction(10) ** k
    r, mm, mp = v / s, mm / s, mp / s
    digits = []
    while True:
        r, mm, mp = r * 10, mm * 10, mp * 10
        d = int(r)
        r -= d
        low, high = r < mm, r > 1 - mp
        if low or high or len(digits) > 12:
            break
        digits.append(d)
    if high and (not low or r * 2 >= 1):
        d += 1
    digits.append(d)
    i = len(digits) - 1
    while digits[i] == 10:
        digits[i] = 0
        if i == 0:
            digits.insert(0, 1)
            k += 1
            break
        digits[i - 1] += 1
        i -= 1
    t = ''.join(map(str, digits)).rstrip('0') or '0'
    if k <= 0:
        out = '0.' + '0' * (-k) + t
    elif k >= len(t):
        out = t + '0' * (k - len(t))
    else:
        out = t[:k] + '.' + t[k:]
    return ('-' if neg else '') + out


def fmt_f32(x, int_ok=True):
    """Java Float.toString과 같은 가장 짧은 표기. int_ok면 정수는 '.0' 없이."""
    x = f32(x)
    if x == int(x) and abs(x) < 1e7:
        return str(int(x)) if int_ok else f'{int(x)}.0'
    s = _dragon(x)
    if '.' not in s and not int_ok:
        s += '.0'
    a = abs(x)
    if a < 1e-3 or a >= 1e7:      # Java식 과학 표기
        plain = s.lstrip('-')
        digits = plain.replace('.', '').lstrip('0').rstrip('0') or '0'
        # 지수는 십진 표기에서 직접 센다(0.0001f의 log10은 -4보다 살짝 작아 한 자리 어긋난다)
        if a < 1e-3:
            e = -(len(plain.split('.')[1]) - len(plain.split('.')[1].lstrip('0')) + 1)
        else:
            e = len(plain.split('.')[0]) - 1
        s = ('-' if x < 0 else '') + digits[0] + '.' + (digits[1:] or '0') + 'E' + str(e)
    return s


class Writer:
    def __init__(self, model, version, nonessential=True):
        self.m = model
        self.version = version
        self.major = tuple(int(x) for x in version.split('.')[:2])
        self.v43 = self.major >= (4, 3)
        self.nonessential = nonessential
        self.fps = model['fps']
        self.sort_skin_slots = True

    # ------------------------------------------------------------ 숫자

    def n(self, x, kind='pos'):
        if self.v43:
            return Num(fmt_f32(x))
        return Num(fmt_round(x, DIGITS[kind]))

    def nf(self, x, kind='pos'):
        """4.3에서 정수도 '.0'을 붙이는 자리(정점·메시 크기 등)."""
        if self.v43:
            return Num(fmt_f32(x, int_ok=False))
        return Num(fmt_round(x, DIGITS[kind]))

    def sec(self, frame):
        """프레임 → 초. 4.0~4.2 편집기는 float32로 1/fps를 곱하고, 4.3은 fps로 나눈다."""
        if self.v43:
            return f32(f32(frame) / f32(self.fps))
        return f32(f32(frame) * f32(1.0 / self.fps))

    def time(self, frame):
        return self.n(self.sec(frame), 'time')

    def is_default(self, x, d, kind='pos'):
        if self.v43:        # 4.3도 값 종류별 자릿수로 기본값을 판정한다(뼈 scaleY 0.999634는 쓰고, 위치 0.004는 뺀다)
            return round_half_up(f32(x) - f32(d), DIGITS[kind]) == 0
        return fmt_round(x, DIGITS[kind]) == fmt_round(d, DIGITS[kind])

    def key_default(self, x, d, kind='pos'):
        """키프레임 값의 기본값 판정."""
        return self.is_default(x, d, kind)

    def vert(self, x, kind='pos'):
        """정점 좌표·가중치: 4.3도 값은 자릿수로 반올림한다(표기만 float)."""
        if self.v43:
            return Num(fmt_f32(round_half_up(x, DIGITS[kind]), int_ok=False))
        return Num(fmt_round(x, DIGITS[kind]))

    # ------------------------------------------------------------ 전체

    def build(self):
        m = self.m
        out = {}
        out['skeleton'] = self.skeleton()
        out['bones'] = [self.bone(b) for b in m['bones']]
        out['slots'] = [self.slot(s) for s in m['slots']]
        cons = m['constraints']
        if self.v43:
            if cons:
                out['constraints'] = [self.constraint43(c) for c in cons]
        else:
            iks = sorted((c for c in cons if c['type'] == 'ik'), key=lambda c: c['name'])
            trs = sorted((c for c in cons if c['type'] == 'transform'), key=lambda c: c['name'])
            paths = sorted((c for c in cons if c['type'] == 'path'), key=lambda c: c['name'])
            phys = sorted((c for c in cons if c['type'] == 'physics'), key=lambda c: c['name'])
            if any(c['type'] == 'slider' for c in cons):
                raise ValueError('슬라이더 제약은 Spine 4.3 전용이라 이 버전으로 내보낼 수 없습니다')
            if iks:
                out['ik'] = [self.ik(c) for c in iks]
            if trs:
                out['transform'] = [self.transform(c) for c in trs]
            if paths:
                out['path'] = [self.path(c) for c in paths]
            if phys and self.major >= (4, 2):
                out['physics'] = [self.physics(c) for c in phys]
            elif phys:
                raise ValueError('물리 제약은 Spine 4.2 이상에서만 내보낼 수 있습니다')
        out['skins'] = [self.skin(s) for s in m['skins'] if s['attachments'] or s['name'] != 'default' or True]
        if m['events']:
            out['events'] = {e['name']: self.event(e) for e in m['events']}
        anims = {a['name']: self.animation(a) for a in m['animations']}
        if anims:
            out['animations'] = anims
        out['skeleton']['hash'] = self.hash(out)
        out['skeleton'] = dict([('hash', out['skeleton'].pop('hash'))] + list(out['skeleton'].items()))
        return out

    def hash(self, out):
        """편집기의 해시 계산법은 알 수 없다. 내용으로 정해지는 8바이트 값을 쓴다(런타임은 비교에만 쓴다)."""
        body = dumps({k: v for k, v in out.items() if k != 'skeleton'})
        d = hashlib.sha1(body.encode()).digest()[:8]
        return base64.b64encode(d).decode().rstrip('=')

    def skeleton(self):
        m = self.m
        s = {'spine': self.version}
        if m['bounds']:
            x, y, w, h = m['bounds']
            s.update(x=self.n(x), y=self.n(y), width=self.n(w), height=self.n(h))
        if m['referenceScale'] != 100 and self.major >= (4, 2):
            s['referenceScale'] = self.n(m['referenceScale'])
        if self.nonessential:
            if m['fps'] != 30:
                s['fps'] = self.n(m['fps'])
            s['images'] = m['images']
            s['audio'] = m['audio'] if (m['audio'] or not self.v43) else None
        return s

    def bone(self, b):
        o = {'name': b['name']}
        if b['parent'] is not None:
            o['parent'] = b['parent']
        if b['length']:
            o['length'] = self.n(b['length'])
        for k, d, kind in (('rotation', 0, 'pos'), ('x', 0, 'pos'), ('y', 0, 'pos'), ('scaleX', 1, 'scale'),
                           ('scaleY', 1, 'scale'), ('shearX', 0, 'pos'), ('shearY', 0, 'pos')):
            if not self.is_default(b[k], d, kind):
                o[k] = self.n(b[k], kind)
        if b['inherit'] != 'normal':
            o['inherit' if self.major >= (4, 2) else 'transform'] = b['inherit']
        if b['skin']:
            o['skin'] = True
        if self.nonessential:
            if b['color'] and b['color'] != '989898ff' and b['color'] != '9b9b9bff':
                o['color'] = b['color']
            if b['icon'] and b['icon'] != 'bone' and self.major >= (4, 1):
                o['icon'] = b['icon']
        return o

    def slot(self, s):
        o = {'name': s['name'], 'bone': s['bone']}
        if s['color'] != 'ffffffff':
            o['color'] = s['color']
        if s['dark']:
            o['dark'] = s['dark']
        if s['attachment'] is not None:
            o['attachment'] = s['attachment']
        if s['blend'] != 'normal':
            o['blend'] = s['blend']
        if self.nonessential and not s['visible'] and self.major >= (4, 1):
            o['visible'] = False
        return o

    def ik(self, c):
        o = {'name': c['name']}
        if c['order']:
            o['order'] = c['order']
        if c['skin']:
            o['skin'] = True
        o['bones'] = c['bones']
        o['target'] = c['target']
        if not self.is_default(c['mix'], 1, 'mix'):
            o['mix'] = self.n(c['mix'], 'mix')
        if not self.is_default(c['softness'], 0):
            o['softness'] = self.n(c['softness'])
        if len(c['bones']) == 2:
            if not c['bendPositive']:
                o['bendPositive'] = False
            if c['stretch']:
                o['stretch'] = True
        else:
            if c['compress']:
                o['compress'] = True
            if c['stretch']:
                o['stretch'] = True
            if c['uniform']:
                o['uniform'] = True
        return o

    def transform(self, c):
        o = {'name': c['name']}
        if c['order']:
            o['order'] = c['order']
        if c['skin']:
            o['skin'] = True
        o['bones'] = c['bones']
        o['target'] = c['target']
        if not self.is_default(c['rotation'], 0):
            o['rotation'] = self.n(c['rotation'])
        if c['local']:
            o['local'] = True
        if c['relative']:
            o['relative'] = True
        for k, kind in (('x', 'pos'), ('y', 'pos'), ('scaleX', 'scale'), ('scaleY', 'scale'), ('shearY', 'pos')):
            if not self.is_default(c[k], 0, kind):
                o[k] = self.n(c[k], kind)
        self.mixes(o, c)
        return o

    @staticmethod
    def path_kinds(c):
        """위치·간격의 자릿수: 비율 모드는 scale(4자리), 길이 모드는 pos(2자리)."""
        pos = 'scale' if c['positionMode'] == 'percent' else 'pos'
        spc = 'scale' if c['spacingMode'] in ('percent', 'proportional') else 'pos'
        return pos, spc

    def path_fields(self, o, c):
        if c['positionMode'] != 'percent':
            o['positionMode'] = c['positionMode']
        if c['spacingMode'] != 'length':
            o['spacingMode'] = c['spacingMode']
        if c['rotateMode'] != 'tangent':
            o['rotateMode'] = c['rotateMode']
        pos, spc = self.path_kinds(c)
        if not self.is_default(c['rotation'], 0):
            o['rotation'] = self.n(c['rotation'])
        if not self.is_default(c['position'], 0, pos):
            o['position'] = self.n(c['position'], pos)
        if not self.is_default(c['spacing'], 0, spc):
            o['spacing'] = self.n(c['spacing'], spc)
        if not self.is_default(c['mixRotate'], 1, 'mix'):
            o['mixRotate'] = self.n(c['mixRotate'], 'mix')
        if not self.is_default(c['mixX'], 1, 'mix'):
            o['mixX'] = self.n(c['mixX'], 'mix')
        if not self.is_default(c['mixY'], c['mixX'], 'mix'):
            o['mixY'] = self.n(c['mixY'], 'mix')

    def path(self, c):
        o = {'name': c['name']}
        if c['order']:
            o['order'] = c['order']
        if c['skin']:
            o['skin'] = True
        o['bones'] = c['bones']
        o['target'] = c['target']
        self.path_fields(o, c)
        return o

    def physics_fields(self, o, c):
        for k, d, kind in (('x', 0, 'scale'), ('y', 0, 'scale'), ('rotate', 0, 'scale'), ('scaleX', 0, 'scale'),
                           ('shearX', 0, 'scale')):
            if not self.is_default(c[k], d, kind):
                o[k] = self.n(c[k], kind)
        if not self.is_default(c['limit'], 5000):
            o['limit'] = self.n(c['limit'])
        if not self.is_default(c['fps'], 60):
            o['fps'] = self.n(c['fps'])
        for k, d, kind in (('inertia', 0.5, 'scale'), ('strength', 100, 'pos'), ('damping', 0.85, 'scale'), ('mass', 1, 'scale'),
                           ('wind', 0, 'pos'), ('gravity', 0, 'pos'), ('mix', 1, 'scale')):
            if not self.is_default(c[k], d, kind):
                o[k] = self.n(c[k], kind)
        for k in ('inertiaGlobal', 'strengthGlobal', 'dampingGlobal', 'massGlobal', 'windGlobal', 'gravityGlobal', 'mixGlobal'):
            if c[k]:
                o[k] = True

    def physics(self, c):
        o = {'name': c['name']}
        if c['order']:
            o['order'] = c['order']
        if c['skin']:
            o['skin'] = True
        o['bone'] = c['bone']
        self.physics_fields(o, c)
        return o

    def mixes(self, o, c, keyed=False):
        for k, d in (('mixRotate', 1), ('mixX', 1), ('mixY', c['mixX']), ('mixScaleX', 1),
                     ('mixScaleY', 1 if self.v43 and keyed else c['mixScaleX']),   # 4.3 키의 mixScaleY 기본값은 1
                     ('mixShearY', 1)):
            if not (self.key_default if keyed else self.is_default)(c[k], d, 'scale' if keyed else 'mix'):
                o[k] = self.n(c[k], 'scale' if keyed else 'mix')

    def constraint43(self, c):
        o = {'type': c['type'], 'name': c['name']}
        if c['skin']:
            o['skin'] = True
        if c['type'] == 'ik':
            o['target'] = c['target']
            o['bones'] = c['bones']
            if not self.is_default(c['mix'], 1):
                o['mix'] = self.n(c['mix'])
            if not self.is_default(c['softness'], 0):
                o['softness'] = self.n(c['softness'])
            if len(c['bones']) == 2:
                if not c['bendPositive']:
                    o['bendPositive'] = False
                if c['stretch']:
                    o['stretch'] = True
            else:
                if c['compress']:
                    o['compress'] = True
                if c['stretch']:
                    o['stretch'] = True
                if c['uniform']:
                    o['uniform'] = True
            return o
        if c['type'] == 'physics':
            o['bone'] = c['bone']
            self.physics_fields(o, c)
            return o
        if c['type'] == 'slider':
            if c['loop']:
                o['loop'] = True
            if c['animation']:
                o['animation'] = c['animation']
            if c['bone']:
                o['bone'] = c['bone']
            if c['property']:
                o['property'] = c['property']
            if not self.is_default(c['max'], 0):
                o['max'] = self.n(c['max'])
            if not self.is_default(c['scale'], 1, 'scale'):
                o['scale'] = self.n(c['scale'])
            if c['local']:
                o['local'] = True
            return o
        if c['type'] == 'path':
            o['slot'] = c['target']
            o['bones'] = c['bones']
            self.path_fields(o, c)
            return o
        o['source'] = c['target']
        o['bones'] = c['bones']
        props = c.get('properties')
        if props is None:
            props = legacy_properties(c)
        targets = {name for _, _, tos in props for name, _, _, _ in tos}
        for k, prop in (('rotation', 'rotate'), ('x', 'x'), ('y', 'y'), ('scaleX', 'scaleX'), ('scaleY', 'scaleY'), ('shearY', 'shearY')):
            if prop in targets and not self.is_default(c[k], 0):     # 대상 속성이 없는 오프셋은 쓰지 않는다
                o[k] = self.n(c[k])
        po = {}
        for frm, off, tos in props:
            d = {}
            if not self.is_default(off, 0):
                d['offset'] = self.n(off)
            to = {}
            for name, toff, mx, sc in tos:
                t = {}
                if not self.is_default(toff, 0):
                    t['offset'] = self.n(toff)
                if not self.is_default(mx, 1):
                    t['max'] = self.n(mx)
                if not self.is_default(sc, 1):
                    t['scale'] = self.n(sc)
                to[name] = t
            d['to'] = to
            po[frm] = d
        o['properties'] = po
        if c['local']:
            o['localSource'] = True
            o['localTarget'] = True
        if c['relative']:
            o['additive'] = True
        have = targets
        mix = {k: v for k, v in c.items() if k.startswith('mix')}
        for k, d, prop in (('mixRotate', 1, 'rotate'), ('mixX', 1, 'x'), ('mixY', c['mixX'] if 'x' in have else 1, 'y'),
                           ('mixScaleX', 1, 'scaleX'), ('mixScaleY', c['mixScaleX'] if 'scaleX' in have else 1, 'scaleY'),
                           ('mixShearY', 1, 'shearY')):
            if prop in have and not self.is_default(mix[k], d):
                o[k] = self.n(mix[k])
        return o

    # ------------------------------------------------------------ 스킨

    def skin(self, s):
        o = {'name': s['name']}
        if s['bones']:
            o['bones'] = s['bones']
        iks = [n for n in s['constraints'] if self._ctype(n) == 'ik']
        trs = [n for n in s['constraints'] if self._ctype(n) == 'transform']
        paths = [n for n in s['constraints'] if self._ctype(n) == 'path']
        phys = [n for n in s['constraints'] if self._ctype(n) == 'physics']
        if iks:
            o['ik'] = iks
        if trs:
            o['transform'] = trs
        if paths:
            o['path'] = paths
        if phys:
            o['physics'] = phys
        if self.nonessential and s.get('color') and s['color'] != '99999900' and False:
            o['color'] = s['color']
        atts = {}
        rows = s['attachments']
        if self.sort_skin_slots:
            rows = sorted(rows, key=lambda r: (natural(r[0]), natural(r[1])))
        for slot, key, a in rows:
            atts.setdefault(slot, {})[key] = self.attachment(a)
        if atts:                    # 첨부가 없는 스킨은 키를 쓰지 않는다
            o['attachments'] = atts
        return o

    def cons(self, name):
        return next(c for c in self.m['constraints'] if c['name'] == name)

    def _ctype(self, name):
        for c in self.m['constraints']:
            if c['name'] == name:
                return c['type']
        return None

    def attachment(self, a):
        o = {}
        t = a['type']
        if t != 'region':
            o['type'] = t
        if a['name'] != a['key']:
            o['name'] = a['name']
        if t == 'region':
            if a['path']:
                o['path'] = a['path']
            if a['color'] != 'ffffffff':
                o['color'] = a['color']
            for k, d, kind in (('x', 0, 'pos'), ('y', 0, 'pos'), ('scaleX', 1, 'scale'), ('scaleY', 1, 'scale'), ('rotation', 0, 'pos')):
                if not self.is_default(a[k], d, kind):
                    o[k] = self.n(a[k], kind)
            o['width'] = self.n(a['width'])
            o['height'] = self.n(a['height'])
            self.seq_field(o, a)
            return o
        if t == 'mesh':
            if a['path']:
                o['path'] = a['path']
            if a['color'] != 'ffffffff':
                o['color'] = a['color']
            o['uvs'] = [self.n(x, 'uv') for x in a['uvs']]
            o['triangles'] = a['triangles']
            o['vertices'] = self.verts(a)
            o['hull'] = a['hull']
            if self.nonessential:
                if a['edges']:
                    o['edges'] = a['edges']
                o['width'] = self.nf(a['width'])
                o['height'] = self.nf(a['height'])
            self.seq_field(o, a)
            return o
        if t == 'linkedmesh':
            if a['path']:
                o['path'] = a['path']
            if a['color'] != 'ffffffff':
                o['color'] = a['color']
            if a['skin']:
                o['skin'] = a['skin']
            o['source' if self.v43 else 'parent'] = a['parent']
            if not a['timelines']:
                o['timelines' if self.major >= (4, 1) else 'deform'] = False
            if self.nonessential:
                o['width'] = self.nf(a['width'])
                o['height'] = self.nf(a['height'])
            return o
        if t == 'path':
            if a['closed']:
                o['closed'] = True
            if not a['constantSpeed']:
                o['constantSpeed'] = False
            o['lengths'] = [self.nf(x) for x in a['lengths']]
            o['vertexCount'] = a['vertexCount']
            o['vertices'] = self.verts(a)
            if self.nonessential and a['color'] != 'ff7f00ff':
                o['color'] = a['color']
            return o
        if t in ('boundingbox', 'clipping'):
            if t == 'clipping':
                o['end'] = a['end']
                if self.v43 and a.get('convex'):
                    o['convex'] = True
            o['vertexCount'] = a['vertexCount']
            o['vertices'] = self.verts(a)
            if self.nonessential and a['color'] != {'boundingbox': '60f000ff'}.get(t, 'ffffffff'):
                o['color'] = a['color']
            return o
        raise ValueError(t)

    @staticmethod
    def seq_field(o, a):
        q = a.get('sequence')
        if q:
            d = {'count': q['count']}
            if q['start'] != 1:
                d['start'] = q['start']
            if q['digits']:
                d['digits'] = q['digits']
            if q['setup']:
                d['setup'] = q['setup']
            o['sequence'] = d

    def verts(self, a):
        v = a['vertices']
        if not a['weighted']:
            return [self.vert(x) for x in v]
        out, i = [], 0
        cnt = (lambda n: Num(f'{n}.0')) if self.v43 else (lambda n: n)   # 4.3은 정점 배열 전체가 float 표기
        while i < len(v):
            n = v[i]
            out.append(cnt(n))
            i += 1
            for _ in range(n):
                out += [cnt(v[i]), self.vert(v[i + 1]), self.vert(v[i + 2]), self.vert(v[i + 3], 'weight')]
                i += 4
        return out

    # ------------------------------------------------------------ 이벤트

    def event(self, e):
        o = {}
        if e['int']:
            o['int'] = e['int']
        if e['float']:
            o['float'] = self.n(e['float'])
        if e['string']:
            o['string'] = e['string']
        if e['audio']:
            o['audio'] = e['audio']
            o['volume'] = self.n(e['volume'])
            o['balance'] = self.n(e['balance'])
        return o

    # ------------------------------------------------------------ 애니메이션

    def animation(self, an):
        sec = {}
        for t in an['timelines']:
            ty = t['type']
            if ty in ('color', 'attachment'):
                d = sec.setdefault('slots', {}).setdefault(t['slot'], {})
                if ty == 'attachment':
                    d['attachment'] = self.att_keys(t)
                else:
                    name, keys = self.color_keys(t)
                    d[name] = keys
            elif ty in ('rotate', 'translate', 'scale', 'shear'):
                sec.setdefault('bones', {}).setdefault(t['bone'], {})[ty] = self.bone_keys(t)
            elif ty == 'ik':
                sec.setdefault('ik', {})[t['constraint']] = self.ik_keys(t)
            elif ty == 'transform':
                sec.setdefault('transform', {})[t['constraint']] = self.tr_keys(t)
            elif ty == 'deform':
                if self.major < (4, 1):     # 4.0: deform/<스킨>/<슬롯>/<첨부> = 키 목록
                    sec.setdefault('deform', {}).setdefault(t['skin'], {}).setdefault(t['slot'], {})[
                        t['attachment']] = self.deform_keys(t)
                else:
                    sec.setdefault('attachments', {}).setdefault(t['skin'], {}).setdefault(t['slot'], {}).setdefault(
                        t['attachment'], {})['deform'] = self.deform_keys(t)
            elif ty == 'event':
                sec.setdefault('_events', []).append(t)
            elif ty == 'drawOrder':
                sec['drawOrder'] = self.draw_keys(t)
            elif ty in ('translatex', 'translatey'):
                sec.setdefault('bones', {}).setdefault(t['bone'], {})[ty] = self.value_keys(t, 0, 'pos')
            elif ty == 'sequence':
                sec.setdefault('attachments', {}).setdefault(t['skin'], {}).setdefault(t['slot'], {}).setdefault(
                    t['attachment'], {})['sequence'] = self.seq_keys(t)
            elif ty == 'physicsWind':
                sec.setdefault('physics', {}).setdefault(t['constraint'], {})['wind'] = self.value_keys(t, 0, 'pos')
            elif ty == 'physicsWindAll':
                sec.setdefault('physics', {}).setdefault('', {})['wind'] = self.value_keys(t, 0, 'pos')
            elif ty == 'sliderMix':
                sec.setdefault('slider', {}).setdefault(t['constraint'], {})['mix'] = self.value_keys(t, 1, 'mix')
            elif ty in ('pathPosition', 'pathSpacing', 'pathMix'):
                d = sec.setdefault('path', {}).setdefault(t['constraint'], {})
                d[{'pathPosition': 'position', 'pathSpacing': 'spacing', 'pathMix': 'mix'}[ty]] = self.path_keys(t)
        if '_events' in sec:        # 이벤트 타임라인은 정의마다 따로 있다: 시간순으로 합친다
            sec['events'] = self.event_keys({'keys': sorted((k for t in sec.pop('_events') for k in t['keys']),
                                                            key=lambda k: k['frame'])})
        order = ['slots', 'bones', 'ik', 'transform', 'path', 'physics', 'slider', 'attachments', 'deform', 'drawOrder', 'events']
        if self.v43 and 'bones' in sec:         # 4.3은 뼈도 이름순
            sec['bones'] = dict(sorted(sec['bones'].items(), key=lambda kv: natural(kv[0])))
        for k in ('slots', 'ik', 'transform', 'constraints', 'path', 'physics'):
            if k in sec:
                sec[k] = dict(sorted(sec[k].items(), key=lambda kv: natural(kv[0])))
        for sname in ('attachments', 'deform'):
            if sname not in sec:
                continue
            nat = lambda kv: natural(kv[0])
            sec[sname] = {sk: {sl: dict(sorted(a.items(), key=nat)) for sl, a in sorted(v.items(), key=nat)}
                                  for sk, v in sorted(sec[sname].items(), key=nat)}
        return {k: sec[k] for k in order if k in sec}

    def key_base(self, i, k):
        o = {}
        if k['frame'] != 0:
            o['time'] = self.time(k['frame'])
        return o

    def curve(self, keys, i, kinds):
        k = keys[i]
        if i == len(keys) - 1:
            return None
        if k['curve'] == 'stepped':
            return 'stepped'
        nxt = keys[i + 1]
        if 'dense' in k and k['curve'] == 'linear' and len(k['dense']) == len(nxt['dense']) and all(
                abs(a - b) <= 0.0001 for a, b in zip(k['dense'], nxt['dense'])):
            return 'stepped'
        if same_key_stepped(k, nxt, len(kinds)):
            return 'stepped'
        if k['curve'] == 'linear':
            return None
        t0, t1 = k['frame'], nxt['frame']
        out = []
        for c, kind in enumerate(kinds):
            if k['values']:
                v0, v1 = k['values'][c], nxt['values'][c]
            else:                       # 값 없는 타임라인(변형 등): 곡선은 0→1 비율
                v0, v1 = 0.0, 1.0
            h0 = k['handles'][c] if c < len(k['handles']) else None
            h1 = nxt['handles'][c] if c < len(nxt['handles']) else None
            cx1, cy1 = (h0[2], h0[3]) if h0 and h0[2] != 2147483648.0 else (t0 + (t1 - t0) / 3, v0)
            cx2, cy2 = (h1[0], h1[1]) if h1 and h1[0] != 2147483648.0 else (t1 - (t1 - t0) / 3, v1)
            for x, kd in ((cx1, 'ctime'), (cy1, kind), (cx2, 'ctime'), (cy2, kind)):
                if kd == 'ctime':
                    out.append(self.n(self.sec(x), 'ctime'))
                else:
                    out.append(self.n(x, kd))
        return out

    def value_keys(self, t, default, kind):
        keys = t['keys']
        out = []
        for i, k in enumerate(keys):
            o = self.key_base(i, k)
            if not self.key_default(k['values'][0], default, kind):
                o['value'] = self.n(k['values'][0], kind)
            cv = self.curve(keys, i, [kind])
            if cv is not None:
                o['curve'] = cv
            out.append(o)
        return out

    def seq_keys(self, t):
        import model as model_mod
        out = []
        for i, k in enumerate(t['keys']):
            o = self.key_base(i, k)
            mode = model_mod.SEQ_MODE[int(k['values'][0])]
            if mode != 'hold':
                o['mode'] = mode
            if int(k['values'][1]):
                o['index'] = int(k['values'][1])
            prev = t['keys'][i - 1]['values'][2] if i else 0.0      # 앞 키와 같으면 쓰지 않는다
            if k['values'][2] != prev:
                o['delay'] = self.n(k['values'][2], 'time')
            out.append(o)
        return out

    def path_keys(self, t):
        keys = t['keys']
        c = self.cons(t['constraint'])
        pos, spc = self.path_kinds(c)
        out = []
        for i, k in enumerate(keys):
            o = self.key_base(i, k)
            v = k['values']
            if t['type'] == 'pathMix':
                vd = dict(zip(['mixRotate', 'mixX', 'mixY'], v))
                if not self.key_default(vd['mixRotate'], 1, 'mix'):
                    o['mixRotate'] = self.n(vd['mixRotate'], 'mix')
                if not self.key_default(vd['mixX'], 1, 'mix'):
                    o['mixX'] = self.n(vd['mixX'], 'mix')
                if not self.key_default(vd['mixY'], vd['mixX'], 'mix'):
                    o['mixY'] = self.n(vd['mixY'], 'mix')
                kinds = ['mix'] * 3
            else:
                kind = pos if t['type'] == 'pathPosition' else spc
                if not self.key_default(v[0], 0, kind):
                    o['value'] = self.n(v[0], kind)
                kinds = [kind]
            cv = self.curve(keys, i, kinds)
            if cv is not None:
                o['curve'] = cv
            out.append(o)
        return out

    def bone_keys(self, t):
        ty = t['type']
        d = 0 if ty in ('rotate', 'translate', 'shear') else 1
        kind = 'scale' if ty == 'scale' else 'pos'
        ckind = 'mix' if ty == 'scale' else 'pos'
        keys = t['keys']
        out = []
        for i, k in enumerate(keys):
            o = self.key_base(i, k)
            if ty == 'rotate':
                if not self.key_default(k['values'][0], 0):
                    o['value'] = self.n(k['values'][0])
                kinds = ['pos']
            else:
                for c, nm in enumerate(('x', 'y')):
                    if not self.key_default(k['values'][c], d, 'mix' if ty == 'scale' else 'pos'):
                        o[nm] = self.n(k['values'][c], 'mix' if ty == 'scale' else 'pos')
                kinds = [ckind, ckind]
            cv = self.curve(keys, i, kinds)
            if cv is not None:
                o['curve'] = cv
            out.append(o)
        return out

    def color_keys(self, t):
        slot = next(s for s in self.m['slots'] if s['name'] == t['slot'])
        two = slot['dark'] is not None
        keys = t['keys']
        out = []
        for i, k in enumerate(keys):
            o = self.key_base(i, k)
            v = k['values']
            light = ''.join('%02x' % int(round(x * 255)) for x in v[:4])
            if two:
                o['light'] = light
                o['dark'] = ''.join('%02x' % int(round(x * 255)) for x in v[4:7])
                kinds = ['pos'] * 7
            else:
                o['color'] = light
                kinds = ['pos'] * 4
            cv = self.curve(keys, i, kinds)
            if cv is not None:
                o['curve'] = cv
            out.append(o)
        return ('rgba2' if two else 'rgba'), out

    def att_keys(self, t):
        out = []
        for i, k in enumerate(t['keys']):
            o = self.key_base(i, k)
            if k['name'] is not None or self.major < (4, 1):
                o['name'] = k['name']
            out.append(o)
        return out

    def ik_keys(self, t):
        keys = t['keys']
        out = []
        for i, k in enumerate(keys):
            o = self.key_base(i, k)
            v = k['values']
            if not self.key_default(v[0], 1, 'mix'):
                o['mix'] = self.n(v[0], 'mix')
            if not self.key_default(v[1], 0):
                o['softness'] = self.n(v[1])
            two = len(self.cons(t['constraint'])['bones']) == 2
            if two and not v[4]:
                o['bendPositive'] = False
            if v[3] and not two:        # 값 순서: mix, softness, stretch, compress, bend (차등 표본으로 확인)
                o['compress'] = True
            if v[2]:
                o['stretch'] = True
            cv = self.curve(keys, i, ['mix', 'pos'])
            if cv is not None:
                o['curve'] = cv
            out.append(o)
        return out

    def tr_keys(self, t):
        keys = t['keys']
        out = []
        for i, k in enumerate(keys):
            o = self.key_base(i, k)
            v = dict(zip(['mixRotate', 'mixX', 'mixY', 'mixScaleX', 'mixScaleY', 'mixShearY'], k['values']))
            self.mixes(o, self.tr_used(t, v), True)
            cv = self.curve(keys, i, ['mix'] * 6)
            if cv is not None:
                o['curve'] = cv
            out.append(o)
        return out

    def tr_used(self, t, v):
        """4.3: 제약에 없는 속성의 mix는 쓰지 않는다(런타임 기본값 1로 둬도 쓰이지 않는다)."""
        c = self.cons(t['constraint'])
        props = c.get('properties')
        if props is None:
            props = legacy_properties(c)
        if not self.v43:
            return v
        have = {name for _, _, tos in props for name, _, _, _ in tos}
        out = dict(v)
        for k, prop in (('mixRotate', 'rotate'), ('mixX', 'x'), ('mixY', 'y'), ('mixScaleX', 'scaleX'),
                        ('mixScaleY', 'scaleY'), ('mixShearY', 'shearY')):
            if prop not in have:
                out[k] = out['mixX'] if k == 'mixY' else 1.0
        return out

    def deform_keys(self, t):
        att = self.find_att(t)
        keys = t['keys']
        out = []
        for i, k in enumerate(keys):
            o = self.key_base(i, k)
            vec = self.deform_vec(att, k)
            # 앞뒤에서 설정 위치와 0.01 이내로 같은 값은 잘라낸다(편집기 규칙, float32로 비교)
            nz = [j for j, (x, s0) in enumerate(zip(vec, att['setup'])) if abs(f32(f32(s0 + x) - s0)) > f32(0.01)]
            if nz:
                start, end = nz[0], nz[-1] + 1
                if start:
                    o['offset'] = start
                o['vertices'] = [self.n(x, 'deform') for x in vec[start:end]]
            cv = self.curve(keys, i, ['pos'])
            if cv is not None:
                o['curve'] = cv
            out.append(o)
        return out

    def find_att(self, t):
        for s in self.m['skins']:
            if s['name'] == t['skin']:
                for slot, key, a in s['attachments']:
                    if slot == t['slot'] and key == t['attachment']:
                        return a
        raise ValueError(f"첨부를 찾을 수 없습니다: {t['skin']}/{t['slot']}/{t['attachment']}")

    @staticmethod
    def deform_vec(att, k):
        dense = k['dense']
        return [dense[j] if j < len(dense) else 0.0 for j in att['dense_map']]

    def draw_keys(self, t):
        index = {s['name']: i for i, s in enumerate(self.m['slots'])}
        out = []
        for i, k in enumerate(t['keys']):
            o = self.key_base(i, k)
            changed = sorted(k['offsets'], key=lambda r: index[r[0]])
            if changed:
                o['offsets'] = [{'slot': s, 'offset': n} for s, n in changed]
            out.append(o)
        return out

    def event_keys(self, t):
        out = []
        for i, k in enumerate(t['keys']):
            o = self.key_base(i, k)
            e = k['event']
            o['name'] = e['name']
            data = next(d for d in self.m['events'] if d['name'] == e['name'])
            if e['int'] != data['int']:
                o['int'] = e['int']
            if e['float'] != data['float']:
                o['float'] = self.n(e['float'])
            if e['string'] is not None and e['string'] != data['string']:
                o['string'] = e['string']
            if data['audio']:
                if e['volume'] != data['volume']:
                    o['volume'] = self.n(e['volume'])
                if e['balance'] != data['balance']:
                    o['balance'] = self.n(e['balance'])
            out.append(o)
        return out


def dumps(v):
    if isinstance(v, Num):
        return v.s
    if isinstance(v, dict):
        return '{' + ','.join(_s(k) + ':' + dumps(x) for k, x in v.items()) + '}'
    if isinstance(v, (list, tuple)):
        return '[' + ','.join(dumps(x) for x in v) + ']'
    if v is True:
        return 'true'
    if v is False:
        return 'false'
    if v is None:
        return 'null'
    if isinstance(v, int):
        return str(v)
    if isinstance(v, float):
        return fmt_f32(v)
    return _s(v)


def _s(s):
    out = ['"']
    for ch in s:
        if ch == '"':
            out.append('\\"')
        elif ch == '\\':
            out.append('\\\\')
        elif ch == '\n':
            out.append('\\n')
        elif ord(ch) < 0x20:
            out.append('\\u%04x' % ord(ch))
        else:
            out.append(ch)
    out.append('"')
    return ''.join(out)


def write_json(model, version, nonessential=True):
    return dumps(Writer(model, version, nonessential).build())
