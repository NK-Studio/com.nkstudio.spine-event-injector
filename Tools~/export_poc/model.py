"""프로젝트 그래프 → 버전 중립 스켈레톤 모델.

모델은 Spine 4.2 JSON과 같은 이름을 쓰는 dict다. 값은 반올림하지 않은 float32 그대로 두고,
기본값도 모두 채운다(무엇을 생략할지는 출력기가 버전 규칙대로 정한다).
애니메이션의 시간은 프레임 단위 그대로 두고(`frame`), 출력기가 fps로 나눈다.

필드 태그 번호는 4.0~4.3 프로젝트에서 같다(포맷마다 키 모양만 다르다 — project_reader가 감춘다).
"""
from project_reader import Node

INHERIT = {1: 'normal', 2: 'onlyTranslation', 3: 'noRotationOrReflection', 4: 'noScale', 5: 'noScaleOrReflection'}
BLEND = {1: 'normal', 2: 'additive', 3: 'multiply', 4: 'screen'}
SPACING_MODE = {1: 'length', 2: 'fixed', 3: 'percent', 4: 'proportional'}
ROTATE_MODE = {1: 'tangent', 2: 'chain', 3: 'chainScale'}
POSITION_MODE = {1: 'fixed', 2: 'percent'}
UNSET = 2147483648.0            # 곡선 손잡이가 정해지지 않았다는 표시(0x4f000000)

# 타임라인 종류 → (모델 이름, 값 채널 이름들)
TIMELINES = {
    0: ('rotate', ['value']),
    1: ('translate', ['x', 'y']),
    2: ('scale', ['x', 'y']),
    3: ('shear', ['x', 'y']),
    4: ('color', ['r', 'g', 'b', 'a', 'dr', 'dg', 'db']),
    5: ('attachment', []),
    6: ('deform', []),
    7: ('ik', ['mix', 'softness', 'stretch', 'compress', 'bendPositive']),
    8: ('transform', ['mixRotate', 'mixX', 'mixY', 'mixScaleX', 'mixScaleY', 'mixShearY']),
    9: ('pathPosition', ['value']),
    10: ('pathSpacing', ['value']),
    11: ('pathMix', ['mixRotate', 'mixX', 'mixY']),
    12: ('event', []),
    13: ('drawOrder', []),
    14: ('translatex', ['value']),
    15: ('translatey', ['value']),
    22: ('sequence', ['mode', 'index', 'delay']),
    27: ('physicsWind', ['value']),
    36: ('physicsWindAll', ['value']),
    40: ('sliderMix', ['value']),
}
# 곡선이 붙는 채널 수(나머지 값은 계단)
CURVE_CHANNELS = {0: 1, 1: 2, 2: 2, 3: 2, 4: 7, 6: 1, 7: 2, 8: 6, 9: 1, 10: 1, 11: 3, 14: 1, 15: 1, 27: 1, 36: 1, 40: 1}
SEQ_MODE = ['hold', 'once', 'loop', 'pingpong', 'onceReverse', 'loopReverse', 'pingpongReverse']


def rgba(c):
    return '%02x%02x%02x%02x' % tuple(c) if c is not None else None


def rgb(c):
    return '%02x%02x%02x' % tuple(c[:3]) if c is not None else None


def zigzag_dec(v):
    return (v >> 1) ^ -(v & 1)


class ModelError(Exception):
    pass


