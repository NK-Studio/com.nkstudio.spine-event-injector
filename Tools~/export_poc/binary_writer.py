"""모델 → Spine 바이너리(.skel).

런타임(spine-runtimes)의 SkeletonBinary가 읽는 순서 그대로 쓴다. 버전마다 다른 부분은 self.major로 가른다.
숫자는 모두 float32 빅엔디언, 정수는 varint(부호 없는 것) 또는 4바이트 int다.
"""
import struct

from json_writer import natural, f32, round_half_up, same_key_stepped, legacy_properties

INHERIT = ['normal', 'onlyTranslation', 'noRotationOrReflection', 'noScale', 'noScaleOrReflection']
BLEND = ['normal', 'additive', 'multiply', 'screen']
POSITION_MODE = ['fixed', 'percent']
SPACING_MODE = ['length', 'fixed', 'percent', 'proportional']
ROTATE_MODE = ['tangent', 'chain', 'chainScale']
ATT_TYPES = {'region': 0, 'boundingbox': 1, 'mesh': 2, 'linkedmesh': 3, 'path': 4, 'point': 5, 'clipping': 6}
SEQ_MODE = ['hold', 'once', 'loop', 'pingpong', 'onceReverse', 'loopReverse', 'pingpongReverse']
PROPS_INDEX = {'rotate': 0, 'x': 1, 'y': 2, 'scaleX': 3, 'scaleY': 4, 'shearY': 5}
CURVE_LINEAR, CURVE_STEPPED, CURVE_BEZIER = 0, 1, 2


class Out:
    def __init__(self):
        self.b = bytearray()

    def byte(self, v):
        self.b.append(v & 0xFF)

    def bool(self, v):
        self.byte(1 if v else 0)

    def varint(self, v):
        v &= 0xFFFFFFFF
        while True:
            x = v & 0x7F
            v >>= 7
            if v:
                self.b.append(x | 0x80)
            else:
                self.b.append(x)
                return

    def svarint(self, v):     # readInt(false): zigzag
        self.varint((v << 1) ^ (v >> 31))

    def int(self, v):
        self.b += struct.pack('>I', v & 0xFFFFFFFF)

    def float(self, v):
        self.b += struct.pack('>f', v)

    def string(self, s):
        if s is None:
            self.varint(0)
            return
        data = s.encode('utf-8')
        self.varint(len(s) + 1)
        self.b += data


def color_int(hexstr):
    return int(hexstr, 16) if hexstr else 0xFFFFFFFF


