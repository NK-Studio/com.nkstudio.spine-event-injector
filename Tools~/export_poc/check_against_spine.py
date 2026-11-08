#!/usr/bin/env python3
"""우리 JSON·.skel 결과를 같은 표본의 Spine CLI 내보내기와 비교한다.

  python3 export_poc/check_against_spine.py [--versions 4.1.24,4.3.26] [--samples sb,sb-k]

표본은 research/versions/<표본>-<버전 뒤>.spine 이고, 비교는 그 버전의 CLI로 한다.
- 해시(본문 앞 8바이트)와 가중치 메시의 삼각형 순서는 알려진 차이라서 빼고 비교한다.
- 4.3 JSON은 숫자 끝자리(Spine의 짧은 표기 근사 오차)를 따로 세고, 구조 차이만 실패로 본다.
자세한 내용: docs/SPINE_EXPORT_USAGE.md
"""
import argparse
import glob
import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import binary_writer  # noqa: E402
import json_writer  # noqa: E402
import model as model_mod  # noqa: E402
import project_reader  # noqa: E402

SPINE = os.environ.get('SPINE_BIN', '/Applications/Spine.app/Contents/MacOS/Spine')
SAMPLES = os.path.join(os.path.dirname(HERE), 'research', 'versions')
VERSIONS = '4.0.64,4.1.24,4.2.00,4.2.43,4.3.00,4.3.26'


def spine_export(version, spine, out, kind):
    """CLI 내보내기 결과 파일 경로."""
    d = os.path.join(out, kind)
    os.makedirs(d, exist_ok=True)
    r = subprocess.run([SPINE, '-u', version, '-i', spine, '-o', d, '-e', kind], capture_output=True, text=True)
    found = glob.glob(os.path.join(d, '*.json' if kind == 'json' else '*.skel'))
    if not found:
        raise SystemExit(f'Spine {version} 내보내기 실패:\n{r.stdout[-800:]}{r.stderr[-400:]}')
    return found[0]


def parsed(text):
    obj = json.loads(text, parse_float=str, parse_int=str)
    obj['skeleton'].pop('hash', None)
    for skin in obj.get('skins', []):
        for atts in skin['attachments'].values():
            for a in atts.values():
                if 'triangles' in a:
                    t = a['triangles']
                    a['triangles'] = sorted(tuple(t[i:i + 3]) for i in range(0, len(t), 3))
    return obj


def near(a, b):
    try:
        x, y = float(a), float(b)
    except ValueError:
        return False
    return abs(x - y) <= 5e-5 * max(1.0, abs(x))


def diff(a, b, path, out):
    """구조 차이는 out[0], 숫자 끝자리 차이는 out[1]에 모은다."""
    if isinstance(a, dict) and isinstance(b, dict):
        if list(a) != list(b):
            out[0].append(f'{path}: 키 {list(a)[:8]} / {list(b)[:8]}')
        for k in a:
            if k in b:
                diff(a[k], b[k], f'{path}/{k}', out)
    elif isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            out[0].append(f'{path}: 길이 {len(a)} / {len(b)}')
        for i, (x, y) in enumerate(zip(a, b)):
            diff(x, y, f'{path}[{i}]', out)
    elif a != b:
        out[1 if isinstance(a, str) and isinstance(b, str) and near(a, b) else 0].append(f'{path}: {a!r} / {b!r}')


def check(spine, version, work):
    model = model_mod.read_model(project_reader.Project(spine))
    ref_text = open(spine_export(version, spine, work, 'json'), encoding='utf-8').read()
    out = ([], [])
    diff(parsed(json_writer.write_json(model, version)), parsed(ref_text), '', out)
    lines = out[1]
    json_ok = not out[0] and (not lines or version >= '4.3')

    # 삼각형 순서는 기준 JSON으로 맞춘 뒤 바이트로 비교한다.
    ref = json.loads(ref_text)
    for skin, ref_skin in zip(model['skins'], ref['skins']):
        for slot, key, att in skin['attachments']:
            if att['type'] == 'mesh':
                att['triangles'] = ref_skin['attachments'][slot][key]['triangles']
    ours = binary_writer.write_binary(model, version)
    theirs = open(spine_export(version, spine, work, 'binary'), 'rb').read()
    binary_ok = ours[8:] == theirs[8:]
    print(f'  JSON {"같음" if json_ok else "다름"} (구조 차이 {len(out[0])}, 숫자 끝자리 {len(lines)})'
          f' / 바이너리 {"같음" if binary_ok else "다름"} ({len(ours)} / {len(theirs)}바이트)')
    for d in out[0][:8] + ([] if json_ok else lines[:8]):
        print('    ', d)
    return json_ok and binary_ok


def main():
    ap = argparse.ArgumentParser(description='우리 내보내기를 Spine CLI 결과와 비교한다')
    ap.add_argument('--versions', default=VERSIONS)
    ap.add_argument('--samples', default='sb,sb-k', help='표본 이름 앞부분(sb → sb-<버전>.spine, sb-k → sb-<버전>-k.spine)')
    a = ap.parse_args()
    if not os.path.exists(SPINE):
        sys.exit(f'Spine을 찾을 수 없습니다: {SPINE} (SPINE_BIN으로 지정)')
    failed = total = 0
    with tempfile.TemporaryDirectory(prefix='spine-export-check.') as tmp:
        for version in a.versions.split(','):
            for sample in a.samples.split(','):
                name = f'sb-{version}.spine' if sample == 'sb' else f'sb-{version}-{sample[3:]}.spine'
                path = os.path.join(SAMPLES, name)
                if not os.path.exists(path):
                    print(f'건너뜀 {name} (표본 없음)')
                    continue
                print(f'{name} → Spine {version}')
                total += 1
                if not check(path, version, os.path.join(tmp, f'{name}-{total}')):
                    failed += 1
    print(f'결과: {total - failed}/{total} 통과')
    sys.exit(1 if failed or not total else 0)


if __name__ == '__main__':
    main()