class Reader:
    def __init__(self, project):
        self.p = project
        self.doc = project.doc
        self.new_style = 'timeline' in self.doc.kinds.values()
        self.fps = 30.0
        self.full = {}          # 폴더 안 슬롯·제약의 전체 이름(폴더/이름)

    # ------------------------------------------------------------ 작은 도우미

    def name(self, n):
        if n is None:
            return None
        full = self.full.get(id(n))
        return full if full is not None else self.p.get(n, 1, kind='str')

    def node(self, n, tag, cls=None):
        return self.p.get(n, tag, kind=cls or 'node')

    def full_name(self, n, folder_tag):
        """폴더 안 항목의 이름: 폴더 경로(Folder 1 이름, 3 부모)/이름. 뿌리 폴더는 이름이 없다."""
        name = self.p.get(n, 1, kind='str')
        f = self.p.get(n, folder_tag, kind='Folder')
        while f is not None:
            part = self.p.get(f, 1, kind='str')
            if part:
                name = part + '/' + name
            f = self.p.get(f, 3, kind='Folder')
        return name

    def skin_name(self, s):
        return self.full_name(s, 5)

    def lst(self, n, tag):
        return self.p.items(self.p.get(n, tag, kind='List'))

    def f(self, n, tag, default=0.0):
        v = self.p.get(n, tag, kind='num')
        return float(v) if v is not None else default

    def i(self, n, tag, default=0):
        v = self.p.get(n, tag, kind='num')
        return int(v) if v is not None else default

    def leaf(self, n, tag, default=None):
        v = self.p.get(n, tag, kind='node')
        return self.p.leaf(v) if v is not None else default

    # ------------------------------------------------------------ 전체

    def read(self, sk):
        p = self.p
        proj = p.root
        self.fps = self.f(proj, 2, 30.0) or 30.0
        bounds = self.leaf(sk, 24)
        m = {
            'name': p.get(sk, 1, kind='str'),
            'format': p.format,
            'fps': self.fps,
            'images': p.get(sk, 3, kind='str') or '',
            'audio': p.get(sk, 34, kind='str') or '',
            'referenceScale': self.f(sk, 46, 100.0),
            'bounds': bounds,
        }
        self.bones = self.lst(sk, 4)
        self.bone_index = {id(b): i for i, b in enumerate(self.bones)}
        self.slots = self.lst(sk, 13)
        self.slot_index = {id(s): i for i, s in enumerate(self.slots)}
        for s0 in self.slots:
            self.full[id(s0)] = self.full_name(s0, 16)
        for c0 in self.lst(sk, 16) + self.lst(sk, 22) + self.lst(sk, 23) + self.constraint_order(sk):
            self.full[id(c0)] = self.full_name(c0, 32)
        m['bones'] = [self.bone(b) for b in self.bones]
        m['slots'] = [self.slot(s) for s in self.slots]
        m['constraints'] = self.constraints(sk)
        self.skin_nodes = self.lst(sk, 8)
        in_skin = {id(b) for s in self.skin_nodes for b in self.lst(s, 6)}
        for b, bm in zip(self.bones, m['bones']):
            bm['skin'] = id(b) in in_skin
        m['skins'] = self.skins(sk)
        m['strings'] = self.string_table(sk)
        m['events'] = [self.event_data(e) for e in self.lst(sk, 10)]
        m['animations'] = [self.animation(a) for a in self.lst(sk, 7) if self.i(a, 5, 1) != 0]     # 5: 내보내기 포함 여부
        return m

    # ------------------------------------------------------------ 뼈·슬롯

    def bone(self, b):
        p = self.p
        parent = self.node(b, 3, 'Bone')
        return {
            'name': self.name(b),
            'parent': self.name(parent) if parent is not None else None,
            'length': self.f(b, 9), 'x': self.f(b, 10), 'y': self.f(b, 11), 'rotation': self.f(b, 12),
            'scaleX': self.f(b, 13, 1.0), 'scaleY': self.f(b, 14, 1.0),
            'shearX': self.f(b, 23), 'shearY': self.f(b, 25),
            'inherit': INHERIT.get(self.leaf(b, 27, 1), 'normal'),
            'color': rgba(self.leaf(b, 17)),
            'icon': p.get(b, 28, kind='str') or 'bone',
            'visible': self.i(b, 2, 1) != 0,
            'editor33': self.f(b, 33, 1.0), 'editor34': self.f(b, 34),   # 4.3 비필수 float 두 개(뜻 미상, 그대로 옮긴다)
        }

    def slot(self, s):
        att = self.node(s, 9)
        if att is not None and not self.exported(att):
            att = None
        dark = self.i(s, 12) != 0
        return {
            'name': self.name(s),
            'bone': self.name(self.node(s, 2, 'Bone')),
            'color': rgba(self.leaf(s, 5, (255, 255, 255, 255))),
            'dark': rgb(self.leaf(s, 11)) if dark else None,
            'attachment': self.att_key(att) if att is not None else None,
            'blend': BLEND.get(self.leaf(s, 7, 1), 'normal'),
            'visible': self.i(s, 3, 1) != 0,
        }

    # ------------------------------------------------------------ 제약

    def constraint_order(self, sk):
        old = self.p.get(sk, 25, kind='List')
        if old is not None:
            return self.p.items(old)
        folder = self.p.get(sk, 44, kind='Folder')
        if folder is None:
            for c in self.p.of('IkConstraint') + self.p.of('TransformConstraint'):
                if self.p.get(c, 0, kind='Skeleton') is not sk:     # 다른 스켈레톤의 제약 폴더를 쓰지 않는다
                    continue
                folder = self.p.get(c, 32, kind='Folder')
                if folder is not None:
                    break
        out = []

        def walk(f):
            for x in self.p.items(self.p.get(f, 5, kind='List') or self.p.get(f, 4, kind='List')):
                if isinstance(x, Node) and x.cls == 'Folder':
                    walk(x)
                elif isinstance(x, Node):
                    out.append(x)
        if folder is not None:
            walk(folder)
        out.reverse()
        return out

    def constraints(self, sk):
        order = self.constraint_order(sk)
        nodes = self.lst(sk, 16) + self.lst(sk, 22) + self.lst(sk, 23)
        seen = {id(n) for n in order}
        order += [n for n in nodes if id(n) not in seen]
        out = []
        for i, c in enumerate(order):
            skins = self.lst(c, 22)
            if c.cls == 'IkConstraint':
                out.append({
                    'type': 'ik', 'name': self.name(c), 'order': i, 'skin': bool(skins),
                    'bones': [self.name(b) for b in self.lst(c, 2)],
                    'target': self.name(self.node(c, 3, 'Bone')),
                    'mix': self.f(c, 5, 1.0), 'softness': self.f(c, 14),
                    'bendPositive': self.i(c, 16, 1) != 0,
                    'compress': self.i(c, 9) != 0, 'stretch': self.i(c, 11) != 0, 'uniform': self.i(c, 13) != 0,
                })
            elif c.cls == 'TransformConstraint':
                out.append({
                    'type': 'transform', 'name': self.name(c), 'order': i, 'skin': bool(skins),
                    'bones': [self.name(b) for b in self.lst(c, 19)],
                    'target': self.name(self.node(c, 3, 'Bone')),
                    'rotation': self.f(c, 14), 'x': self.f(c, 6), 'y': self.f(c, 7),
                    'scaleX': self.f(c, 16), 'scaleY': self.f(c, 17), 'shearY': self.f(c, 18),
                    'local': self.i(c, 20) != 0, 'relative': self.i(c, 21) != 0,
                    'mixRotate': self.f(c, 8, 1.0), 'mixX': self.f(c, 4, 1.0), 'mixY': self.f(c, 23, 1.0),
                    'mixScaleX': self.f(c, 10, 1.0), 'mixScaleY': self.f(c, 25, 1.0), 'mixShearY': self.f(c, 12, 1.0),
                    'properties': self.transform_props(c),
                })
            elif c.cls == 'PathConstraint':
                out.append({
                    'type': 'path', 'name': self.name(c), 'order': i, 'skin': bool(skins),
                    'bones': [self.name(b) for b in self.lst(c, 2)],
                    'target': self.name(self.node(c, 3, 'Slot')),
                    'spacingMode': SPACING_MODE.get(self.leaf(c, 14, 1), 'length'),
                    'rotateMode': ROTATE_MODE.get(self.leaf(c, 15, 1), 'tangent'),
                    'positionMode': POSITION_MODE.get(self.leaf(c, 16, 2), 'percent'),
                    'position': self.f(c, 4), 'spacing': self.f(c, 12), 'rotation': self.f(c, 10),
                    'mixRotate': self.f(c, 8, 1.0), 'mixX': self.f(c, 6, 1.0), 'mixY': self.f(c, 23, 1.0),
                })
            elif c.cls == 'PhysicsConstraint':
                # 저장값 a(태그 7)·b(태그 9) → rotate = a·t, shearX = b·t, t = a/(a+b) (a가 0이면 t = 1).
                # 예제 저장본의 (1,0) (0,1) (1,1) (0.5707,0.2228)로 맞춘 식이다.
                a0, b0 = self.f(c, 7), self.f(c, 9)
                t0 = a0 / (a0 + b0) if a0 else 1.0
                rot, she = a0 * t0, b0 * t0
                out.append({
                    'type': 'physics', 'name': self.name(c), 'order': i, 'skin': bool(skins),
                    'bone': self.name(self.node(c, 3, 'Bone')),
                    'x': self.f(c, 5), 'y': self.f(c, 6), 'rotate': rot, 'scaleX': self.f(c, 8), 'shearX': she,
                    'limit': self.f(c, 33, 5000.0), 'fps': 1.0 / self.f(c, 4, 1.0 / 60), 'inertia': self.f(c, 10, 1.0),
                    'strength': self.f(c, 12, 100.0), 'damping': self.f(c, 14, 1.0), 'mass': self.f(c, 16, 1.0),
                    'wind': self.f(c, 18), 'gravity': self.f(c, 20), 'mix': self.f(c, 23, 1.0),
                    'inertiaGlobal': self.i(c, 25) != 0, 'strengthGlobal': self.i(c, 26) != 0, 'dampingGlobal': self.i(c, 27) != 0,
                    'massGlobal': self.i(c, 28) != 0, 'windGlobal': self.i(c, 29) != 0, 'gravityGlobal': self.i(c, 30) != 0,
                    'mixGlobal': self.i(c, 31) != 0,
                })
            elif c.cls == 'SliderConstraint':
                prop = self.node(c, 11)
                out.append({
                    'type': 'slider', 'name': self.name(c), 'order': i, 'skin': bool(skins),
                    'animation': self.full_name(self.node(c, 2, 'Animation'), 7) if self.node(c, 2, 'Animation') is not None else None,
                    'bone': self.name(self.node(c, 10, 'Bone')) if self.node(c, 10, 'Bone') is not None else None,
                    'property': self.PROPS[int(prop.cls[6:]) - 134] if prop is not None else None,
                    'loop': self.i(c, 9) != 0, 'local': self.i(c, 13) != 0,
                    'max': self.f(c, 4), 'scale': self.f(c, 12, 1.0), 'mix': self.f(c, 6, 1.0), 'offset': self.f(c, 15),
                    'propertyOffset': prop.f['p0'][2] if prop is not None else 0.0,
                })
            else:
                raise ModelError(f'아직 지원하지 않는 제약: {c.cls}')
        return out

    PROPS = ['rotate', 'x', 'y', 'scaleX', 'scaleY', 'shearY']

    def transform_props(self, c):
        """4.3: 속성 목록(29) — XfFrom134~139(rotate…shearY) {p0 오프셋, p1 [XfTo140~145 (오프셋, 최대, 배율)]}.
        4.2까지는 없다(None) — 출력기가 여섯 속성 모두로 본다."""
        import struct
        lst = self.p.get(c, 29, kind='List')
        if lst is None:
            return None
        out = []
        for x in self.p.items(lst):
            if not x.cls.startswith('XfFrom'):
                continue
            tos = []
            for t in x.f['p1'][2] or []:
                t = self.p.deref(t)
                off, mx, sc = struct.unpack('>3f', self.p.body(t)[:12])
                tos.append((self.PROPS[int(t.cls[4:]) - 140], off, mx, sc))
            out.append((self.PROPS[int(x.cls[6:]) - 134], x.f['p0'][2], tos))
        return out

    # ------------------------------------------------------------ 스킨·첨부

    def string_table(self, sk):
        """바이너리 문자열 표: 프로젝트의 첨부 목록(이름순, 자리표시자 포함)을 그대로 돌며 이름과 경로를 넣는다."""
        out = []
        for a in self.lst(sk, 6):
            out.append(self.att_name(a))
            path = self.p.get(a, {'RegionAtt': 13, 'MeshAtt': 14}.get(a.cls, -1), kind='str')
            if path:
                out.append(path)
        return out

    def att_key(self, a):
        ph = self.p.get(a, 4, kind='Placeholder')
        if ph is not None:
            return self.p.get(ph, 1, kind='str')
        return self.att_name(a)

    def att_name(self, a):
        n = self.p.get(a, 1, kind='str')
        if n is None:       # 정점 첨부: 이름이 다른 단계에 있다(d1 등)
            for k, v in a.f.items():
                if k.lstrip('td').split(':')[-1] == '1' and isinstance(v[2], str):
                    return v[2]
        return n

    def exported(self, a):
        """첨부의 Export 체크(태그 5). 꺼져 있으면 첨부와 그 키가 내보내기에서 빠진다."""
        return a.cls == 'Placeholder' or self.i(a, 5, 1) != 0

    def att_slot(self, a):
        return self.p.get(a, 0, kind='Slot')

    def skins(self, sk):
        def in_default(a):      # 6번 목록에는 모든 스킨의 첨부와 자리표시자가 함께 있다
            return a.cls != 'Placeholder' and self.p.get(a, 4, kind='Placeholder') is None
        out = [{'name': 'default', 'bones': [], 'constraints': [],
                'attachments': self.skin_atts([a for a in self.lst(sk, 6) if in_default(a)])}]
        def order(n):       # 폴더 밖 항목이 먼저, 그다음 폴더별(폴더 안에서 다시 같은 규칙)
            parts = self.skin_name(n).split('/')
            return [(0 if i == len(parts) - 1 else 1, p.lower()) for i, p in enumerate(parts)]
        for s in sorted(self.skin_nodes, key=order):
            out.append({
                'name': self.skin_name(s),
                'bones': [self.name(b) for b in self.lst(s, 6)],
                'constraints': [self.name(c) for c in self.lst(s, 7)],
                'color': rgba(self.leaf(s, 8)),
                'attachments': self.skin_atts(self.lst(s, 2)),
            })
        return out

    def skin_atts(self, atts):
        rows = []
        for a in atts:
            if a.cls == 'Placeholder' or not self.exported(a):      # 자리표시자, Export를 끈 첨부
                continue
            slot = self.att_slot(a)
            rows.append((self.slot_index.get(id(slot), 1 << 30), self.att_key(a), self.attachment(a)))
        rows.sort(key=lambda r: (r[0], r[1]))
        return [(self.name(self.slots[si]), key, att) for si, key, att in rows]

    def vertex_data(self, a):
        """정점 첨부 공통: 런타임 형식 정점 배열, 가중치 여부, 조밀한(편집기) 배열 → 런타임 순서 변환표."""
        p = self.p
        bones = [b for b in self.lst(a, 1)]
        vws = [w for w in self.lst(a, 2)]
        weighted = self.i(a, 30) != 0
        verts, dense_map, setup = [], [], []
        if not weighted:
            for vi, w in enumerate(vws):
                pos = self.leaf(w, 1)
                verts += pos[:2]
                setup += pos[:2]
                dense_map += [vi * 2, vi * 2 + 1]
            return verts, False, dense_map, setup
        nb = len(bones)
        for vi, w in enumerate(vws):
            weights = self.leaf(w, 0)
            pos = self.leaf(w, 1)
            used = [bi for bi in range(nb) if weights[bi] != 0]
            verts.append(len(used))
            for bi in used:
                verts += [self.bone_index[id(bones[bi])], pos[bi * 2], pos[bi * 2 + 1], weights[bi]]
                setup += [pos[bi * 2], pos[bi * 2 + 1]]
                dense_map += [(vi * nb + bi) * 2, (vi * nb + bi) * 2 + 1]
        return verts, True, dense_map, setup

    def sequence(self, q):
        if q is None:
            return None
        return {'count': zigzag_dec(self.i(q, 2)), 'start': zigzag_dec(self.i(q, 1)),
                'digits': zigzag_dec(self.i(q, 3)), 'setup': zigzag_dec(self.i(q, 4))}

    def attachment(self, a):
        p = self.p
        name = self.att_name(a)
        key = self.att_key(a)
        base = {'name': name, 'key': key}
        if a.cls == 'RegionAtt':
            base.update(type='region', path=p.get(a, 13, kind='str') or '',
                        x=self.f(a, 6), y=self.f(a, 7), scaleX=self.f(a, 8, 1.0), scaleY=self.f(a, 9, 1.0),
                        rotation=self.f(a, 10), width=self.f(a, 11), height=self.f(a, 12),
                        color=rgba(self.leaf(a, 14, (255, 255, 255, 255))))
            base['sequence'] = self.sequence(self.p.get(a, 15, kind='Sequence'))
            return base
        if a.cls in ('MeshAtt', 'BBoxAtt', 'ClippingAtt', 'PathAtt'):
            verts, weighted, dense, setup = self.vertex_data(a)
            base.update(vertices=verts, weighted=weighted, dense_map=dense, setup=setup,
                        vertexCount=len(self.lst(a, 2)), color=rgba(self.leaf(a, 31, (255, 255, 255, 255))))
            if a.cls == 'BBoxAtt':
                base['type'] = 'boundingbox'
            elif a.cls == 'PathAtt':
                base.update(type='path', closed=self.i(a, 12) != 0, constantSpeed=self.i(a, 13, 1) != 0,
                            lengths=self.leaf(a, 15) or [])
            elif a.cls == 'ClippingAtt':
                base['type'] = 'clipping'
                base['end'] = self.name(self.p.get(a, 12, kind='Slot'))
                base['convex'] = self.p.format.startswith('4.3') and self.i(a, 13) != 0   # 4.3 새 필드
            else:
                parent = self.p.get(a, 26, kind='MeshAtt')
                if parent is not None:
                    owner = self.p.get(parent, 32, kind='Skin')
                    base.update(type='linkedmesh', path=p.get(a, 14, kind='str') or '', parent=self.att_key(parent),
                                parentSlot=self.name(self.att_slot(parent)),
                                skin=self.skin_name(owner) if owner is not None else None,
                                timelines=self.i(a, 27, 1) != 0, width=self.f(a, 16), height=self.f(a, 17))
                    return base
                tri = self.leaf(a, 13) or []
                base['sequence'] = self.sequence(self.p.get(a, 36, kind='Sequence'))
                base.update(type='mesh', path=p.get(a, 14, kind='str') or '', uvs=self.leaf(a, 12) or [],
                            triangles=tri, edges=self.leaf(a, 8) or [], hull=hull_count(tri),
                            width=self.f(a, 16), height=self.f(a, 17))
            return base
        raise ModelError(f'아직 지원하지 않는 첨부: {a.cls}')

    # ------------------------------------------------------------ 이벤트

    def event_data(self, e):
        return {
            'name': self.p.get(e, 2, kind='str'),
            'int': zigzag_dec(self.i(e, 3)), 'float': self.f(e, 4),
            'string': self.p.get(e, 5, kind='str') or '', 'audio': self.p.get(e, 6, kind='str') or '',
            'volume': self.f(e, 8, 1.0), 'balance': self.f(e, 9),
        }

    # ------------------------------------------------------------ 애니메이션

    def animation(self, an):
        timelines = []
        for g in self.lst(an, 2):
            target = self.node(g, 1)
            for tl in self.lst(g, 2):
                t = self.timeline(target, tl)
                if t is not None:
                    timelines.append(t)
        return {'name': self.full_name(an, 7), 'timelines': timelines, 'noClean': self.i(an, 9) != 0, 'color': rgba(self.leaf(an, 10, (255, 255, 255, 255)))}

    def tl_type(self, tl):
        if 'type' in tl.f:
            return tl.f['type'][2]
        return self.leaf(tl, 9)

    def tl_keys(self, tl):
        if 'keys' in tl.f:
            return self.p.items(self.p.deref(tl.f['keys'][2]))
        return self.lst(tl, 2)

    def tl_extra(self, tl, ty):
        """새 방식: extra<종류>, 옛 방식: 3 슬롯, 4 이벤트 정의, 5 첨부, 6 IK, 7 변형."""
        if 'type' in tl.f:
            v = tl.f.get(f'extra{ty}')
            return self.p.deref(v[2]) if v and v[2] is not None else None
        tag = {4: 3, 5: 3, 6: 5, 7: 6, 8: 7, 9: 8, 10: 8, 11: 8, 12: 4}.get(ty)
        return self.p.get(tl, tag, kind='node') if tag is not None else None

    def timeline(self, target, tl):
        ty = self.tl_type(tl)
        if ty not in TIMELINES:
            raise ModelError(f'아직 지원하지 않는 타임라인 종류 {ty}')
        kind, channels = TIMELINES[ty]
        extra = self.tl_extra(tl, ty)
        t = {'type': kind, 'code': ty, 'keys': []}
        if kind in ('rotate', 'translate', 'scale', 'shear'):
            t['bone'] = self.name(target)
        elif kind in ('color', 'attachment'):
            t['slot'] = self.name(extra)
        elif kind == 'deform':
            att = extra
            if not self.exported(att):
                return None
            t['slot'] = self.name(self.att_slot(att)) if 'type' in tl.f else self.name(self.p.get(tl, 3, kind='Slot'))
            t['attachment'] = self.att_key(att)
            skin = self.p.get(att, 32, kind='Skin')
            t['skin'] = self.skin_name(skin) if skin is not None else 'default'
        elif kind in ('ik', 'transform', 'pathPosition', 'pathSpacing', 'pathMix', 'physicsWind', 'sliderMix'):
            t['constraint'] = self.name(extra)
        elif kind in ('translatex', 'translatey'):
            t['bone'] = self.name(target)
        elif kind == 'sequence':
            att = self.p.deref(tl.f['extraB22'][2])
            if not self.exported(att):
                return None
            t['slot'] = self.name(extra)
            t['attachment'] = self.att_key(att)
            skin = self.p.get(att, 32, kind='Skin')
            t['skin'] = self.skin_name(skin) if skin is not None else 'default'
        keys = self.tl_keys(tl)
        n = len(TIMELINES[ty][1])
        nc = CURVE_CHANNELS.get(ty, 0)
        for k in keys:
            t['keys'].append(self.key(k, ty, n, nc, t))
        return t

    def key(self, k, ty, n, nc, t):
        p = self.p
        if 'time' in k.f:                                   # 4.2.22~: 고정 배치
            frame = k.f['time'][2]
            flag = k.f['flag'][2]
            curve = {0: 'linear', 1: 'bezier', 2: 'stepped'}.get(flag, 'linear')
            vals_at = k.f['time'][1]
            vals = list(_floats(p.b, vals_at, n))
            if self.doc.curve_cond:
                # 4.3.04~: 손잡이 블록은 플래그 뒤에, 이 키(나 앞 키)가 베지어일 때만 있다
                after = k.f['flag'][1]
                payload_start = min((v[0] for kk, v in k.f.items() if kk.startswith('payload')), default=k.end)
                handles = list(_floats(p.b, after, nc * 5)) if nc and payload_start - after >= 20 * nc else []
            else:
                handles = list(_floats(p.b, vals_at + 4 * n, nc * 5)) if nc else []
            payload = None
            for kk, v in k.f.items():
                if kk.startswith('payload'):
                    payload = p.deref(v[2])
        else:                                               # ~4.2.20: 태그 필드
            frame = self.f(k, 2)
            curve = {1: 'linear', 2: 'bezier', 3: 'stepped'}.get(self.leaf(k, 5, 1), 'linear')
            vals = self.leaf(k, 3) or []
            handles = self.leaf(k, 4) or []
            payload = p.get(k, {5: 6, 6: 9, 12: 7, 13: 8}.get(ty, -1), kind='node')
        key = {'frame': frame, 'curve': curve, 'values': vals,
               'handles': [handles[i * 5:i * 5 + 4] for i in range(len(handles) // 5)]}
        if ty == 5:
            key['name'] = self.att_key(payload) if payload is not None and self.exported(payload) else None
        elif ty == 6:
            dense = p.leaf(payload) if payload is not None else []
            key['dense'] = dense
        elif ty == 12:
            ev = payload
            data = p.get(ev, 1, kind='EventData')
            key['event'] = {
                'name': p.get(data, 2, kind='str'),
                'int': zigzag_dec(self.i(ev, 2)), 'float': self.f(ev, 3),
                'string': p.get(ev, 4, kind='str'), 'volume': self.f(ev, 5, 1.0), 'balance': self.f(ev, 6),
            }
        elif ty == 13:
            key['offsets'] = [(self.name(p.get(e, 0, kind='Slot')), zigzag_dec(self.i(e, 1))) for e in p.items(payload)]
        return key


def _floats(b, at, n):
    import struct
    return struct.unpack(f'>{n}f', b[at:at + 4 * n]) if n else ()


def hull_count(tri):
    """경계 변(삼각형 하나에만 속한 변)의 수 = 테두리 정점 수."""
    cnt = {}
    for i in range(0, len(tri) - 2, 3):
        a, b, c = tri[i:i + 3]
        for e in ((a, b), (b, c), (c, a)):
            k = (min(e), max(e))
            cnt[k] = cnt.get(k, 0) + 1
    return sum(1 for v in cnt.values() if v == 1)


def read_models(project):
    """프로젝트의 스켈레톤마다 모델 하나."""
    skels = project.of('Skeleton')
    if not skels:
        raise ModelError('스켈레톤이 없습니다')
    return [Reader(project).read(sk) for sk in skels]


def read_model(project):
    models = read_models(project)
    if len(models) > 1:
        raise ModelError(f'스켈레톤이 {len(models)}개입니다(read_models를 쓰세요)')
    return models[0]