class Writer:
    def __init__(self, model, version, nonessential=False):
        self.m = model
        self.version = version
        self.major = tuple(int(x) for x in version.split('.')[:2])
        self.vtuple = tuple(int(x) for x in version.split('.')[:3])
        self.nonessential = nonessential
        self.fps = model['fps']
        self.o = Out()
        self.bone_index = {b['name']: i for i, b in enumerate(model['bones'])}
        self.slot_index = {s['name']: i for i, s in enumerate(model['slots'])}
        self.cons = model['constraints']
        self.iks = sorted((c for c in self.cons if c['type'] == 'ik'), key=lambda c: natural(c['name']))
        self.trs = sorted((c for c in self.cons if c['type'] == 'transform'), key=lambda c: natural(c['name']))
        self.paths = sorted((c for c in self.cons if c['type'] == 'path'), key=lambda c: natural(c['name']))
        self.phys = sorted((c for c in self.cons if c['type'] == 'physics'), key=lambda c: natural(c['name']))
        self.ik_index = {c['name']: i for i, c in enumerate(self.iks)}
        self.tr_index = {c['name']: i for i, c in enumerate(self.trs)}
        self.path_index = {c['name']: i for i, c in enumerate(self.paths)}
        self.phys_index = {c['name']: i for i, c in enumerate(self.phys)}
        self.event_index = {e['name']: i for i, e in enumerate(model['events'])}
        self.skins = [s for s in model['skins'] if s['attachments'] or s['name'] != 'default']
        self.strings = self.collect_strings()

    # ------------------------------------------------------------ 시간·문자열

    def sec(self, frame):
        if self.major >= (4, 3):
            return f32(f32(frame) / f32(self.fps))
        return f32(f32(frame) * f32(1.0 / self.fps))

    def collect_strings(self):
        out = list(self.m['strings'])
        if self.major < (4, 2):            # 4.0·4.1: 스킨·이벤트 이름도 문자열 표 끝에(참조로 쓴다)
            out += [x['name'] for x in self.skins if x['name'] != 'default']
            out += [e['name'] for e in self.m['events']]
        return out

    def ref(self, s):
        if s is None:
            self.o.varint(0)
            return
        # 같은 문자열이 여러 번 있으면 편집기는 마지막 것을 가리킨다(이름→번호 표를 덮어쓰며 만든 것으로 보임)
        self.o.varint(len(self.strings) - self.strings[::-1].index(s))

    # ------------------------------------------------------------ 전체

    def build(self):
        m, o = self.m, self.o
        o.b += bytes(8)                     # 해시: 본문을 다 쓴 뒤 채운다
        o.string(self.version)
        x, y, w, h = m['bounds'] or (0.0, 0.0, 0.0, 0.0)
        for v in (x, y, w, h):
            o.float(v)
        if self.major >= (4, 2):
            o.float(m['referenceScale'])
        o.bool(self.nonessential)
        if self.nonessential:
            o.float(m['fps'])
            o.string(m['images'])
            o.string(m['audio'])
        o.varint(len(self.strings))
        for s in self.strings:
            o.string(s)
        self.bones()
        self.slots()
        if self.major >= (4, 3):
            self.constraints43()
        else:
            self.ik()
            self.transform()
            self.path_constraints()
            if self.major >= (4, 2):
                self.physics_constraints()
        self.default_skin()
        o.varint(len(self.skins) - (1 if self.skins and self.skins[0]['name'] == 'default' else 0))
        for s in self.skins:
            if s['name'] != 'default':
                self.skin(s)
        o.varint(len(m['events']))
        for e in m['events']:
            self.event_data(e)
        o.varint(len(m['animations']))
        for an in m['animations']:
            o.string(an['name'])
            self.animation(an)
            if self.nonessential and self.major >= (4, 3):
                o.int(color_int(an['color']))
        if self.major >= (4, 3):            # 슬라이더가 쓰는 애니메이션 번호는 애니메이션을 다 읽은 뒤에 온다
            names = [an['name'] for an in m['animations']]
            for c in sorted(self.cons, key=lambda c: c['order']):
                if c['type'] == 'slider':
                    o.varint(names.index(c['animation']))
        import hashlib
        o.b[:8] = hashlib.sha1(bytes(o.b[8:])).digest()[:8]     # 편집기의 계산법은 미상. 내용으로 정해지는 값
        return bytes(o.b)

    # ------------------------------------------------------------ 설정 자세

    def bones(self):
        o = self.o
        o.varint(len(self.m['bones']))
        for i, b in enumerate(self.m['bones']):
            o.string(b['name'])
            if i:
                o.varint(self.bone_index[b['parent']])
            if self.major >= (4, 3):       # 4.3: 길이가 상속 방식 뒤로
                for k in ('rotation', 'x', 'y', 'scaleX', 'scaleY', 'shearX', 'shearY'):
                    o.float(b[k])
                o.byte(INHERIT.index(b['inherit']))
                o.float(b['length'])
            else:
                for k in ('rotation', 'x', 'y', 'scaleX', 'scaleY', 'shearX', 'shearY', 'length'):
                    o.float(b[k])
                o.byte(INHERIT.index(b['inherit']))
            o.bool(b['skin'])
            if self.nonessential:
                o.int(color_int(b['color']))
                if self.major >= (4, 2):
                    o.string(None if b['icon'] == 'bone' else b['icon'])
                    if self.major >= (4, 3):
                        o.float(b['editor33'])
                        o.float(b['editor34'])
                    o.bool(b['visible'])

    def slots(self):
        o = self.o
        o.varint(len(self.m['slots']))
        for s in self.m['slots']:
            o.string(s['name'])
            o.varint(self.bone_index[s['bone']])
            o.int(color_int(s['color']))
            o.int(int(s['dark'], 16) if s['dark'] else 0xFFFFFFFF)
            self.ref(s['attachment'])
            o.varint(BLEND.index(s['blend']))
            if self.nonessential and self.major >= (4, 2):
                o.bool(s['visible'])

    def ik(self):
        o = self.o
        o.varint(len(self.iks))
        for c in self.iks:
            o.string(c['name'])
            o.varint(c['order'])
            if self.major < (4, 2):        # 4.0·4.1: 플래그 묶음 없이 차례로
                o.bool(c['skin'])
                o.varint(len(c['bones']))
                for b in c['bones']:
                    o.varint(self.bone_index[b])
                o.varint(self.bone_index[c['target']])
                o.float(c['mix'])
                o.float(c['softness'])
                o.byte(1 if c['bendPositive'] else 0xFF)
                o.bool(c['compress'])
                o.bool(c['stretch'])
                o.bool(c['uniform'])
                continue
            o.varint(len(c['bones']))
            for b in c['bones']:
                o.varint(self.bone_index[b])
            o.varint(self.bone_index[c['target']])
            flags = (1 if c['skin'] else 0) | (2 if c['bendPositive'] else 0) | (4 if c['compress'] else 0) \
                | (8 if c['stretch'] else 0) | (16 if c['uniform'] else 0)
            if c['mix'] != 0:
                flags |= 32
            if c['mix'] != 1:
                flags |= 64
            if c['softness'] != 0:
                flags |= 128
            o.byte(flags)
            if flags & 96 == 96:            # 32: 0이 아님, 64: 1이 아님 → 둘 다면 값을 쓴다
                o.float(c['mix'])
            if flags & 128:
                o.float(c['softness'])

    def transform(self):
        o = self.o
        o.varint(len(self.trs))
        for c in self.trs:
            o.string(c['name'])
            o.varint(c['order'])
            if self.major < (4, 2):
                o.bool(c['skin'])
                o.varint(len(c['bones']))
                for b in c['bones']:
                    o.varint(self.bone_index[b])
                o.varint(self.bone_index[c['target']])
                o.bool(c['local'])
                o.bool(c['relative'])
                for k in ('rotation', 'x', 'y', 'scaleX', 'scaleY', 'shearY',
                          'mixRotate', 'mixX', 'mixY', 'mixScaleX', 'mixScaleY', 'mixShearY'):
                    o.float(c[k])
                continue
            o.varint(len(c['bones']))
            for b in c['bones']:
                o.varint(self.bone_index[b])
            o.varint(self.bone_index[c['target']])
            f1 = (1 if c['skin'] else 0) | (2 if c['local'] else 0) | (4 if c['relative'] else 0)
            vals1 = []
            for bit, k in ((8, 'rotation'), (16, 'x'), (32, 'y'), (64, 'scaleX'), (128, 'scaleY')):
                if c[k] != 0:
                    f1 |= bit
                    vals1.append(c[k])
            o.byte(f1)
            for v in vals1:
                o.float(v)
            f2, vals2 = 0, []
            for bit, k in ((1, 'shearY'), (2, 'mixRotate'), (4, 'mixX'), (8, 'mixY'), (16, 'mixScaleX'), (32, 'mixScaleY'),
                           (64, 'mixShearY')):
                if c[k] != 0:
                    f2 |= bit
                    vals2.append(c[k])
            o.byte(f2)
            for v in vals2:
                o.float(v)

    def path_body(self, c):
        """경로 제약 본문(뼈 목록 뒤부터)."""
        o = self.o
        pos, spc, rot = POSITION_MODE.index(c['positionMode']), SPACING_MODE.index(c['spacingMode']), ROTATE_MODE.index(c['rotateMode'])
        if self.major < (4, 2):
            o.varint(pos)
            o.varint(spc)
            o.varint(rot)
            o.float(c['rotation'])
        else:
            if self.major >= (4, 3):
                flags = (1 if c['skin'] else 0) | (pos << 1) | (spc << 2) | (rot << 4)
            else:
                flags = pos | (spc << 1) | (rot << 3)
            if c['rotation'] != 0:
                flags |= 128
            o.byte(flags)
            if flags & 128:
                o.float(c['rotation'])
        for k in ('position', 'spacing', 'mixRotate', 'mixX', 'mixY'):
            o.float(c[k])

    def path_constraints(self):
        o = self.o
        o.varint(len(self.paths))
        for c in self.paths:
            o.string(c['name'])
            o.varint(c['order'])
            o.bool(c['skin'])
            o.varint(len(c['bones']))
            for b in c['bones']:
                o.varint(self.bone_index[b])
            o.varint(self.slot_index[c['target']])
            self.path_body(c)

    def physics_body(self, c):
        """물리 제약 본문(뼈 번호 뒤부터)."""
        o = self.o
        flags, vals = (1 if c['skin'] else 0), []
        for bit, k in ((2, 'x'), (4, 'y'), (8, 'rotate'), (16, 'scaleX'), (32, 'shearX')):
            if c[k] != 0:
                flags |= bit
                vals.append(c[k])
        if c['limit'] != 5000:
            flags |= 64
            vals.append(c['limit'])
        if c['mass'] != 1:
            flags |= 128
        o.byte(flags)
        for v in vals:
            o.float(v)
        o.byte(int(round(c['fps'])))
        for k in ('inertia', 'strength', 'damping'):
            o.float(c[k])
        if flags & 128:
            o.float(1 / c['mass'])
        o.float(c['wind'])
        o.float(c['gravity'])
        f2 = 0
        for bit, k in ((1, 'inertiaGlobal'), (2, 'strengthGlobal'), (4, 'dampingGlobal'), (8, 'massGlobal'), (16, 'windGlobal'),
                       (32, 'gravityGlobal'), (64, 'mixGlobal')):
            if c[k]:
                f2 |= bit
        if c['mix'] != 1:
            f2 |= 128
        o.byte(f2)
        if f2 & 128:
            o.float(c['mix'])

    def physics_constraints(self):
        o = self.o
        o.varint(len(self.phys))
        for c in self.phys:
            o.string(c['name'])
            o.varint(c['order'])
            o.varint(self.bone_index[c['bone']])
            self.physics_body(c)

    def slider_body(self, c):
        o = self.o
        flags = (1 if c['skin'] else 0) | (2 if c['loop'] else 0)
        if self.nonessential and c['max'] != 0:     # 비필수 데이터가 없으면 max는 쓰지 않는다
            flags |= 8
        if c['mix'] != 0:
            flags |= 16
        if c['mix'] != 1:
            flags |= 32
        if c['bone'] is not None:
            flags |= 64
            if c['local']:
                flags |= 128
        o.byte(flags)
        if flags & 8:
            o.float(c['max'])
        if flags & 16 and flags & 32:
            o.float(c['mix'])
        if flags & 64:
            o.varint(self.bone_index[c['bone']])
            o.float(c['propertyOffset'])
            o.byte(PROPS_INDEX[c['property']])
            o.float(c['offset'])
            o.float(c['scale'])

    PROPS = ['rotate', 'x', 'y', 'scaleX', 'scaleY', 'shearY']

    def constraints43(self):
        """4.3: 제약을 적용 순서대로 한 목록에. 이름, 종류(0 ik, 2 transform), 종류별 본문."""
        o = self.o
        cons = sorted(self.cons, key=lambda c: c['order'])
        self.con_index = {c['name']: i for i, c in enumerate(cons)}
        o.varint(len(cons))
        for c in cons:
            o.string(c['name'])
            if c['type'] == 'path':
                o.byte(1)
                o.varint(len(c['bones']))
                for b in c['bones']:
                    o.varint(self.bone_index[b])
                o.varint(self.slot_index[c['target']])
                self.path_body(c)
                continue
            if c['type'] == 'physics':
                o.byte(3)
                o.varint(self.bone_index[c['bone']])
                self.physics_body(c)
                continue
            if c['type'] == 'slider':
                o.byte(4)
                self.slider_body(c)
                continue
            if c['type'] == 'ik':
                o.byte(0)
                o.varint(len(c['bones']))
                for b in c['bones']:
                    o.varint(self.bone_index[b])
                o.varint(self.bone_index[c['target']])
                two = len(c['bones']) == 2
                # 4.3: 1 skin, 2 uniform, 4 굽힘 음수(두 뼈), 8 compress, 16 stretch (표본 실측 + 추정)
                flags = (1 if c['skin'] else 0) | (2 if c['uniform'] and not two else 0) \
                    | (4 if not c['bendPositive'] else 0) | (8 if c['compress'] and not two else 0) \
                    | (16 if c['stretch'] else 0)
                if c['mix'] != 0:
                    flags |= 32
                if c['mix'] != 1:
                    flags |= 64
                if c['softness'] != 0:
                    flags |= 128
                o.byte(flags)
                if flags & 96 == 96:
                    o.float(c['mix'])
                if flags & 128:
                    o.float(c['softness'])
                continue
            o.byte(2)
            o.varint(len(c['bones']))
            for b in c['bones']:
                o.varint(self.bone_index[b])
            o.varint(self.bone_index[c['target']])
            props = c.get('properties')
            if props is None:
                props = legacy_properties(c)
            flags = (1 if c['skin'] else 0) | (2 if c['local'] else 0) | (4 if c['local'] else 0) \
                | (8 if c['relative'] else 0) | (len(props) << 5)
            o.byte(flags)
            for frm, off, tos in props:
                o.byte(self.PROPS.index(frm))
                o.float(off)
                o.varint(len(tos))
                for name, toff, mx, sc in tos:
                    o.byte(self.PROPS.index(name))
                    o.float(toff)
                    o.float(mx)
                    o.float(sc)
            targets = {name for _, _, tos in props for name, _, _, _ in tos}
            bits, vals = 0, []
            for bit, k, prop in ((1, 'rotation', 'rotate'), (2, 'x', 'x'), (4, 'y', 'y'), (8, 'scaleX', 'scaleX'),
                                 (16, 'scaleY', 'scaleY'), (32, 'shearY', 'shearY')):
                if prop in targets and c[k] != 0:   # 대상 속성이 없는 오프셋은 쓰지 않는다
                    bits |= bit
                    vals.append(c[k])
            o.byte(bits)
            for v in vals:
                o.float(v)
            bits, vals = 0, []
            for bit, k, prop in ((1, 'mixRotate', 'rotate'), (2, 'mixX', 'x'), (4, 'mixY', 'y'), (8, 'mixScaleX', 'scaleX'),
                                 (16, 'mixScaleY', 'scaleY'), (32, 'mixShearY', 'shearY')):
                if prop in targets and c[k] != 0:
                    bits |= bit
                    vals.append(c[k])
            o.byte(bits)
            for v in vals:
                o.float(v)

    # ------------------------------------------------------------ 스킨

    def default_skin(self):
        d = next((s for s in self.skins if s['name'] == 'default'), None)
        if d is None:
            self.o.varint(0)
            return
        self.skin_slots(d)

    def skin(self, s):
        o = self.o
        if self.major < (4, 2):
            self.ref(s['name'])
        else:
            o.string(s['name'])
        if self.nonessential and self.major >= (4, 2):
            o.int(color_int(s.get('color')))
        o.varint(len(s['bones']))
        for b in s['bones']:
            o.varint(self.bone_index[b])
        if self.major >= (4, 3):            # 4.3: 제약은 통합 목록의 번호 하나로
            o.varint(len(s['constraints']))
            for n in s['constraints']:
                o.varint(self.con_index[n])
        else:
            for idx in (self.ik_index, self.tr_index, self.path_index) + ((self.phys_index,) if self.major >= (4, 2) else ()):
                names = [n for n in s['constraints'] if n in idx]
                o.varint(len(names))
                for n in names:
                    o.varint(idx[n])
        self.skin_slots(s)

    def skin_slots(self, s):
        o = self.o
        by_slot = {}
        for slot, key, a in s['attachments']:
            by_slot.setdefault(slot, []).append((key, a))
        o.varint(len(by_slot))
        for slot in sorted(by_slot, key=natural):       # 편집기는 슬롯 이름순, 그 안은 첨부 이름순
            o.varint(self.slot_index[slot])
            atts = sorted(by_slot[slot], key=lambda r: natural(r[0]))
            o.varint(len(atts))
            for key, a in atts:
                self.ref(key)
                self.attachment(key, a)

    def attachment_old(self, key, a):
        """4.0·4.1: 이름 참조(같으면 null) → 종류 바이트 → 종류별 필드(플래그 없음)."""
        o = self.o
        t = a['type']
        self.ref(a['name'] if a['name'] != key else None)
        o.byte(ATT_TYPES[t])
        if t == 'region':
            self.ref(a['path'] or None)
            for k in ('rotation', 'x', 'y', 'scaleX', 'scaleY', 'width', 'height'):
                o.float(a[k])
            o.int(color_int(a['color']))
            self.sequence_old(a)
            return
        if t in ('boundingbox', 'clipping'):
            if t == 'clipping':
                o.varint(self.slot_index[a['end']])
            o.varint(a['vertexCount'])
            self.vertices_old(a)
            if self.nonessential:
                o.int(color_int(a['color']))
            return
        if t == 'mesh':
            self.ref(a['path'] or None)
            o.int(color_int(a['color']))
            o.varint(a['vertexCount'])
            for v in a['uvs']:
                o.float(v)
            o.varint(len(a['triangles']))
            for v in a['triangles']:
                o.b += struct.pack('>h', v)
            self.vertices_old(a)
            o.varint(a['hull'])
            self.sequence_old(a)
            if self.nonessential:
                o.varint(len(a['edges']))
                for v in a['edges']:
                    o.b += struct.pack('>h', v)
                o.float(a['width'])
                o.float(a['height'])
            return
        if t == 'linkedmesh':
            self.ref(a['path'] or None)
            o.int(color_int(a['color']))
            self.ref(a['skin'])
            self.ref(a['parent'])
            o.bool(a['timelines'])
            self.sequence_old(a)
            if self.nonessential:
                o.float(a['width'])
                o.float(a['height'])
            return
        if t == 'path':
            o.bool(a['closed'])
            o.bool(a['constantSpeed'])
            o.varint(a['vertexCount'])
            self.vertices_old(a)
            for v in a['lengths']:
                o.float(v)
            if self.nonessential:
                o.int(color_int(a['color']))
            return
        raise ValueError(t)

    def sequence_old(self, a):
        """4.1: 있음 여부 + 값. 4.0에는 시퀀스가 없다."""
        if self.major >= (4, 1):
            q = a.get('sequence')
            self.o.bool(q is not None)
            if q:
                self.sequence_fields(q)

    def sequence_fields(self, q):
        o = self.o
        o.varint(q['count'])
        o.varint(q['start'])
        o.varint(q['digits'])
        o.varint(q['setup'])

    def vertices_old(self, a):
        o = self.o
        o.bool(a['weighted'])
        v = a['vertices']
        if not a['weighted']:
            for x in v:
                o.float(x)
            return
        i = 0
        while i < len(v):
            n = v[i]
            o.varint(n)
            i += 1
            for _ in range(n):
                o.varint(v[i])
                o.float(v[i + 1])
                o.float(v[i + 2])
                o.float(round_half_up(v[i + 3], 5))     # 가중치는 5자리로 반올림된 값이 저장된다
                i += 4

    def attachment(self, key, a):
        if self.major < (4, 2):
            return self.attachment_old(key, a)
        o = self.o
        t = a['type']
        flags = ATT_TYPES[t]
        if a['name'] != key:
            flags |= 8
        if t == 'region':
            if a['path']:
                flags |= 16
            if a['color'] != 'ffffffff':
                flags |= 32
            if a.get('sequence'):
                flags |= 64
            if a['rotation'] != 0:
                flags |= 128
            o.byte(flags)
            if flags & 8:
                self.ref(a['name'])
            if flags & 16:
                self.ref(a['path'])
            if flags & 32:
                o.int(color_int(a['color']))
            if flags & 64:
                self.sequence_fields(a['sequence'])
            if flags & 128:
                o.float(a['rotation'])
            for k in ('x', 'y', 'scaleX', 'scaleY', 'width', 'height'):
                o.float(a[k])
            return
        if t in ('boundingbox', 'clipping'):
            if a['weighted']:
                flags |= 16
            if t == 'clipping' and self.major >= (4, 3) and a.get('convex'):
                flags |= 32                 # 4.3: 볼록 클리핑
            o.byte(flags)
            if flags & 8:
                self.ref(a['name'])
            if t == 'clipping':
                o.varint(self.slot_index[a['end']])
            self.vertices(a)
            if self.nonessential:
                o.int(color_int(a['color']))
            return
        if t == 'mesh':
            if a['path']:
                flags |= 16
            if a['color'] != 'ffffffff':
                flags |= 32
            if a.get('sequence'):
                flags |= 64
            if a['weighted']:
                flags |= 128
            o.byte(flags)
            if flags & 8:
                self.ref(a['name'])
            if flags & 16:
                self.ref(a['path'])
            if flags & 32:
                o.int(color_int(a['color']))
            if flags & 64:
                self.sequence_fields(a['sequence'])
            o.varint(a['hull'])
            self.vertices(a)
            for v in a['uvs']:
                o.float(v)
            for v in a['triangles']:
                o.varint(v)
            if self.major >= (4, 3):
                o.varint(0)                 # 4.3에서 생긴 값(뜻 미상, 표본은 모두 0)
            if self.nonessential:
                o.varint(len(a['edges']))
                for v in a['edges']:
                    o.varint(v)
                o.float(a['width'])
                o.float(a['height'])
            return
        if t == 'linkedmesh':
            if a['path']:
                flags |= 16
            if a['color'] != 'ffffffff':
                flags |= 32
            if a.get('sequence'):
                flags |= 64
            if a['timelines']:
                flags |= 128
            o.byte(flags)
            if flags & 8:
                self.ref(a['name'])
            if flags & 16:
                self.ref(a['path'])
            if flags & 32:
                o.int(color_int(a['color']))
            if flags & 64:
                self.sequence_fields(a['sequence'])
            if self.major >= (4, 3):
                o.varint(self.slot_index[a['parentSlot']])
            order = [x['name'] for x in self.skins]
            o.varint(order.index(a['skin'] or 'default'))
            self.ref(a['parent'])
            if self.nonessential:
                o.float(a['width'])
                o.float(a['height'])
            return
        if t == 'path':
            if a['closed']:
                flags |= 16
            if a['constantSpeed']:
                flags |= 32
            if a['weighted']:
                flags |= 64
            o.byte(flags)
            if flags & 8:
                self.ref(a['name'])
            self.vertices(a)
            for v in a['lengths']:
                o.float(v)
            if self.nonessential:
                o.int(color_int(a['color']))
            return
        raise ValueError(t)

    def vertices(self, a):
        o = self.o
        o.varint(a['vertexCount'])
        v = a['vertices']
        if not a['weighted']:
            for x in v:
                o.float(x)
            return
        if self.major >= (4, 3):           # 4.3: 뼈 배열 길이(정점마다 1 + 뼈 수)를 먼저
            n, i = 0, 0
            while i < len(v):
                n += 1 + v[i]
                i += 1 + 4 * v[i]
            o.varint(n)
        i = 0
        while i < len(v):
            n = v[i]
            o.varint(n)
            i += 1
            for _ in range(n):
                o.varint(v[i])
                o.float(v[i + 1])
                o.float(v[i + 2])
                o.float(round_half_up(v[i + 3], 5))     # 가중치는 5자리로 반올림된 값이 저장된다
                i += 4

    def event_data(self, e):
        o = self.o
        if self.major < (4, 2):
            self.ref(e['name'])
        else:
            o.string(e['name'])
        o.svarint(e['int'])
        o.float(e['float'])
        o.string(e['string'])
        o.string(e['audio'] or None)
        if e['audio']:
            o.float(e['volume'])
            o.float(e['balance'])

    # ------------------------------------------------------------ 애니메이션

    def curve_kind(self, keys, i):
        """JSON 출력기와 같은 규칙으로 곡선 종류와 손잡이를 정한다."""
        from json_writer import Writer as JW
        return JW.curve(self._jw, keys, i, self._kinds)

    def animation(self, an):
        from json_writer import Writer as JW
        self._jw = JW(self.m, self.version)
        o = self.o
        tls = an['timelines']
        count = 0
        slots, bones, iks, trs, deforms, events = {}, {}, {}, {}, {}, []
        paths, phys, sliders, draw = {}, {}, {}, []
        for t in tls:
            ty = t['type']
            if ty in ('color', 'attachment'):
                slots.setdefault(t['slot'], []).append(t)
            elif ty in ('rotate', 'translate', 'scale', 'shear', 'translatex', 'translatey'):
                bones.setdefault(t['bone'], []).append(t)
            elif ty == 'ik':
                iks[t['constraint']] = t
            elif ty == 'transform':
                trs[t['constraint']] = t
            elif ty in ('deform', 'sequence'):
                deforms.setdefault(t['skin'], {}).setdefault(t['slot'], []).append(t)
            elif ty == 'event':
                events = sorted(events + t['keys'], key=lambda k: k['frame'])     # 정의별 타임라인을 시간순으로 합친다
            elif ty in ('pathPosition', 'pathSpacing', 'pathMix'):
                paths.setdefault(t['constraint'], []).append(t)
            elif ty == 'physicsWind':
                phys.setdefault(t['constraint'], []).append(t)
            elif ty == 'physicsWindAll':
                phys.setdefault('', []).append(t)
            elif ty == 'sliderMix':
                sliders.setdefault(t['constraint'], []).append(t)
            elif ty == 'drawOrder':
                draw = t['keys']
        count = sum(len(v) for v in slots.values()) + sum(len(v) for v in bones.values()) + len(iks) + len(trs) \
            + sum(len(v) for sk in deforms.values() for v in sk.values()) + (1 if events else 0) + (1 if draw else 0) \
            + sum(len(v) for v in paths.values()) + sum(len(v) for v in phys.values()) + sum(len(v) for v in sliders.values())
        o.varint(count)
        # 슬롯
        o.varint(len(slots))
        for slot in sorted(slots, key=natural):          # JSON과 같은 순서: 슬롯은 이름순
            o.varint(self.slot_index[slot])
            o.varint(len(slots[slot]))
            for t in slots[slot]:
                self.slot_timeline(t)
        # 뼈
        o.varint(len(bones))
        border = sorted(bones, key=natural) if self.major >= (4, 3) else list(bones)   # 4.2까지 뼈는 프로젝트 순서
        for bone in border:
            o.varint(self.bone_index[bone])
            o.varint(len(bones[bone]))
            for t in bones[bone]:
                self.bone_timeline(t)
        # IK
        o.varint(len(iks))
        for name in sorted(iks, key=lambda n: self.ik_index[n]):
            self.ik_timeline(iks[name])
        # 변형
        o.varint(len(trs))
        for name in sorted(trs, key=lambda n: self.tr_index[n]):
            self.tr_timeline(trs[name])
        v43 = self.major >= (4, 3)
        path_idx = (lambda n: self.con_index[n]) if v43 else (lambda n: self.path_index[n])
        o.varint(len(paths))
        for name in sorted(paths, key=path_idx):
            o.varint(path_idx(name))
            o.varint(len(paths[name]))
            for t in paths[name]:
                code, nch = {'pathPosition': (0, 1), 'pathSpacing': (1, 1), 'pathMix': (2, 3)}[t['type']]
                self.typed_timeline(t, code, nch)
        if self.major >= (4, 2):            # 물리 번호는 1부터(0 = 모든 물리 제약)
            phys_idx = lambda n: 0 if n == '' else (self.con_index[n] if v43 else self.phys_index[n]) + 1
            o.varint(len(phys))
            for name in sorted(phys, key=phys_idx):
                o.varint(phys_idx(name))
                o.varint(len(phys[name]))
                for t in phys[name]:
                    self.typed_timeline(t, 5, 1)
        if v43:
            o.varint(len(sliders))
            for name in sorted(sliders, key=lambda n: self.con_index[n]):
                o.varint(self.con_index[name])
                o.varint(len(sliders[name]))
                for t in sliders[name]:
                    self.typed_timeline(t, 1, 1)
        # 첨부(변형)
        o.varint(len(deforms))
        skin_order = [x['name'] for x in self.skins]
        for skin in sorted(deforms, key=skin_order.index):
            o.varint(skin_order.index(skin))
            o.varint(len(deforms[skin]))
            for slot in sorted(deforms[skin], key=natural):
                o.varint(self.slot_index[slot])
                o.varint(len(deforms[skin][slot]))
                for t in deforms[skin][slot]:
                    if t['type'] == 'sequence':
                        self.sequence_timeline(t)
                    else:
                        self.deform_timeline(t)
        o.varint(len(draw))                 # 그리기 순서
        for k in draw:
            o.float(self.sec(k['frame']))
            changed = sorted(k['offsets'], key=lambda r: self.slot_index[r[0]])
            o.varint(len(changed))
            for slot, offset in changed:
                o.varint(self.slot_index[slot])
                o.varint(offset)
        if v43:
            o.varint(0)                     # 드로우 오더 폴더 타임라인
        o.varint(len(events))
        for k in events:
            self.event_key(k)

    def bezier_count(self, keys, nch):
        n = 0
        for i in range(len(keys) - 1):
            if self.curve(keys, i, nch)[0] == CURVE_BEZIER:
                n += nch
        return n

    def curve(self, keys, i, nch):
        self._kinds = ['pos'] * nch
        from json_writer import Writer as JW
        # 손잡이를 프레임 그대로 구한다(JSON처럼 반올림하지 않는다)
        k, nxt = keys[i], keys[i + 1]
        if k['curve'] == 'stepped':
            return CURVE_STEPPED, None
        if same_key_stepped(k, nxt, nch):
            return CURVE_STEPPED, None
        if 'dense' in k and k['curve'] == 'linear' and len(k['dense']) == len(nxt['dense']) \
                and all(abs(a - b) <= 0.0001 for a, b in zip(k['dense'], nxt['dense'])):
            return CURVE_STEPPED, None
        if k['curve'] == 'linear':
            return CURVE_LINEAR, None
        t0, t1 = k['frame'], nxt['frame']
        hs = []
        for c in range(nch):
            if k['values']:
                v0, v1 = k['values'][c], nxt['values'][c]
            else:
                v0, v1 = 0.0, 1.0
            h0 = k['handles'][c] if c < len(k['handles']) else None
            h1 = nxt['handles'][c] if c < len(nxt['handles']) else None
            cx1, cy1 = (h0[2], h0[3]) if h0 and h0[2] != 2147483648.0 else (t0 + (t1 - t0) / 3, v0)
            cx2, cy2 = (h1[0], h1[1]) if h1 and h1[0] != 2147483648.0 else (t1 - (t1 - t0) / 3, v1)
            hs.append((self.sec(cx1), cy1, self.sec(cx2), cy2))
        return CURVE_BEZIER, hs

    def write_curve(self, keys, i, nch):
        kind, hs = self.curve(keys, i, nch)
        self.o.byte(kind)
        if kind == CURVE_BEZIER:
            for h in hs:
                for v in h:
                    self.o.float(v)

    def values_timeline(self, t, nch, scale_vals=None):
        o = self.o
        keys = t['keys']
        for i, k in enumerate(keys):           # 런타임은 다음 키를 읽은 뒤 앞 구간의 곡선을 읽는다
            o.float(self.sec(k['frame']))
            for c in range(nch):
                o.float(k['values'][c])
            if i:
                self.write_curve(keys, i - 1, nch)

    def typed_timeline(self, t, code, nch):
        """종류 바이트, 키 수, 베지어 수, 키들 — 경로·물리·슬라이더 타임라인 공통."""
        o = self.o
        o.byte(code)
        o.varint(len(t['keys']))
        o.varint(self.bezier_count(t['keys'], nch))
        self.values_timeline(t, nch)

    def bone_timeline(self, t):
        o = self.o
        ty = t['type']
        code = {'rotate': 0, 'translate': 1, 'translatex': 2, 'translatey': 3, 'scale': 4, 'shear': 7}[ty]
        nch = 1 if ty in ('rotate', 'translatex', 'translatey') else 2
        o.byte(code)
        o.varint(len(t['keys']))
        o.varint(self.bezier_count(t['keys'], nch))
        self.values_timeline(t, nch)

    def slot_timeline(self, t):
        o = self.o
        keys = t['keys']
        if t['type'] == 'attachment':
            o.byte(0)
            o.varint(len(keys))
            for k in keys:
                o.float(self.sec(k['frame']))
                self.ref(k['name'])
            return
        slot = next(s for s in self.m['slots'] if s['name'] == t['slot'])
        two = slot['dark'] is not None
        nch = 7 if two else 4
        o.byte(3 if two else 1)
        o.varint(len(keys))
        o.varint(self.bezier_count(keys, nch))
        for i, k in enumerate(keys):
            o.float(self.sec(k['frame']))
            for c in range(nch):
                o.byte(int(round(k['values'][c] * 255)))
            if i:
                self.write_curve(keys, i - 1, nch)

    def ik_timeline(self, t):
        o = self.o
        keys = t['keys']
        o.varint(self.con_index[t['constraint']] if self.major >= (4, 3) else self.ik_index[t['constraint']])
        o.varint(len(keys))
        o.varint(self.bezier_count(keys, 2))
        if self.major < (4, 2):            # 4.0·4.1: 시간, mix, softness, 굽힘 방향, compress, stretch, 곡선
            for i, k in enumerate(keys):
                mix, soft, stretch, compress, bend = k['values']
                o.float(self.sec(k['frame']))
                o.float(mix)
                o.float(soft)
                if i:
                    self.write_curve(keys, i - 1, 2)
                o.byte(1 if bend else 0xFF)
                o.bool(compress)
                o.bool(stretch)
            return
        for i, k in enumerate(keys):
            mix, soft, stretch, compress, bend = k['values']
            flags = 0
            # 4.2.22~: 1 = mix가 0이 아님, 2 = 1이 아님. 4.2.00~4.2.20은 두 비트가 반대였다(표본 실측)
            if i and (4, 2, 0) <= self.vtuple < (4, 2, 22):
                # 4.2.00~4.2.20 편집기는 두 번째 키부터 mix·softness를 앞 키 값으로 썼다(표본 실측, 편집기 버그로 보임)
                mix, soft = keys[i - 1]['values'][0], keys[i - 1]['values'][1]
            if mix != 0:
                flags |= 1
            if mix != 1:
                flags |= 2
            if soft != 0:
                flags |= 4
            if bend:
                flags |= 8
            if compress:
                flags |= 16
            if stretch:
                flags |= 32
            if i:
                kind, hs = self.curve(keys, i - 1, 2)
                if kind == CURVE_STEPPED:
                    flags |= 64
                elif kind == CURVE_BEZIER:
                    flags |= 128
            o.byte(flags)
            o.float(self.sec(k['frame']))
            if flags & 3 == 3:
                o.float(mix)
            if flags & 4:
                o.float(soft)
            if flags & 128:
                for h in hs:
                    for v in h:
                        o.float(v)

    def tr_timeline(self, t):
        o = self.o
        keys = t['keys']
        o.varint(self.con_index[t['constraint']] if self.major >= (4, 3) else self.tr_index[t['constraint']])
        o.varint(len(keys))
        o.varint(self.bezier_count(keys, 6))
        self.values_timeline(t, 6)

    def sequence_timeline(self, t):
        o = self.o
        self.ref(t['attachment'])
        o.byte(1)
        o.varint(len(t['keys']))
        for k in t['keys']:
            o.float(self.sec(k['frame']))
            mode, index, delay = k['values']
            o.int((int(index) << 4) | int(mode))
            o.float(delay)

    def deform_timeline(self, t):
        from json_writer import Writer as JW
        o = self.o
        att = self._jw.find_att(t)
        keys = t['keys']
        self.ref(t['attachment'])
        if self.major >= (4, 1):           # 4.1~: 첨부 타임라인 종류(0 = deform). 4.0에는 없다
            o.byte(0)
        o.varint(len(keys))
        o.varint(self.bezier_count(keys, 1))
        for i, k in enumerate(keys):
            vec = JW.deform_vec(att, k)
            nz = [j for j, x in enumerate(vec) if x != 0]   # 바이너리는 정확히 0인 앞뒤만 자른다(JSON과 다름)
            o.float(self.sec(k['frame']))
            if i:
                self.write_curve(keys, i - 1, 1)
            if not nz:
                o.varint(0)
            else:
                start, end = nz[0], nz[-1] + 1
                o.varint(end - start)
                o.varint(start)
                for v in vec[start:end]:
                    o.float(v)

    def event_key(self, k):
        o = self.o
        e = k['event']
        data = next(d for d in self.m['events'] if d['name'] == e['name'])
        o.float(self.sec(k['frame']))
        o.varint(self.event_index[e['name']])
        o.svarint(e['int'])
        o.float(e['float'])
        o.string(e['string'] if e['string'] is not None and e['string'] != data['string'] else None)
        if data['audio']:
            o.float(e['volume'])
            o.float(e['balance'])


class Unsupported(Exception):
    """바이너리 출력이 아직 다루지 못하는 기능(JSON은 지원). 빠뜨린 채 쓰지 않고 멈춘다."""


def unsupported(model, version):
    """이 런타임 버전의 .skel에 넣을 수 없는 기능."""
    major = tuple(int(x) for x in version.split('.')[:2])
    bad = set()
    for c in model['constraints']:
        if c['type'] == 'slider' and major < (4, 3):
            bad.add('슬라이더 제약(4.3 전용)')
        if c['type'] == 'physics' and major < (4, 2):
            bad.add('물리 제약(4.2 이상)')
    if major < (4, 1):
        for s in model['skins']:
            for _, _, a in s['attachments']:
                if a.get('sequence'):
                    bad.add('시퀀스 첨부(4.1 이상)')
    return sorted(bad)


def write_binary(model, version, nonessential=False):
    bad = unsupported(model, version)
    if bad:
        raise Unsupported(', '.join(bad))
    return Writer(model, version, nonessential).build()
