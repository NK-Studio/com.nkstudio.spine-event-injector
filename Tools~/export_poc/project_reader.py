"""`.spine` 프로젝트 → 읽기 쉬운 객체 그래프.

파일을 읽는 일(컨테이너, 포맷별 스키마 고르기, Kryo 그래프 해석)은 이벤트 주입 PoC의 해석기
(`poc/spine_event_poc.py`)를 그대로 쓴다. 여기서는 그 결과 위에
- 참조를 따라가 실제 객체를 돌려주고,
- 버전마다 다르게 적히는 필드 키(`12`, `t12`, `d12`, `1:12`…)를 태그 번호 하나로 찾고,
- 해석기가 건너뛰는 값(배열·색·Vec3·열거형·키프레임 값과 곡선)을 바이트에서 꺼낸다.
"""
import glob
import json
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), 'poc'))

import spine_event_poc as P  # noqa: E402

Node, Ref, FormatError = P.Node, P.Ref, P.FormatError


def _key_tag(key):
    """필드 키 → 태그 번호(키프레임의 'time' 같은 이름 키는 None)."""
    k = key.lstrip('td')
    if ':' in k:
        k = k.split(':', 1)[1]
    return int(k) if k.isdigit() else None


class Project:
    def __init__(self, path):
        self.partial = None
        try:
            loaded = P.load(path)
            self.doc = loaded[2] if isinstance(loaded, tuple) else loaded
        except FormatError as e:
            self.doc = self.load_partial(path, e)
        self.b = self.doc.b
        self.format = self.doc.version
        self._tags = {}

    @staticmethod
    def schemas_for(version):
        for fn in sorted(glob.glob(os.path.join(os.path.dirname(HERE), 'poc', 'schema', '*.json'))):
            s = json.load(open(fn))
            if version in s.get('formats', [os.path.basename(fn)[:-5]]):
                yield s

    def load_partial(self, path, error):
        """내보내기에는 스켈레톤만 있으면 된다. 뒤쪽 편집기 상태(내보내기 설정 등)에서 막혀도
        프로젝트의 스켈레톤 목록을 끝까지 읽었다면 그 결과를 쓴다."""
        body = P.read_body(path)
        head = P.Document.__new__(P.Document)
        head.b, head.p = body, 0
        for _ in range(3):
            P.Document.varint(head)
        version = P.Document.kstring(head)
        for schema in self.schemas_for(version):
            doc = P.Document.__new__(P.Document)
            try:
                P.Document.__init__(doc, body, schema)
                return doc
            except FormatError as e:
                root = doc.stack[1] if len(doc.stack) > 1 else None
                if root is not None and root.cls == 'Project' and '0' in root.f:
                    self.partial = str(e)
                    return doc
        raise error

    @property
    def root(self):
        found = self.doc.of('Project')
        return found[0] if found else self.doc.stack[1]

    # ------------------------------------------------------------ 값 꺼내기

    def deref(self, v):
        if isinstance(v, Ref):
            return self.doc.byid[tuple(v)]
        return v

    def tags(self, node):
        """태그 번호 → [필드 키…] (같은 태그가 여러 단계에 있을 수 있다)."""
        t = self._tags.get(id(node))
        if t is None:
            t = {}
            for k in node.f:
                n = _key_tag(k)
                if n is not None:
                    t.setdefault(n, []).append(k)
            self._tags[id(node)] = t
        return t

    def raw(self, node, key):
        return node.f[key][2]

    def get(self, node, tag, default=None, kind=None):
        """태그 번호로 값. kind: 'str' 'num' 'node' 또는 클래스 이름 — 같은 태그가 여럿이면 이것으로 고른다."""
        if node is None:
            return default
        keys = self.tags(node).get(tag, [])
        for k in keys:
            v = self.deref(node.f[k][2])
            if kind is None or self._is(v, kind):
                return v
        return default

    @staticmethod
    def _is(v, kind):
        if kind == 'str':
            return isinstance(v, str)
        if kind == 'num':
            return isinstance(v, (int, float)) and not isinstance(v, bool)
        if kind == 'node':
            return isinstance(v, Node)
        return isinstance(v, Node) and v.cls == kind

    def items(self, node):
        """리스트·맵 노드의 원소(참조를 따라간 것)."""
        if node is None:
            return []
        return [self.deref(v) for _, _, v in node.items]

    def body(self, node):
        """새 객체 본문 바이트(표지 뒤부터). 슬롯이 없는 잎 객체에만 쓴다."""
        if not node.parts:
            return b''
        start = node.parts[0][1]
        return self.b[start:node.end]

    def leaf(self, node):
        """배열·색·Vec3·열거형·문자열 객체의 값."""
        if node is None:
            return None
        kind = self.doc.kinds.get(node.cls)
        if kind == 'varint':
            return node.f['v'][2]
        if kind == 'string':
            return node.f['s'][2]
        data = self.body(node)
        if kind == 'fixed4':
            return tuple(data[:4])
        if kind == 'fixed12':
            return struct.unpack('>3f', data[:12])
        if kind in ('rfarray', 'farray', 'rsarray', 'rvarray'):
            n, p = _varint(data, 0)
            if kind == 'farray':
                if n == 0:
                    return None
                n -= 1
            if kind in ('rfarray', 'farray'):
                return list(struct.unpack(f'>{n}f', data[p:p + 4 * n]))
            if kind == 'rsarray':
                return list(struct.unpack(f'>{n}H', data[p:p + 2 * n]))
            out = []
            for _ in range(n):
                v, p = _varint(data, p)
                out.append(v)
            return out
        if kind == 'boxfarray':
            n, p = _varint(data, 0)
            out = []
            for _ in range(max(n - 1, 0)):
                m, p = _varint(data, p)
                if m:
                    out.append(struct.unpack('>f', data[p:p + 4])[0])
                    p += 4
                else:
                    out.append(None)
            return out
        if kind in ('defvtx', 'defvtx0'):
            total, p = _varint(data, 0)
            chunks, p = _varint(data, p)
            out = [0.0] * total
            for i in range(chunks):
                head = data[p]
                p += 1
                if head or kind == 'defvtx':
                    n = min(128, total - 128 * i)
                    out[128 * i:128 * i + n] = struct.unpack(f'>{n}f', data[p:p + 4 * n])
                    p += 4 * n
            return out
        raise FormatError(f'{node.cls}({kind})는 잎 값이 아닙니다')

    def value(self, node, tag, default=None, kind=None):
        """get + 잎 값 풀기."""
        v = self.get(node, tag, None, kind)
        if v is None:
            return default
        if isinstance(v, Node) and self.doc.kinds.get(v.cls) not in ('tagged', 'list', 'map', 'timeline', 'keyframe', 'plain'):
            return self.leaf(v)
        return v

    def of(self, cls):
        return self.doc.of(cls)


def _varint(b, p):
    r = sh = 0
    while True:
        x = b[p]
        p += 1
        r |= (x & 0x7F) << sh
        sh += 7
        if not x & 0x80:
            return r, p
