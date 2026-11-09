#!/usr/bin/env python3
"""Spine 프로젝트 파일(.spine)에 애니메이션 이벤트 키·이벤트 정의를 직접 심는 PoC.

지원 포맷: 본문 머리의 프로젝트 포맷 버전이 poc/schema/<버전>.json 으로 있는 것
  4.0.07 = Spine 4.2.22로 저장한 spineboy-pro.spine
  4.3.00 = Spine 4.3.75-beta로 저장한 spineboy-pro.spine
표준 라이브러리만 쓴다.

  python3 poc/spine_event_poc.py IN.spine --list
  python3 poc/spine_event_poc.py IN.spine OUT.spine --anim run --frame 12
  python3 poc/spine_event_poc.py IN.spine OUT.spine --anim idle --frame 5 --event hit --new-event --oracle
  python3 poc/spine_event_poc.py IN.spine OUT.spine --anim run --frame 12 \\
      --event footstep --int 7 --float 1.5 --string hello --oracle

동작:
  1. raw inflate로 본문을 얻는다.
  2. 스키마(클래스 번호 → 종류, (클래스, 태그) → 값 타입, 참조 표)로 본문 전체를 읽어 객체 그래프로 만든다.
     객체 본문 = «원래 바이트 조각»과 «슬롯(다른 객체를 가리키는 자리)»의 나열. 슬롯은 처음 나온 곳에서는
     객체 본문을 품고(새 객체), 그 뒤로는 번호로 가리킨다(역참조) — Kryo 참조 규칙.
  3. 그래프를 고친다: 키 객체 추가, 이벤트 타임라인(그룹·타임라인·키 리스트) 추가, 이벤트 정의 추가.
  4. Kryo 규칙대로 다시 쓴다. 어떤 객체든 «처음 만나는 슬롯»에 본문이 들어가고 번호도 그 순서로 매겨지므로,
     참조 번호 재계산과 «정의가 처음 쓰이는 위치»(4.3) 문제가 저절로 풀린다.
     아무것도 고치지 않으면 원본과 바이트가 같아야 한다(매번 확인).
  5. 다시 읽어 검증한 뒤 level 6 raw deflate로 새 파일에 쓴다(원본은 건드리지 않는다).
"""
import argparse
import glob
import json
import os
import struct
import subprocess
import sys
import tempfile
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
SPINE = os.environ.get('SPINE_BIN', '/Applications/Spine.app/Contents/MacOS/Spine')


class FormatError(Exception):
    pass


class UsageError(Exception):
    pass


# ---------------------------------------------------------------- 컨테이너

TRAILER_LEN = 20


def read_container(path):
    """(본문, 꼬리) — 4.3.26부터 deflate 뒤에 20바이트 꼬리가 붙는다:
    00 00 00 00 | 압축 길이(BE 4) | 8바이트(로드 때 검사 안 함) | 02 0b 02 0b"""
    raw = open(path, 'rb').read()
    d = zlib.decompressobj(-15)
    body = d.decompress(raw) + d.flush()
    if not d.eof:
        raise FormatError('raw deflate 스트림이 끝나지 않았습니다')
    tail = d.unused_data
    if not tail:
        return body, None
    clen = len(raw) - len(tail)
    if (len(tail) == TRAILER_LEN and tail[:4] == b'\0\0\0\0'
            and int.from_bytes(tail[4:8], 'big') == clen):
        return body, tail
    raise FormatError(f'deflate 뒤에 모르는 데이터 {len(tail)}바이트: {tail[:24].hex()}')


def read_body(path):
    return read_container(path)[0]


def write_body(path, body, like=None):
    """like: 꼬리 형식을 따를 원본 경로(꼬리가 있으면 압축 길이만 고쳐 붙인다)"""
    c = zlib.compressobj(6, zlib.DEFLATED, -15)
    data = c.compress(body) + c.flush()
    tail = read_container(like)[1] if like else None
    if tail:
        data += tail[:4] + len(data).to_bytes(4, 'big') + tail[8:]
    tmp = path + '.tmp'
    with open(tmp, 'wb') as f:
        f.write(data)
    os.replace(tmp, path)


# ---------------------------------------------------------------- 기본 인코딩

def varint(v):
    out = bytearray()
    while True:
        x = v & 0x7F
        v >>= 7
        if not v:
            out.append(x)
            return bytes(out)
        out.append(x | 0x80)


def zigzag(i):
    return ((i << 1) ^ (i >> 31)) & 0xFFFFFFFF


def unzigzag(v):
    return (v >> 1) ^ -(v & 1)


def kryo_string(s):
    """Kryo writeString: 2~32자 ASCII는 마지막 바이트 |0x80, 그 외는 «플래그 varint 길이+1» + UTF-8."""
    if 1 < len(s) <= 32 and all(0x20 <= ord(c) < 0x80 for c in s):
        bs = s.encode('ascii')
        return bs[:-1] + bytes([bs[-1] | 0x80])
    n = len(s) + 1
    out = bytearray([0x80 | (n & 0x3F) | (0x40 if n >> 6 else 0)])
    if n >> 6:
        out += varint(n >> 6)
    return bytes(out) + s.encode('utf-8')


# ---------------------------------------------------------------- 객체 그래프

class Slot:
    """다른 객체를 담는 자리. prefix: 클래스 번호(필드 선언이 구체 클래스면 None — 표지부터 쓴다)."""
    __slots__ = ('prefix', 'obj')

    def __init__(self, prefix, obj):
        self.prefix, self.obj = prefix, obj


class Node:
    """객체 하나.
    parts: 본문 = [('raw', 원래 시작, 원래 끝, 바이트) | ('slot', 원래 시작, 원래 끝, Slot)].
           원래 위치는 고칠 자리를 찾는 데만 쓴다(복제본은 원본의 위치를 물려받는다).
    f: 필드 → (값 시작, 값 끝, 값). items: 리스트 원소 (시작, 끝, 값). size_at: 리스트 크기 varint 위치."""
    __slots__ = ('cls', 'id', 'start', 'end', 'f', 'items', 'size_at', 'parts', 'last', 'tb')

    def __init__(self, cls, oid, start, tb=None):
        self.cls, self.id, self.start, self.end, self.tb = cls, oid, start, start, tb
        self.f, self.items, self.size_at, self.parts, self.last = {}, [], None, [], start

    def __repr__(self):
        return f'<{self.cls} {self.id}>'


