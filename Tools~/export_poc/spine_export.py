#!/usr/bin/env python3
"""Spine 없이 .spine 프로젝트를 JSON·바이너리(.skel)로 내보내고 이미지를 atlas로 묶는 PoC.

  python3 export_poc/spine_export.py 입력.spine 출력폴더 --version 4.2.43
  python3 export_poc/spine_export.py 입력.spine 출력폴더 --format binary --atlas --images 이미지폴더

자세한 내용: docs/SPINE_EXPORT_USAGE.md
"""
import argparse
import os
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import anim_clean  # noqa: E402
import atlas_packer  # noqa: E402
import binary_writer  # noqa: E402
import json_writer  # noqa: E402
import model as model_mod  # noqa: E402
import project_reader  # noqa: E402


# 팀 export 설정을 한 번에 켠다. 따로 준 옵션이 우선한다.
PRESETS = {
    'anc': {'format': 'binary', 'atlas': True, 'ness': True, 'clean': True, 'pma': False, 'bleed': True, 'alias': True,
            'max_size': 2048, 'skel_ext': '.skel.bytes', 'atlas_ext': '.atlas.txt'},
}
DEFAULTS = {'format': 'both', 'atlas': False, 'clean': False, 'pma': True, 'bleed': False, 'alias': False, 'max_size': atlas_packer.MAX_SIZE,
            'skel_ext': '.skel', 'atlas_ext': '.atlas'}


def parse_version(s):
    parts = s.split('.')
    if len(parts) != 3 or not all(p.isdigit() for p in parts) or not (4, 0) <= (int(parts[0]), int(parts[1])) <= (4, 3):
        raise SystemExit(f'--version은 4.0.xx ~ 4.3.xx 형식이어야 합니다: {s}')
    return s


