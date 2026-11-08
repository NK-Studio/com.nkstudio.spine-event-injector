"""애니메이션 정리: Spine 내보내기의 "애니메이션 정리"와 같은 일을 모델에 적용한다.

규칙은 Spine CLI의 정리 전·후 결과를 공식 예제 전체에서 비교해 얻었다.
- 값이 앞 키와 같고 그 구간이 평평하며, 뒤 키도 같거나 마지막인 키는 지운다.
- 남은 키가 설정 자세와 같은 키 하나뿐이면 타임라인을 지운다.
- 첨부·드로우 오더는 앞 키와 같으면 지우고, 앞쪽의 설정과 같은 키도 지운다.
- 그 결과 애니메이션이 짧아지면, 마지막 시간에 키가 있던 타임라인 중 맨 뒤의 것에 그 키를 되살린다.
- 앞쪽이 설정 자세로 평평하면 변화 직전 키만 남긴다.
- IK, 물리, 슬라이더, 이벤트, 시퀀스는 건드리지 않는다.
"""
from json_writer import _ulps

CHANNELS = {'rotate': 1, 'translate': 2, 'scale': 2, 'shear': 2, 'translatex': 1, 'translatey': 1, 'color': 7,
            'transform': 6, 'pathPosition': 1, 'pathSpacing': 1, 'pathMix': 3}


UNSET = 2147483648.0


def near(a, b):
    return abs(a - b) <= 1e-6 or _ulps(a, b) <= 1


def color_bytes(values):
    return [int(round(x * 255)) for x in values]