class Ref(tuple):
    """역참조: (표, 대상 번호)"""


class Document:
    def __init__(self, body, schema):
        self.b, self.p = body, 0
        self.names, self.kinds = schema['names'], schema['kinds']
        self.fields, self.slots, self.tail = schema['fields'], schema['slots'], schema['tail']
        self.tables, self.plain = schema.get('tables', {}), schema.get('plain', {})
        self.timelines, self.ascending = schema.get('timelines', {}), schema.get('ascending', False)
        self.anyA = schema.get('anyA', {})
        # 묶음 방식(4.3 정식 첨부): 필드가 묶음 순서대로 나오고 묶음 안 순서는 빌드마다 다르다.
        # 필드 수 값은 실제보다 작게 적히므로 쓰지 않고, 묶음마다 모든 필드를 읽으면 다음 묶음으로 간다.
        # 키: 0번 묶음은 태그, g번 묶음은 'g:태그'.
        self.groups = schema.get('groups', {})
        self.tl_refs = schema.get('tl_refs', True)     # 4.3 초기: 타임라인·키가 그룹·타임라인을 역참조한다
        # 4.3.04~: 플래그 뒤에 조건부 핸들 블록. 'own'(4.3.04): 이 키가 베지어일 때만, True(4.3.05~): 앞 키가 베지어여도
        self.curve_cond = schema.get('curve_cond', False)
        self.kl_prev = 0
        self.key_event_field = schema.get('key_event_field', '7')
        self.cnum = {}                      # 클래스 이름 → 번호
        for num, name in sorted(self.names.items(), key=lambda kv: int(kv[0])):
            self.cnum.setdefault(name, int(num))
        self.objs = {'A': [], 'B': []}      # 표 → 객체 번호 → 클래스 이름
        self.byid = {}                      # (표, 번호) → Node
        self.refs = []                      # (위치, 바이트 수, 표, 대상 번호)
        self.news = []                      # (표지 위치, 표) — 새 객체, 번호 순
        self.nodes = []
        self.tltype = None
        self.top = Node('<top>', None, 0)
        self.stack = [self.top]
        for _ in range(3):
            self.varint()
        self.version = self.kstring()
        if schema.get('root_varint', True):
            self.varint()   # 루트 앞 값(4.x: 스켈레톤 수). 3.8에는 없다
        if schema.get('root_pad'):
            self.take(schema['root_pad'])   # 4.3.26: 16바이트 값(프로젝트 ID로 보임)
        self.typed_slot('Project')      # 루트는 클래스 없이 표지부터 쓰는 Project
        tail_at = self.p    # 뒤쪽 에디터 상태 구간: 첫 값 1은 새 객체 표지라 참조 번호를 하나 쓴다
        if self.varint() == 1:
            tail = self.new_node('EditorTail', tail_at)
            tail.last = self.p
            self.stack.append(tail)
            self.read_tail()
            self.flush(tail)
            self.stack.pop()
            tail.end = self.p
            self.nodes.append(tail)
            self.add_slot(tail_at, None, tail)
        else:
            self.read_tail()
        self.flush(self.top)
        self.top.end = self.p

    def read_tail(self):
        self.varint()
        while self.p < len(self.b):
            self.obj_slot()

    def fail(self, msg, at=None):
        raise FormatError(f'@{self.p if at is None else at:#x}: {msg}')

    def take(self, n):
        if self.p + n > len(self.b):
            self.fail('본문이 끝났습니다')
        v = self.b[self.p:self.p + n]
        self.p += n
        return v

    def varint(self):
        r = sh = 0
        while True:
            x = self.take(1)[0]
            r |= (x & 0x7F) << sh
            sh += 7
            if not x & 0x80:
                return r

    def kstring(self):
        b0 = self.take(1)[0]
        if not b0 & 0x80:
            out = bytearray([b0])
            while True:
                x = self.take(1)[0]
                out.append(x & 0x7F)
                if x & 0x80:
                    return out.decode('ascii')
        n, sh = b0 & 0x3F, 6
        if b0 & 0x40:
            while True:
                x = self.take(1)[0]
                n |= (x & 0x7F) << sh
                sh += 7
                if not x & 0x80:
                    break
        if n == 0:
            return None
        chars = []
        for _ in range(n - 1):
            c = self.take(1)[0]
            if c >= 0xE0:
                c = ((c & 0x0F) << 12) | ((self.take(1)[0] & 0x3F) << 6) | (self.take(1)[0] & 0x3F)
            elif c >= 0xC0:
                c = ((c & 0x1F) << 6) | (self.take(1)[0] & 0x3F)
            chars.append(chr(c))
        return ''.join(chars)

    # ------------------------------------------------------------ 그래프 기록

    def flush(self, node):
        if self.p > node.last:
            node.parts.append(('raw', node.last, self.p, self.b[node.last:self.p]))
        node.last = self.p

    def add_slot(self, s0, prefix, obj):
        parent = self.stack[-1]
        if s0 > parent.last:
            parent.parts.append(('raw', parent.last, s0, self.b[parent.last:s0]))
        parent.parts.append(('slot', s0, self.p, Slot(prefix, obj)))
        parent.last = self.p

    def table(self, cls):
        return 'A' if cls == '*A' else self.tables.get(cls, 'B')

    def new_node(self, cls, start):
        """방금 읽은 표지(1)에 해당하는 새 객체. 참조를 추적하지 않는 클래스(none·flag)는 번호가 없다."""
        tb = self.table(cls)
        oid = None
        if tb not in ('none', 'flag'):
            self.objs[tb].append(cls)
            self.news.append((self.p - 1, tb))
            oid = (tb, len(self.objs[tb]) - 1)
        node = Node(cls, oid, start, tb)
        if oid:
            self.byid[oid] = node
        return node

    def marker(self, cls):
        """참조 표지: 0 = null, 1 = 새 객체, n = 이미 읽은 n-2번 객체(클래스가 속한 표에서)."""
        at = self.p
        r = self.varint()
        if r < 2:
            return r
        tb, tid = self.table(cls), r - 2
        if tb in ('none', 'flag') or tid >= len(self.objs[tb]) or cls != '*A' and self.objs[tb][tid] != cls:
            self.fail(f'역참조 {tb}{tid}가 {cls}가 아닙니다', at)
        self.refs.append((at, self.p - at, tb, tid))
        return Ref((tb, tid))

    def string_slot(self):
        s0 = self.p
        m = self.marker('String')
        if m == 1:
            node = self.new_node('String', s0)
            node.last = self.p
            self.stack.append(node)
            node.f['s'] = (self.p, None, self.kstring())
            self.flush(node)
            self.stack.pop()
            node.end = self.p
            self.nodes.append(node)
            self.add_slot(s0, None, node)
            return node.f['s'][2]
        if isinstance(m, Ref):
            node = self.byid[tuple(m)]
            self.add_slot(s0, None, node)
            return node.f['s'][2]
        return None

    def obj_slot(self):
        at = self.p
        c = self.varint()
        if c == 0:
            return None
        cls = self.names.get(str(c))
        if cls is None:
            self.fail(f'모르는 클래스 번호 {c}', at)
        if self.table(cls) == 'none':          # 참조 추적 안 하는 클래스: 표지 없이 본문
            node = self.body(cls, at)
            self.add_slot(at, c, node)
            return node
        m = self.marker(cls)
        if m == 1:
            node = self.body(cls, at)
            self.add_slot(at, c, node)
            return node
        if not isinstance(m, Ref):
            self.fail(f'클래스 {cls} 뒤 표지가 0입니다', at)
        self.add_slot(at, c, self.byid[tuple(m)])
        return m

    def typed_slot(self, cls):
        at = self.p
        m = self.marker(cls)
        if m == 1:
            if cls == '*A':     # 대상 클래스가 바이트에 없다: «필드 수:첫 태그»로 판별(스키마 anyA)
                save = self.p
                key = f'{self.varint()}:{self.varint()}'
                self.p = save
                cls = self.anyA.get(key)
                if cls is None:     # 필드 순서는 표본마다 달라진다: 필드 수만으로 하나로 정해지면 그것
                    same = {c for k, c in self.anyA.items() if k.split(':')[0] == key.split(':')[0]}
                    cls = same.pop() if len(same) == 1 else None
                if cls is None:
                    self.fail(f'*A 슬롯의 새 객체를 판별할 수 없습니다(필드 수:첫 태그 = {key})', at)
            node = self.body(cls, at)
            self.add_slot(at, None, node)
            return node
        if isinstance(m, Ref):
            self.add_slot(at, None, self.byid[tuple(m)])
        return m or None

    def body(self, cls, at):
        kind = self.kinds[cls]
        node = self.new_node(cls, at)
        node.last = self.p
        self.stack.append(node)
        if kind == 'tagged' and cls in self.groups:
            self.varint()       # 적힌 필드 수(판정에 쓰지 않음)
            need = [set(g) for g in self.groups[cls]]
            gi = 0
            for _ in range(sum(len(g) for g in need)):
                while not need[gi]:
                    gi += 1
                tat = self.p
                tag = self.varint()
                key = str(tag) if gi == 0 else f'{gi}:{tag}'
                if tag not in need[gi]:
                    self.group_need = (cls, gi, sorted(need[gi]))
                    self.fail(f'모르는 필드 {cls}.{key}', tat)
                need[gi].discard(tag)
                ty = self.fields.get(cls, {}).get(key)
                if ty is None:
                    self.fail(f'모르는 필드 {cls}.{key}', tat)
                vat = self.p
                val = self.value(cls, key, ty)
                node.f[key] = (vat, self.p, val)
        elif kind == 'tagged':
            n = self.varint() + self.tail.get(cls, 0)
            n0 = n - self.tail.get(cls, 0)
            last, level = -1, 0
            for i in range(n):
                tat = self.p
                tag = self.varint()
                if self.ascending:      # 4.3: 클래스 단계마다 태그 오름차순, 내려가면 상위 단계
                    if tag <= last:
                        level += 1
                    last = tag
                    key = str(tag) if level == 0 else f'{level}:{tag}'
                else:                   # 4.2: 개수 뒤 꼬리 필드는 t<태그>, 같은 태그가 다시 나오면 d<태그>
                    key = str(tag) if i < n0 else f't{tag}'
                    while key in node.f:
                        key = 'd' + key
                ty = self.fields.get(cls, {}).get(key)
                if ty is None:
                    self.fail(f'모르는 필드 {cls}.{key}', tat)
                vat = self.p
                val = self.value(cls, key, ty)
                node.f[key] = (vat, self.p, val)
        elif kind == 'timeline':            # 4.3 타임라인: 그룹, 종류, 키 리스트, 종류별 추가 객체
            if self.tl_refs:
                self.field(node, 'group', 'T')
            node.f['type'] = (self.p, None, self.varint())
            lay = self.timelines.get(str(node.f['type'][2]))
            if lay is None:
                self.fail(f'모르는 타임라인 종류 {node.f["type"][2]}')
            saved, self.tltype = self.tltype, node.f['type'][2]
            self.field(node, 'keys', 'T')
            self.tltype = saved
            if lay.get('extra'):
                self.field(node, f'extra{node.f["type"][2]}', lay['extra'])
            if lay.get('extra2'):       # 시퀀스 타임라인: 슬롯 + 첨부
                self.field(node, f'extraB{node.f["type"][2]}', lay['extra2'])
        elif kind == 'keyframe':            # 4.3 키: 그룹, 타임라인, 시간, 값, 곡선, 플래그, 종류별 객체
            lay = self.timelines.get(str(self.tltype))
            if lay is None:
                self.fail('타임라인 밖의 키')
            if self.tl_refs:
                self.field(node, 'group', 'T')
                self.field(node, 'timeline', 'T')
            self.field(node, 'time', 'F')
            if self.curve_cond:     # 핸들 블록(들어오는·나가는 핸들): 이 키나 앞 키가 베지어(1)일 때만
                self.take(lay['vals'])
                self.field(node, 'flag', 'V')
                if node.f['flag'][2] == 1 or self.kl_prev == 1 and self.curve_cond != 'own':
                    self.take(20 * lay['curves'])
                self.kl_prev = node.f['flag'][2]
            else:
                self.take(lay['vals'] + 20 * lay['curves'])
                self.field(node, 'flag', 'V')
            if lay.get('payload'):
                self.field(node, f'payload{self.tltype}', lay['payload'])
        elif kind == 'plain':
            for i, ty in enumerate(self.plain[cls]):
                self.field(node, f'p{i}', ty)
        elif kind in ('list', 'map'):
            node.size_at = self.p
            n = self.varint()
            saved, self.kl_prev = self.kl_prev, 0
            for _ in range(n * (2 if kind == 'map' else 1)):
                s = self.p
                v = self.obj_slot()
                node.items.append((s, self.p, v))
            self.kl_prev = saved
        elif kind == 'string':
            node.f['s'] = (self.p, None, self.kstring())
        elif kind == 'varint':
            node.f['v'] = (self.p, None, self.varint())
        elif kind.startswith('fixed'):
            self.take(int(kind[5:]))
        elif kind == 'kstrarray':
            for _ in range(max(self.varint() - 1, 0)):
                self.kstring()
        elif kind == 'enumarray':
            for _ in range(max(self.varint() - 1, 0)):
                self.typed_slot('VideoEnumA')
        elif kind in ('rfarray', 'rvarray', 'rsarray', 'farray'):
            n = self.varint()
            if kind == 'farray':
                n = max(n - 1, 0)
            if kind == 'rvarray':
                for _ in range(n):
                    self.varint()
            else:
                self.take(n * (2 if kind == 'rsarray' else 4))
        elif kind == 'boxfarray':           # Float[]: 길이+1, 원소마다 표지 + float
            for _ in range(max(self.varint() - 1, 0)):
                if self.varint():
                    self.take(4)
        elif kind in ('defvtx', 'defvtx0'):  # defvtx0(4.2.43): 묶음 머리 varint가 0이면 그 묶음은 float 없음
            total, chunks = self.varint(), self.varint()
            for i in range(chunks):
                if self.take(1)[0] or kind == 'defvtx':     # 머리는 varint가 아니라 1바이트(0x80도 나온다)
                    self.take(4 * min(128, total - 128 * i))
        else:
            self.fail(f'모르는 종류 {kind}')
        self.flush(node)
        self.stack.pop()
        node.end = self.p
        self.nodes.append(node)
        return node

    def field(self, node, key, ty):
        vat = self.p
        node.f[key] = (vat, None, self.value(node.cls, key, ty))
        node.f[key] = (vat, self.p, node.f[key][2])

    def value(self, cls, key, ty):
        if ty == 'V':
            return self.varint()
        if ty == 'VB':          # 정수 + 표지 1바이트
            v = self.varint()
            self.take(1)
            return v
        if ty == 'F':
            return struct.unpack('>f', self.take(4))[0]
        if ty == 'S':
            return self.string_slot()
        if ty == 'O':
            return self.obj_slot()
        if ty == 'T':
            return self.typed_slot(self.slots[cls][key])
        if ty == 'N':           # 본문 없는 새 객체(클래스 + 표지 1)
            at = self.p
            c = self.varint()
            cls2 = self.names.get(str(c))
            if self.varint() != 1:
                self.fail('N 슬롯이 새 객체가 아닙니다', at)
            node = self.new_node(cls2, at)
            node.end = self.p
            self.nodes.append(node)
            self.add_slot(at, c, node)
            return None
        if ty == 'LO':          # 개수 + 객체 슬롯들
            return [self.obj_slot() for _ in range(self.varint())]
        self.fail(f'모르는 값 타입 {ty}')

    # ------------------------------------------------------------ 조회(4.2·4.3 공통)

    def of(self, cls):
        return sorted((n for n in self.nodes if n.cls == cls), key=lambda n: n.start)

    def deref(self, v):
        return self.byid[tuple(v)] if isinstance(v, Ref) else v

    def event_defs(self):
        """[(이름, EventData 노드)] — 파일에 나오는 순서."""
        return [(n.f['2'][2], n) for n in self.of('EventData')]

    def event_def(self, name):
        for nm, n in self.event_defs():
            if nm == name:
                return n
        return None

    def animation(self, name):
        for a in self.of('Animation'):
            if a.f['1'][2] == name:
                return a
        return None

    @staticmethod
    def is_event_timeline(tl):
        if 'type' in tl.f:                      # 4.3식 고정 배치: 종류 12
            return tl.f['type'][2] == 12
        return '4' in tl.f and isinstance(tl.f['4'][2], (Ref, Node))    # 4.0.07: 필드 4 = 정의

    @staticmethod
    def def_key(tl):
        """이벤트 타임라인이 가리키는 정의 필드. 이벤트 타임라인은 정의마다 하나다."""
        return 'extra12' if 'extra12' in tl.f else '4'

    def event_group(self, anim):
        """(이벤트 타임라인 그룹, [정의별 이벤트 타임라인…]) 또는 (None, [])"""
        groups = self.deref(anim.f['2'][2])
        for _, _, group in groups.items:
            group = self.deref(group)
            tls = [self.deref(t) for _, _, t in self.deref(group.f['2'][2]).items]
            evs = [t for t in tls if self.is_event_timeline(t)]
            if evs:
                return group, evs
        return None, []

    def timeline_def(self, tl):
        return self.deref(tl.f[self.def_key(tl)][2])

    def key_list(self, tl):
        return self.deref(tl.f['keys' if 'keys' in tl.f else '2'][2])

    def anim_event_keys(self, anim):
        """애니메이션의 모든 이벤트 키(정의별 타임라인을 합쳐 프레임 순)."""
        out = []
        for tl in self.event_group(anim)[1]:
            out += self.event_keys(tl)
        return sorted(out, key=lambda k: k[0])

    @staticmethod
    def time_key(key):
        return 'time' if 'time' in key.f else '2'

    def event_key(self, key):
        """키 안에서 Event 객체가 있는 필드: 4.3식 payload12, 4.0.07 '7', 3.8은 스키마 key_event_field('14')"""
        return 'payload12' if 'payload12' in key.f else self.key_event_field

    def event_keys(self, tl):
        """[(프레임, 이벤트 이름, int, float, string, 키 노드)]"""
        out = []
        for _, _, key in self.key_list(tl).items:
            key = self.deref(key)
            ev = self.deref(key.f[self.event_key(key)][2])
            data = self.deref(ev.f['1'][2])
            out.append((key.f[self.time_key(key)][2], data.f['2'][2],
                        unzigzag(ev.f['2'][2]), ev.f['3'][2], ev.f['4'][2], key))
        return out