def main():
    ap = argparse.ArgumentParser(description='Spine 없이 .spine을 JSON·.skel·atlas로 내보낸다')
    ap.add_argument('input')
    ap.add_argument('output_dir')
    ap.add_argument('--preset', choices=sorted(PRESETS), help='팀 export 설정을 한 번에 켠다(anc: Binary, 비필수 데이터, PMA 끔, 블리드, '
                    '에일리어스, 애니메이션 정리, 최대 2048, .skel.bytes, .atlas.txt). 따로 준 옵션이 우선한다')
    ap.add_argument('--format', choices=['json', 'binary', 'both'], default=None)
    ap.add_argument('--version', help='내보낼 런타임 버전(예: 4.2.43). 기본: 프로젝트에 적힌 포맷 버전')
    ap.add_argument('--name', help='출력 파일 이름(기본: 스켈레톤 이름). 스켈레톤이 하나일 때만')
    ap.add_argument('--skeleton', help='이 이름의 스켈레톤만 내보낸다(기본: 프로젝트의 모든 스켈레톤)')
    ap.add_argument('--atlas', action='store_true', default=None, help='이미지를 atlas로 묶는다')
    ap.add_argument('--images', help='이미지 폴더(기본: 프로젝트에 적힌 이미지 경로, .spine 기준)')
    ap.add_argument('--nonessential', dest='ness', action='store_true', default=None,
                    help='비필수 데이터를 넣는다(JSON은 기본으로 넣고 바이너리는 뺀다 — Spine CLI 기본과 같다)')
    ap.add_argument('--no-nonessential', dest='ness', action='store_false')
    ap.add_argument('--clean', dest='clean', action='store_true', default=None, help='Spine의 애니메이션 정리와 같이 불필요한 키를 지운다')
    ap.add_argument('--no-clean', dest='clean', action='store_false')
    ap.add_argument('--no-pma', dest='pma', action='store_false', default=None, help='알파 프리멀티플라이를 끈다(기본: 켬)')
    ap.add_argument('--pma', dest='pma', action='store_true', help='알파 프리멀티플라이를 켠다')
    ap.add_argument('--bleed', dest='bleed', action='store_true', default=None, help='투명 픽셀 RGB를 주변색으로 채운다(PMA를 끌 때만 적용)')
    ap.add_argument('--no-bleed', dest='bleed', action='store_false')
    ap.add_argument('--alias', dest='alias', action='store_true', default=None, help='같은 이미지를 atlas의 한 자리에 합친다')
    ap.add_argument('--no-alias', dest='alias', action='store_false')
    ap.add_argument('--max-size', type=int, default=None, help=f'atlas 페이지 최대 폭·높이(기본: {atlas_packer.MAX_SIZE})')
    ap.add_argument('--skel-ext', default=None, help='스켈레톤 바이너리 확장자(기본: .skel)')
    ap.add_argument('--atlas-ext', default=None, help='atlas 파일 확장자(기본: .atlas)')
    a = ap.parse_args()
    for source in (PRESETS.get(a.preset, {}), DEFAULTS):        # 직접 준 값 > 프리셋 > 기본값
        for k, v in source.items():
            if getattr(a, k) is None:
                setattr(a, k, v)
    if a.bleed and a.pma:
        print('참고: PMA가 켜져 있으면 블리드는 적용되지 않습니다(--no-pma와 함께 쓰세요).')

    try:
        project = project_reader.Project(a.input)
        models = model_mod.read_models(project)
    except (project_reader.FormatError, project_reader.P.UsageError, model_mod.ModelError, OSError, zlib.error) as e:
        sys.exit(f'읽을 수 없습니다: {e}')
    if project.partial:
        print(f'참고: 스켈레톤 뒤의 편집기 상태 일부를 읽지 못했습니다(내보내기에는 영향 없음): {project.partial}')
    if a.skeleton:
        chosen = [m for m in models if m['name'] == a.skeleton]
        if not chosen:
            sys.exit(f"스켈레톤 '{a.skeleton}'이 없습니다. 있는 것: {', '.join(m['name'] for m in models)}")
        models = chosen
    if a.name and len(models) > 1:
        sys.exit(f'스켈레톤이 {len(models)}개라 --name을 쓸 수 없습니다. --skeleton으로 하나를 고르세요.')
    version = parse_version(a.version) if a.version else parse_version(project.format)
    if not a.version:
        print(f'내보낼 버전을 지정하지 않아 프로젝트 포맷 {version}으로 씁니다. '
              '프로젝트 포맷은 저장한 Spine 버전과 다를 수 있으니(예: 4.0.07은 Spine 4.0~4.2.20 공통) '
              '게임 런타임에 맞춰 --version을 주세요.')
    os.makedirs(a.output_dir, exist_ok=True)
    failed = 0
    for model in models:
        if a.clean:
            anim_clean.clean_model(model)
        try:
            export_one(model, a, version, project.format)
        except (ExportError, atlas_packer.AtlasError) as e:
            failed += 1
            print(f"{model['name']}: 내보낼 수 없습니다: {e}", file=sys.stderr)
    if failed:
        sys.exit(f'스켈레톤 {len(models)}개 중 {failed}개를 내보내지 못했습니다.')


class ExportError(Exception):
    pass


def export_one(model, a, version, fmt):
    name = a.name or model['name']
    done = []
    if a.format in ('json', 'both'):
        try:
            text = json_writer.write_json(model, version, True if a.ness is None else a.ness)
        except ValueError as e:
            raise ExportError(e)
        path = os.path.join(a.output_dir, name + '.json')
        with open(path, 'w', encoding='utf-8') as f:
            f.write(text)
        done.append(path)
    if a.format in ('binary', 'both'):
        try:
            data = binary_writer.write_binary(model, version, False if a.ness is None else a.ness)
        except binary_writer.Unsupported as e:
            if a.format == 'binary':
                raise ExportError(f'.skel을 만들 수 없습니다(이 버전에 없는 기능: {e}). --format json을 쓰세요.')
            print(f"참고: {name}.skel은 만들지 않았습니다(이 버전에 없는 기능: {e}).")
        else:
            path = os.path.join(a.output_dir, name + a.skel_ext)
            with open(path, 'wb') as f:
                f.write(data)
            done.append(path)
    if a.atlas:
        images = a.images or os.path.join(os.path.dirname(os.path.abspath(a.input)), model['images'])
        settings = atlas_packer.AtlasSettings(a.pma, a.bleed, a.alias, a.max_size, a.atlas_ext)
        atlas, pngs = atlas_packer.write_atlas(model, images, a.output_dir, name, settings)
        done += [atlas] + pngs
    print(f"{name}: 포맷 {fmt} → {version}: 뼈 {len(model['bones'])}, 슬롯 {len(model['slots'])}, "
          f"애니메이션 {len(model['animations'])}")
    for path in done:
        print(f'저장: {path}')


if __name__ == '__main__':
    main()
