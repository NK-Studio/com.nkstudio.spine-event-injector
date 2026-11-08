# 파이썬 PoC — `.spine`에 이벤트 키 직접 심기

`spine_event_poc.py` 하나와 포맷별 스키마(`schema/<포맷 버전>.json`: `4.0.07`, `4.2.43`, `4.3.00`)로 동작한다. 표준 라이브러리만 쓴다.

> 자세한 문서: 사용법 [`docs/PYTHON_POC_USAGE.md`](../docs/PYTHON_POC_USAGE.md) · 연구 기록 스펙시트 [`docs/POC_RESEARCH_SPEC.md`](../docs/POC_RESEARCH_SPEC.md)

```bash
# 이벤트 정의와 애니메이션별 이벤트 키(프레임:이름) 보기
python3 poc/spine_event_poc.py ~/Downloads/spineboy-4.2/spineboy-pro.spine --list

# run 12프레임에 footstep 키 추가 → 새 파일로 저장(원본은 건드리지 않음)
python3 poc/spine_event_poc.py ~/Downloads/spineboy-4.2/spineboy-pro.spine /tmp/out.spine --anim run --frame 12

# 이벤트 키가 없던 idle에 키, 새 정의 hit를 만들어 aim에 키
python3 poc/spine_event_poc.py IN.spine OUT.spine --anim idle --frame 5 --oracle
python3 poc/spine_event_poc.py IN.spine OUT.spine --anim aim --frame 2 --event hit --new-event --def-int 3 --oracle

# 세 포맷 × 10개 시나리오 묶음 판정
poc/run_checks.sh

# 값 지정 + Spine CLI 오라클 판정(원본·결과를 JSON으로 내보내 비교)
python3 poc/spine_event_poc.py ~/Downloads/spineboy-4.3/spineboy-pro.spine /tmp/out43.spine \
    --anim walk --frame 3 --event footstep --int 7 --float 1.5 --string hello --oracle
```

## 결과 (2026-11-08)

- 1차(바이트 끼우기 + 역참조 +k): 4.2 표본 4건, 4.3 표본 5건 통과.
- 2차(객체 그래프 재직렬화): `run_checks.sh` 30건(포맷 4.0.07·4.2.43·4.3.00 × 키 추가·맨 앞·맨 뒤·값 지정·4.3 정의 앞·새 그룹 2·새 정의 3) **모두 통과**. 1차 결과 10건과 바이트 동일. 4.2.43은 Spine이 직접 저장한 차등 표본(`research/diff-001-add-event/probe.spine`)을 바이트까지 재현.

자세한 원리·근거·한계: [`docs/POC_RESEARCH_SPEC.md`](../docs/POC_RESEARCH_SPEC.md), 바이트 관찰: [`docs/FORMAT_NOTES.md`](../docs/FORMAT_NOTES.md).

## 한계

- 4.2.43은 스킨·시퀀스·경로·그리기 순서까지 읽지만, 실제 작업 파일 끝의 **내보내기 설정(패킹 설정)·최근 경로 목록**은 아직 못 읽는다 → `포맷 오류: 모르는 …`로 파일을 쓰지 않고 멈춘다. 물리 제약 등 표본에 없던 구조도 같다.
- 정의·이벤트 키가 하나도 없는 파일은 견본이 없어 거부한다. audio·volume·balance 지정은 없다.
- 결과 파일을 사람이 에디터로 열어 보는 확인(P1-4)은 남아 있다.