def load(path):
    body = read_body(path)
    head = Document.__new__(Document)
    head.b, head.p = body, 0
    for _ in range(3):
        Document.varint(head)
    version = Document.kstring(head)
    # 스키마 고르기: 파일마다 담당 포맷 목록(formats, 없으면 파일 이름)을 두고, 해당하는 스키마를 실제로 읽어
    # «끝까지 읽힘 + 다시 쓰기 = 원본»인 첫 스키마를 쓴다. 같은 포맷 문자열이라도 구조가 다른 경우가 있다
    # (예: 4.3.75-beta와 4.3.00 정식이 모두 "4.3.00").
    schemas = []
    for p in sorted(glob.glob(os.path.join(HERE, 'schema', '*.json'))):
        s = json.load(open(p))
        name = os.path.basename(p)[:-5]
        s.setdefault('formats', [name])
        schemas.append((name, s))
    cands = sorted((x for x in schemas if version in x[1]['formats']), key=lambda x: x[0] != version)
    if not cands and version and version.split('.')[0].isdigit() and 1 <= int(version.split('.')[0]) <= 3:
        raise UsageError(f'Spine {version} 프로젝트입니다. 이 도구는 Spine 3.8 이후 프로젝트만 지원합니다')
    if not cands:
        have = ', '.join(sorted({f for _, s in schemas for f in s['formats']}))
        raise FormatError(f'지원하지 않는 프로젝트 포맷 버전 {version!r} (지원: {have})')
    errors = []
    for name, schema in cands:
        try:
            doc = Document(body, schema)
            if serialize(doc) == body:
                schema['_name'] = name
                return body, schema, doc
            errors.append(f'{name}: 다시 쓰기가 원본과 다름')
        except FormatError as e:
            errors.append(f'{name}: {e}')
    raise FormatError('; '.join(errors))