class Cleaner:
    def __init__(self, model):
        self.m = model
        self.slots = {s['name']: s for s in model['slots']}
        self.cons = {c['name']: c for c in model['constraints']}

    def same(self, t, a, b):
        ty = t['type']
        if ty == 'attachment':
            return a['name'] == b['name']
        if ty == 'deform':
            x, y = a['dense'], b['dense']
            n = max(len(x), len(y))
            x = list(x) + [0.0] * (n - len(x))
            y = list(y) + [0.0] * (n - len(y))
            return all(near(p, q) for p, q in zip(x, y))
        if ty == 'drawOrder':
            return self.order(a) == self.order(b)
        if ty == 'color':
            return color_bytes(a['values']) == color_bytes(b['values'])
        return all(near(p, q) for p, q in zip(a['values'], b['values']))

    @staticmethod
    def order(k):
        return sorted((s, n) for s, n in k['offsets'] if n)

    def flat(self, t, a, b):
        """a에서 b까지 값이 변하지 않는가(a와 b의 값이 같다고 가정)."""
        if t['type'] in ('attachment', 'drawOrder') or a['curve'] != 'bezier':
            return True
        n = CHANNELS.get(t['type'], 1)
        if t['type'] == 'deform':
            return True
        if len(a['handles']) < n or len(b['handles']) < n:
            return False
        # 정해지지 않은 손잡이(UNSET)는 값 높이에 놓이므로 평평하다
        return all((a['handles'][c][3] == UNSET or abs(a['handles'][c][3] - a['values'][c]) <= 1e-5)
                   and (b['handles'][c][1] == UNSET or abs(b['handles'][c][1] - b['values'][c]) <= 1e-5) for c in range(n))

    def is_setup(self, t, k):
        ty = t['type']
        if ty in ('rotate', 'translatex', 'translatey'):
            return near(k['values'][0], 0)
        if ty in ('translate', 'shear'):
            return near(k['values'][0], 0) and near(k['values'][1], 0)
        if ty == 'scale':
            return near(k['values'][0], 1) and near(k['values'][1], 1)
        if ty == 'attachment':
            return k['name'] == self.slots[t['slot']]['attachment']
        if ty == 'deform':
            return all(near(v, 0) for v in k['dense'])
        if ty == 'drawOrder':
            return not self.order(k)
        if ty == 'color':
            c = self.slots[t['slot']]['color']
            dark = self.slots[t['slot']]['dark']        # 투 컬러 슬롯은 어두운 색까지 같아야 설정과 같다
            return (color_bytes(k['values'])[:4] == [int(c[i:i + 2], 16) for i in (0, 2, 4, 6)]
                    and (dark is None or color_bytes(k['values'])[4:7] == [int(dark[i:i + 2], 16) for i in (0, 2, 4)]))
        if ty == 'transform':
            c = self.cons[t['constraint']]
            names = ('mixRotate', 'mixX', 'mixY', 'mixScaleX', 'mixScaleY', 'mixShearY')
            return all(near(v, c[n]) for v, n in zip(k['values'], names))
        if ty == 'pathPosition':
            return near(k['values'][0], self.cons[t['constraint']]['position'])
        if ty == 'pathSpacing':
            return near(k['values'][0], self.cons[t['constraint']]['spacing'])
        if ty == 'pathMix':
            c = self.cons[t['constraint']]
            return all(near(v, c[n]) for v, n in zip(k['values'], ('mixRotate', 'mixX', 'mixY')))
        return False

    def clean_keys(self, t):
        keys = t['keys']
        out = [keys[0]]
        if t['type'] in ('attachment', 'drawOrder'):        # 보간이 없는 타임라인: 앞 키와 같으면 지운다
            for k in keys[1:]:
                if not self.same(t, out[-1], k):
                    out.append(k)
            while out and self.is_setup(t, out[0]):         # 설정과 같은 앞쪽 키는 없어도 결과가 같다
                out.pop(0)
            t['keys'] = out
            return
        for i in range(1, len(keys)):
            k, prev = keys[i], out[-1]
            nxt = keys[i + 1] if i + 1 < len(keys) else None
            if self.same(t, prev, k) and self.flat(t, prev, k) and (
                    nxt is None or (self.same(t, k, nxt) and self.flat(t, k, nxt))):
                continue
            out.append(k)
        # 설정 자세로 시작해 값이 안 변하는 앞쪽 키: 첫 키 이전에는 어차피 설정 자세이므로 변화 직전 키만 남긴다
        while len(out) >= 2 and self.is_setup(t, out[0]) and self.same(t, out[0], out[1]) and self.flat(t, out[0], out[1]):
            out.pop(0)
        t['keys'] = out

    def clean_timeline(self, t):
        """타임라인을 남길지 돌려준다."""
        if t['type'] not in CHANNELS and t['type'] not in ('attachment', 'deform', 'drawOrder'):
            return True
        self.clean_keys(t)
        if not t['keys']:
            return False
        keys = t['keys']
        if len(keys) == 1 and self.is_setup(t, keys[0]):
            return False
        for a, b in zip(keys, keys[1:]):        # 지운 키 때문에 이웃이 된 두 키의 값이 같으면 사이 구간은 계단이다
            if self.same(t, a, b) and self.flat(t, a, b):
                a['curve'] = 'stepped'
        return True

    @staticmethod
    def last_frame(timelines):
        return max((k['frame'] for t in timelines for k in t['keys']), default=0)

    def run(self):
        for an in self.m['animations']:
            if an.get('noClean'):               # 애니메이션의 정리 제외 표시
                continue
            before = [(t, t['keys'][-1]) for t in an['timelines'] if t['keys']]
            duration = self.last_frame(an['timelines'])
            kept = [t for t in an['timelines'] if self.clean_timeline(t)]
            if self.last_frame(kept) < duration:    # 정리로 애니메이션 길이가 줄면 마지막 키를 하나 남긴다
                for t, key in reversed(before):     # 원래 순서에서 마지막으로 나오는 타임라인
                    if key['frame'] == duration and (t['type'] in CHANNELS or t['type'] in ('attachment', 'deform', 'drawOrder')):
                        if t not in kept:
                            t['keys'] = []
                            kept.append(t)
                        t['keys'].append(key)
                        break
            an['timelines'] = [t for t in an['timelines'] if t in kept]


def clean_model(model):
    Cleaner(model).run()
    return model