# ---------------------------------------------------------------- 쓰기(Kryo 참조 규칙)

def serialize(doc):
    """그래프를 본문 바이트로. 객체는 처음 만나는 슬롯에서 본문째 쓰고(표지 1), 그 뒤로는 번호 + 2."""
    out = bytearray()
    ids = {'A': {}, 'B': {}}

    def put_slot(s):
        o = s.obj
        if s.prefix is not None:
            out.extend(varint(s.prefix))
        if o.tb == 'none':
            put_body(o)
            return
        if o.tb == 'flag':          # 참조 추적 없이 «null 아님» 표지(1)만
            out.append(1)
            put_body(o)
            return
        seen = ids[o.tb]
        if id(o) in seen:
            out.extend(varint(seen[id(o)] + 2))
            return
        seen[id(o)] = len(seen)
        out.append(1)
        put_body(o)

    def put_body(o):
        for kind, _, _, v in o.parts:
            if kind == 'raw':
                out.extend(v)
            else:
                put_slot(v)

    sys.setrecursionlimit(max(sys.getrecursionlimit(), 20000))
    put_body(doc.top)
    return bytes(out)


# ---------------------------------------------------------------- 그래프 편집

def replace_range(node, p0, p1, new):
    """node 본문에서 원래 위치 [p0, p1)를 new 조각들로 바꾼다(p0 == p1이면 그 자리에 끼운다)."""
    before, after = [], []
    for part in node.parts:
        k, s0, s1, v = part
        if s1 <= p0:
            before.append(part)
        elif s0 >= p1:
            after.append(part)
        elif p0 <= s0 and s1 <= p1:
            continue
        else:
            if k != 'raw' or len(v) != s1 - s0:
                raise FormatError(f'{node.cls} 본문의 [{p0:#x}, {p1:#x})를 고칠 수 없습니다')
            if s0 < p0:
                before.append(('raw', s0, p0, v[:p0 - s0]))
            if s1 > p1:
                after.append(('raw', p1, s1, v[p1 - s0:]))
    node.parts = before + [(k, p0, p0, v) for k, v in new] + after


def set_raw(node, key, data):
    vat, end, _ = node.f[key]
    replace_range(node, vat, end, [('raw', data)])


def set_slot(node, key, slot):
    vat, end, _ = node.f[key]
    replace_range(node, vat, end, [('slot', slot)])


def child(node, key):
    """필드 key 자리의 슬롯이 가리키는 객체(복제본에서도 복제된 자식을 돌려준다)."""
    vat = node.f[key][0]
    for k, s0, _, v in node.parts:
        if k == 'slot' and s0 == vat:
            return v.obj
    return None


def list_slots(node):
    return [v for k, _, _, v in node.parts if k == 'slot']


def set_list(node, slots):
    """리스트 본문을 크기 varint + 슬롯들로 다시 짠다(원소에 null이 없을 때만)."""
    if any(v is None for _, _, v in node.items):
        raise FormatError(f'{node.cls} 리스트에 null 원소가 있어 고칠 수 없습니다')
    node.parts = [('raw', node.size_at, node.size_at, varint(len(slots)))] + \
                 [('slot', node.size_at, node.size_at, s) for s in slots]


def clone(node, deep, memo=None):
    """객체 복제. deep 클래스의 자식은 함께 복제하고, 이미 복제한 객체(부모 등)를 가리키는 슬롯은 복제본으로 돌린다."""
    memo = {} if memo is None else memo
    new = Node(node.cls, None, node.start, node.tb)
    memo[id(node)] = new
    new.f, new.items, new.size_at, new.end = dict(node.f), list(node.items), node.size_at, node.end
    for k, s0, s1, v in node.parts:
        if k == 'slot':
            o = v.obj
            if id(o) in memo:
                o = memo[id(o)]
            elif o.cls in deep:
                o = clone(o, deep, memo)
            v = Slot(v.prefix, o)
        new.parts.append((k, s0, s1, v))
    return new


def new_string(doc, s):
    """새 문자열 객체. 빈 문자열은 Spine처럼 이미 있는 '' 객체를 함께 쓴다."""
    if s == '':
        for n in doc.nodes:
            if n.cls == 'String' and n.f.get('s', (0, 0, None))[2] == '':
                return n
    n = Node('String', None, 0, doc.table('String'))
    n.parts = [('raw', 0, 0, kryo_string(s))]
    n.f['s'] = (0, 0, s)
    return n


class Injector:
    """객체 그래프 편집. 바꾼 결과는 serialize(doc)로 얻는다."""

    def __init__(self, doc):
        self.doc = doc
        self.added = []         # 사람이 읽을 변경 기록

    # -- 이벤트 정의 -------------------------------------------------------

    def skeleton(self):
        sk = self.doc.of('Skeleton')
        if len(sk) != 1:
            raise UsageError(f'스켈레톤이 {len(sk)}개입니다(이 PoC는 스켈레톤 하나만 다룹니다)')
        return sk[0]

    def add_event_def(self, name, ival=0, fval=0.0, sval=''):
        doc = self.doc
        if doc.event_def(name):
            raise UsageError(f'이벤트 정의 {name!r}가 이미 있습니다')
        defs = doc.event_defs()
        if not defs:
            raise UsageError('파일에 이벤트 정의가 하나도 없습니다(정의 목록·이벤트 폴더 견본이 없어 아직 만들 수 없습니다)')
        tpl = defs[0][1]
        new = clone(tpl, deep=())
        set_slot(new, '2', Slot(None, new_string(doc, name)))
        set_raw(new, '3', varint(zigzag(ival)))
        set_raw(new, '4', struct.pack('>f', fval))
        set_slot(new, '5', Slot(None, new_string(doc, sval)))
        if '6' in new.f:                                    # audio 경로: 비움
            set_slot(new, '6', Slot(None, new_string(doc, '')))
        # 스켈레톤의 정의 목록과 이벤트 폴더(정의의 필드 10)의 자식 목록에 끝으로 붙인다.
        sk = self.skeleton()
        evlist = doc.deref(sk.f['10'][2])
        folder = doc.deref(tpl.f['10'][2])
        if not isinstance(evlist, Node) or not isinstance(folder, Node) or folder.cls != 'Folder':
            raise FormatError('이벤트 정의 목록·폴더 구조가 예상과 다릅니다')
        children = doc.deref(folder.f['5'][2])
        cnum = doc.cnum['EventData']
        set_list(evlist, list_slots(evlist) + [Slot(cnum, new)])
        set_list(children, list_slots(children) + [Slot(cnum, new)])
        for key, val in (('2', name), ('3', zigzag(ival)), ('4', fval), ('5', sval)):   # 조회용 값도 맞춘다
            new.f[key] = (new.f[key][0], new.f[key][1], val)
        self.added.append(f'이벤트 정의 {name!r} 추가')
        return new

    # -- 이벤트 키 ---------------------------------------------------------

    def add_key(self, anim_name, frame, data=None, ival=None, fval=None, sval=None):
        """키 하나 추가. data(EventData)를 안 주면 앞 키의 정의(새 타임라인이면 첫 정의).
        값(int·float·string)을 안 주면 정의의 기본값. 돌려주는 값: (키, 정의)"""
        doc = self.doc
        anim = doc.animation(anim_name)
        if anim is None:
            raise UsageError(f'애니메이션 {anim_name!r}이 없습니다')
        group, tls = doc.event_group(anim)
        if data is None:            # 정의를 안 주면 그 프레임 앞 키의 정의, 키가 없으면 첫 정의
            keys = doc.anim_event_keys(anim)
            if keys:
                before = [k for k in keys if k[0] <= frame]
                knode = (before[-1] if before else keys[0])[5]
                data = doc.deref(doc.deref(knode.f[doc.event_key(knode)][2]).f['1'][2])
            else:
                defs = doc.event_defs()
                if not defs:
                    raise UsageError('이벤트 정의가 없습니다. --event 이름 --new-event로 만드세요')
                data = defs[0][1]
        tl = next((t for t in tls if doc.timeline_def(t) is data), None)
        if tl is None:              # 이 정의의 타임라인이 없다: 그룹(없으면 그룹째)에 새로 만든다
            if group is None:
                group, tl = self.add_event_group(anim, data)
            else:
                tl = self.add_def_timeline(group, tls[0], data)
            keylist = child(tl, 'keys' if 'keys' in tl.f else '2')
            slots = list_slots(keylist)
            src, idx, fresh = slots[0].obj, 0, True
        else:
            keylist = doc.key_list(tl)
            slots = list_slots(keylist)
            times = [s.obj.f[doc.time_key(s.obj)][2] for s in slots]
            if any(t == frame for t in times):      # Spine은 겹친 키 하나를 읽으면서 버린다(오라클로 확인)
                raise UsageError(f'{anim_name} {frame}프레임에 이미 {data.f["2"][2]!r} 키가 있습니다')
            idx = sum(1 for t in times if t <= frame)
            src, fresh = (slots[idx - 1] if idx else slots[0]).obj, False
        key = clone(src, deep=('Event',))
        set_raw(key, doc.time_key(key), struct.pack('>f', float(frame)))
        ev = child(key, doc.event_key(key))
        if data is None:
            data = doc.deref(ev.f['1'][2])
        set_slot(ev, '1', Slot(None, data))
        want = (unzigzag(data.f['3'][2]) if ival is None else ival,
                data.f['4'][2] if fval is None else fval,
                (data.f['5'][2] or '') if sval is None else sval)
        have = (unzigzag(ev.f['2'][2]), ev.f['3'][2], ev.f['4'][2] or '')
        if want[0] != have[0]:
            set_raw(ev, '2', varint(zigzag(want[0])))
        if struct.pack('>f', want[1]) != struct.pack('>f', have[1]):
            set_raw(ev, '3', struct.pack('>f', want[1]))
        if sval is not None:
            set_slot(ev, '4', Slot(None, new_string(doc, sval)))
        elif want[2] != have[2]:                        # 정의의 문자열 객체를 함께 쓴다(Spine과 같게)
            dstr = child(data, '5')
            set_slot(ev, '4', Slot(None, dstr if dstr is not None else new_string(doc, want[2])))
        if fresh:                                       # 새 타임라인: 견본 키를 이 키로 바꾼다
            set_list(keylist, [Slot(slots[0].prefix, key)])
        else:
            slots.insert(idx, Slot(slots[0].prefix, key))
            set_list(keylist, slots)
        self.added.append(f'{anim_name} {frame}프레임 키 추가({data.f["2"][2]})')
        return key, data

    TL_DEEP = ('Timeline', 'List', 'KeyList', 'Keyframe', 'Event')

    def _reset_timeline(self, tl, data):
        """복제한 이벤트 타임라인을 data 정의용으로: 정의 참조를 바꾸고 견본 키 하나만 남긴다(add_key가 바꾼다)."""
        set_slot(tl, self.doc.def_key(tl), Slot(None, data))
        keylist = child(tl, 'keys' if 'keys' in tl.f else '2')
        set_list(keylist, list_slots(keylist)[:1])

    def add_def_timeline(self, group, tpl, data):
        """이벤트 그룹에 data 정의용 타임라인을 하나 더 만든다(Spine도 정의마다 타임라인을 따로 둔다)."""
        tl = clone(tpl, self.TL_DEEP)
        self._reset_timeline(tl, data)
        tlist = self.doc.deref(group.f['2'][2])
        cur = list_slots(tlist)
        set_list(tlist, cur + [Slot(cur[0].prefix, tl)])
        self.added.append(f'이벤트 타임라인 생성({data.f["2"][2]})')
        return tl

    def add_event_group(self, anim, data):
        """이벤트 키가 없는 애니메이션에 다른 애니메이션의 이벤트 타임라인 그룹을 견본으로 복제해 붙인다."""
        doc = self.doc
        tpl = None
        for a in doc.of('Animation'):
            g, _ = doc.event_group(a)
            if g is not None:
                tpl = g
                break
        if tpl is None:
            raise UsageError('파일에 이벤트 키가 하나도 없습니다(견본 타임라인이 없어 아직 만들 수 없습니다)')
        group = clone(tpl, ('TimelineGroup',) + self.TL_DEEP)
        if '0' in group.f:          # 4.3.03부터 그룹이 애니메이션을 역참조하지 않는다
            set_slot(group, '0', Slot(None, anim))
        tlist = child(group, '2')
        tls = [s for s in list_slots(tlist) if doc.is_event_timeline(s.obj)]
        set_list(tlist, tls[:1])
        tl = tls[0].obj
        self._reset_timeline(tl, data)
        groups = doc.deref(anim.f['2'][2])
        cur = list_slots(groups)
        set_list(groups, cur + [Slot(cur[0].prefix if cur else doc.cnum['TimelineGroup'], group)])
        self.added.append(f'{anim.f["1"][2]}에 이벤트 타임라인 그룹 생성({data.f["2"][2]})')
        return group, tl


# ---------------------------------------------------------------- 오라클(Spine CLI)

def spine_export(path, version, outdir):
    r = subprocess.run([SPINE, '-u', version, '-i', path, '-o', outdir, '-e', 'json'],
                       capture_output=True, text=True)
    files = glob.glob(os.path.join(outdir, '*.json'))
    if r.returncode != 0 or not files:
        raise SystemExit(f'Spine CLI 익스포트 실패:\n{r.stdout[-2000:]}\n{r.stderr[-2000:]}')
    return json.load(open(files[0]))


def norm_events(j):
    """애니메이션별 이벤트 키를 (프레임, 이름, int, float, string) 정렬 목록으로(정의 기본값 반영)."""
    fps = j['skeleton'].get('fps', 30)
    defs = j.get('events', {})
    out = {}
    for name, anim in j['animations'].items():
        ks = []
        for e in anim.get('events', []):
            d = defs.get(e['name'], {})
            ks.append((round(e.get('time', 0) * fps), e['name'], e.get('int', d.get('int', 0)),
                       round(e.get('float', d.get('float', 0)), 4), e.get('string', d.get('string', ''))))
        out[name] = sorted(ks)
    return out


def oracle(src, out, version, expect_keys, expect_defs):
    """원본·결과를 JSON으로 내보내, 결과 = 원본 + 기대한 변경 인지 본다."""
    with tempfile.TemporaryDirectory() as d:
        a = spine_export(src, version, os.path.join(d, 'a'))
        b = spine_export(out, version, os.path.join(d, 'b'))
    ea, eb = norm_events(a), norm_events(b)
    want = {k: list(v) for k, v in ea.items()}
    for anim, key in expect_keys:
        want[anim] = sorted(want[anim] + [key])
    defs_a = dict(a.get('events', {}))
    for name, d in expect_defs:
        defs_a[name] = d
    report = []
    changed = [n for n in eb if eb[n] != ea.get(n)]
    for n in changed:
        report.append(f'{n} 키: {[(f, nm) for f, nm, *_ in ea.get(n, [])]} → {[(f, nm) for f, nm, *_ in eb[n]]}')
    if set(b.get('events', {})) != set(a.get('events', {})):
        report.append(f'이벤트 정의: {sorted(a.get("events", {}))} → {sorted(b.get("events", {}))}')
    ok_keys = eb == want
    ok_defs = b.get('events', {}) == defs_a
    for j in (a, b):
        j['skeleton'].pop('hash', None)
        j.pop('events', None)
        for anim in j['animations'].values():
            anim.pop('events', None)
    same = a == b
    return report, ok_keys, ok_defs, same


# ---------------------------------------------------------------- CLI

def describe(doc):
    print('이벤트 정의:', ', '.join(nm for nm, _ in doc.event_defs()) or '(없음)')
    for anim in doc.of('Animation'):
        keys = doc.anim_event_keys(anim)
        print(f'  {anim.f["1"][2]:14s}', ' '.join(f'{fr:g}:{nm}' for fr, nm, *_ in keys) or '-')


def main():
    ap = argparse.ArgumentParser(description='Spine .spine 프로젝트에 이벤트 키를 직접 심는 PoC')
    ap.add_argument('input')
    ap.add_argument('output', nargs='?')
    ap.add_argument('--list', action='store_true', help='애니메이션별 이벤트 키를 보여준다')
    ap.add_argument('--anim', help='키를 넣을 애니메이션(이벤트 타임라인이 없으면 새로 만든다)')
    ap.add_argument('--frame', type=int)
    ap.add_argument('--event', help='이벤트 정의 이름(기본: 복제한 키의 정의, 새 타임라인이면 첫 정의)')
    ap.add_argument('--new-event', action='store_true', help='--event 이름으로 이벤트 정의를 새로 만든다')
    ap.add_argument('--def-int', type=int, default=0, help='새 정의의 int 기본값')
    ap.add_argument('--def-float', type=float, default=0.0, help='새 정의의 float 기본값')
    ap.add_argument('--def-string', default='', help='새 정의의 string 기본값')
    ap.add_argument('--int', type=int, dest='ival')
    ap.add_argument('--float', type=float, dest='fval')
    ap.add_argument('--string', dest='sval')
    ap.add_argument('--oracle', action='store_true', help='Spine CLI로 원본과 결과를 JSON으로 내보내 비교한다')
    ap.add_argument('--spine-version', help='오라클에 쓸 Spine 버전(기본: 포맷별 스키마의 spine_version)')
    a = ap.parse_args()

    body, schema, doc = load(a.input)
    nobj = len(doc.objs['A']) + len(doc.objs['B'])
    print(f'포맷 {doc.version}, 본문 {len(body):,}B, 객체 {nobj:,}개, 역참조 {len(doc.refs):,}개')
    if serialize(doc) != body:
        raise FormatError('그래프를 다시 써도 원본과 같지 않습니다(이 파일은 지원 범위 밖)')
    if a.list:
        describe(doc)
        return
    if not a.output:
        ap.error('OUT.spine이 필요합니다')
    if a.anim is None and not a.new_event:
        ap.error('--anim/--frame 또는 --new-event가 필요합니다')
    if (a.anim is None) != (a.frame is None):
        ap.error('--anim과 --frame은 함께 줘야 합니다')
    if a.frame is not None and a.frame < 0:
        ap.error('--frame은 0 이상이어야 합니다')
    if a.new_event and not a.event:
        ap.error('--new-event에는 --event 이름이 필요합니다')
    if os.path.abspath(a.output) == os.path.abspath(a.input):
        ap.error('원본을 덮어쓰지 않습니다. 다른 출력 경로를 주세요')

    inj = Injector(doc)
    expect_keys, expect_defs = [], []
    data = None
    if a.new_event:
        data = inj.add_event_def(a.event, a.def_int, a.def_float, a.def_string)
        d = {}
        if a.def_int:
            d['int'] = a.def_int
        if a.def_float:
            d['float'] = a.def_float
        if a.def_string:
            d['string'] = a.def_string
        expect_defs.append((a.event, d))
    elif a.event:
        data = doc.event_def(a.event)
        if data is None:
            raise UsageError(f'이벤트 정의 {a.event!r}가 없습니다(--new-event로 만들 수 있습니다)')
    if a.anim is not None:
        _, data = inj.add_key(a.anim, a.frame, data, a.ival, a.fval, a.sval)
        dname = data.f['2'][2]
        dd = (unzigzag(data.f['3'][2]), round(data.f['4'][2], 4), data.f['5'][2] or '')
        expect_keys.append((a.anim, (a.frame, dname,
                                     dd[0] if a.ival is None else a.ival,
                                     dd[1] if a.fval is None else round(a.fval, 4),
                                     dd[2] if a.sval is None else a.sval)))

    new_body = serialize(doc)
    check = Document(new_body, schema)
    if serialize(check) != new_body:
        raise FormatError('결과를 다시 읽어 쓴 바이트가 다릅니다')
    for line in inj.added:
        print(line)
    nnew = len(check.objs['A']) + len(check.objs['B'])
    print(f'객체 {nobj:,} → {nnew:,}개, 역참조 {len(doc.refs):,} → {len(check.refs):,}개')
    if a.anim is not None:
        print(f'{a.anim} 이벤트 키:', ' '.join(f'{fr:g}:{nm}' for fr, nm, *_ in
                                          check.anim_event_keys(check.animation(a.anim))))
    if a.new_event:
        print('이벤트 정의:', ', '.join(nm for nm, _ in check.event_defs()))
    write_body(a.output, new_body, like=a.input)
    print('저장:', a.output)

    if a.oracle:
        version = a.spine_version or schema.get('spine_version', '4.2.43')
        report, ok_keys, ok_defs, same = oracle(a.input, a.output, version, expect_keys, expect_defs)
        print(f'오라클(Spine {version}):')
        for line in report:
            print('  ' + line)
        print(f'  키 기대값 일치: {ok_keys} / 정의 기대값 일치: {ok_defs} / 나머지 데이터 동일: {same}')
        ok = ok_keys and ok_defs and same
        print('판정:', '통과' if ok else '실패')
        sys.exit(0 if ok else 1)


if __name__ == '__main__':
    try:
        main()
    except FormatError as e:
        sys.exit(f'포맷 오류: {e}')
    except UsageError as e:
        sys.exit(str(e))
