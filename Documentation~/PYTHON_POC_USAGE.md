# 파이썬 PoC 사용법 — `spine_event_poc.py`

`.spine` 프로젝트 파일의 애니메이션에 이벤트 키를 직접 넣고, 필요하면 이벤트 정의도 새로 만드는 스크립트다. 원리와 검증 결과는 [`POC_RESEARCH_SPEC.md`](POC_RESEARCH_SPEC.md)에 있다.

## 1. 준비

- Python 3.9 이상. 표준 라이브러리만 쓴다(설치할 패키지 없음).
- 파일 구성(함께 있어야 한다):

  ```
  poc/spine_event_poc.py      스크립트
  poc/schema/4.0.07.json      포맷 4.0.07(Esoteric 4.0~4.2 예제 spineboy)
  poc/schema/4.0.64.json      Spine 4.0.64 저장본 (자동 학습)
  poc/schema/4.2.00.json      Spine 4.0.07 ~ 4.2.20 저장본 (formats로 담당 버전 목록)
  poc/schema/4.2.43.json      Spine 4.2.22 ~ 4.2.43 저장본
  poc/schema/4.3.00.json      Spine 4.3.75-beta 저장본(포맷 문자열 4.3.00)
  poc/schema/4.3.00r.json     Spine 4.3.00 ~ 4.3.02 정식
  poc/schema/4.3.03.json      Spine 4.3.03
  poc/schema/4.3.04.json      Spine 4.3.04
  poc/schema/4.3.05.json      Spine 4.3.05 ~ 4.3.06
  poc/schema/4.3.26.json      Spine 4.3.07 ~ 4.3.26
  poc/run_checks.sh           세 포맷 × 10개 시나리오 오라클 판정 묶음
  ```

- `--oracle` 판정을 쓰려면: Spine 설치(`/Applications/Spine.app`, 다른 위치면 환경변수 `SPINE_BIN`), 해당 버전 업데이트가 로컬에 있음, Professional 라이선스.

명령은 저장소 루트에서 실행한다고 가정한다.

## 2. 파일 들여다보기 — `--list`

```bash
python3 poc/spine_event_poc.py ~/Downloads/spineboy-4.2/spineboy-pro.spine --list
```

```
포맷 4.0.07, 본문 181,156B, 객체 8,680개, 역참조 7,751개
이벤트 정의: footstep
  aim            -
  jump           36:footstep
  run            7:footstep 17:footstep
  walk           0:footstep 15:footstep
  ...
```

- 첫 줄의 **포맷**이 `poc/schema/`에 있는 파일 이름과 같아야 읽을 수 있다.
- 애니메이션마다 `프레임:이벤트이름`(정의별 타임라인을 합쳐 프레임 순). `-`는 이벤트 키가 없는 애니메이션이다(여기에도 넣을 수 있다).
- 읽은 뒤 «그대로 다시 쓰면 원본과 바이트가 같은지»를 매번 확인한다. 다르면 지원 범위 밖이라고 알리고 멈춘다.

## 3. 이벤트 키 넣기 / 정의 만들기

```bash
python3 poc/spine_event_poc.py 원본.spine 결과.spine --anim <애니메이션> --frame <프레임> [옵션]
python3 poc/spine_event_poc.py 원본.spine 결과.spine --event <새이름> --new-event [--anim … --frame …]
```

| 인자 | 뜻 |
| --- | --- |
| `원본.spine` | 읽을 파일. **절대 수정하지 않는다** |
| `결과.spine` | 새로 쓸 파일. 원본과 같은 경로면 거부 |
| `--anim` | 애니메이션 이름. 이벤트 키가 하나도 없으면 이벤트 타임라인 그룹을 새로 만든다 |
| `--frame` | 정수 프레임(0 이상). 초가 아니다 — 30fps에서 0.4초 = 12. 기존 키 앞·사이·뒤 어디든 된다 |
| `--event` | 이벤트 정의 이름. 생략하면 그 프레임 앞 키의 정의(키가 없으면 첫 정의) |
| `--new-event` | `--event` 이름으로 정의를 새로 만든다(`--anim` 없이 정의만 만들 수도 있다) |
| `--def-int` / `--def-float` / `--def-string` | 새 정의의 기본값(기본 0 / 0 / 빈 문자열) |
| `--int` / `--float` / `--string` | 이 키만의 값. 생략하면 정의의 기본값 |
| `--oracle` | Spine CLI로 원본·결과를 JSON으로 내보내 «결과 = 원본 + 기대한 변경»인지 판정 |
| `--spine-version` | 오라클에 쓸 Spine 버전(기본: 4.0.07·4.2.43 → 4.2.43, 4.3.00 → 4.3.26) |

정의마다 이벤트 타임라인이 따로 있다(Spine 에디터도 같은 구조로 저장한다). 그래서 다른 정의의 키를 넣으면 같은 이벤트 그룹 안에 그 정의용 타임라인을 새로 만든다.

### 예시

```bash
# run 8프레임에 footstep 키
python3 poc/spine_event_poc.py ~/Downloads/spineboy-4.2/spineboy-pro.spine ~/Downloads/spineboy-4.2/spineboy-pro-run8.spine \
    --anim run --frame 8 --oracle

# 이벤트 키가 없던 idle에 키 넣기(타임라인 그룹 생성)
python3 poc/spine_event_poc.py IN.spine OUT.spine --anim idle --frame 5 --oracle

# 새 정의 hit(int 기본값 3)를 만들고 aim 2프레임에 키
python3 poc/spine_event_poc.py IN.spine OUT.spine --anim aim --frame 2 --event hit --new-event --def-int 3 --oracle
```

출력 예(새 정의 + 키):

```
포맷 4.2.43, 본문 134,454B, 객체 5,317개, 역참조 5,683개
이벤트 정의 'zz_probe' 추가
이벤트 타임라인 생성(zz_probe)
run 3프레임 키 추가(zz_probe)
객체 5,317 → 5,325개, 역참조 5,683 → 5,692개
run 이벤트 키: 3:zz_probe 7:footstep 17:footstep
이벤트 정의: footstep, zz_probe
저장: OUT.spine
오라클(Spine 4.2.43):
  run 키: [(7, 'footstep'), (17, 'footstep')] → [(3, 'zz_probe'), (7, 'footstep'), (17, 'footstep')]
  이벤트 정의: ['footstep'] → ['footstep', 'zz_probe']
  키 기대값 일치: True / 정의 기대값 일치: True / 나머지 데이터 동일: True
판정: 통과
```

종료 코드: 성공 0, 오라클 판정 실패 1, 입력·포맷 오류는 메시지와 함께 0이 아닌 값.

## 4. 결과 확인하기

1. **자동**: `--oracle`. 대상 애니메이션의 키와 정의 목록이 기대한 만큼만 바뀌고(키는 프레임·이름·int·float·string까지 비교) 나머지 JSON이 원본과 같으면 `판정: 통과`.
2. **묶음 판정**: `poc/run_checks.sh` — 세 포맷 표본에 10개 시나리오씩(중간·맨 앞·맨 뒤·값 지정·4.3 정의 앞·새 그룹 ×2·새 정의 ×3) 주입하고 모두 오라클로 판정한다.
3. **목록으로**: 결과 파일에 `--list`를 다시 실행해 키가 보이는지 본다.
4. **사람 확인**: 결과 파일을 원본과 **같은 버전**의 Spine 에디터로 열어 Events 트리·Dopesheet·재생을 확인한다. 이미지가 원본 폴더 기준 상대 경로(`./images/`)라서 결과 파일을 원본 `.spine`과 같은 폴더에 두면 이미지까지 보인다.

   - 이벤트 키는 **도프 시트**에서 보인다(그래프 탭은 선택한 뼈의 곡선만 그려 이벤트가 안 보인다).
   - 이 Mac(macOS 27)에서 `Spine -u 4.2.43 파일`을 터미널에서 바로 실행하면 런처 4.3.06과 4.2.43의 GLFW가 겹쳐 시작 직후 `trace trap`으로 죽는다(파일과 무관). `open`으로 띄우면 훨씬 덜 나지만 가끔 같은 크래시가 난다(그때는 다시 연다):

     ```bash
     open -n -a /Applications/Spine.app --args -u 4.2.43 결과.spine
     ```

> ⚠️ 4.2 파일을 4.3 에디터로 열어 저장하면 포맷이 올라가 4.2에서 다시 열 수 없다. 확인용으로만 열고 저장하지 않는다.
>
> ⚠️ **에디터가 그 파일을 열어 둔 상태에서 주입하지 않는다.** 에디터는 파일을 열 때 한 번 읽고 바뀐 것을 감시하지 않으므로 화면에 반영되지 않고, 이후 에디터가 저장하면 주입한 키를 덮어쓴다(실험으로 확인). 순서: 에디터에서 저장·닫기 → 주입 → 다시 열기. 덮어썼다면 `~/Library/Application Support/Spine/backup/`의 직전 백업에서 되살릴 수 있다.

## 5. 오류 메시지와 대처

| 메시지 | 뜻 | 대처 |
| --- | --- | --- |
| `Spine 3.8.55 프로젝트입니다. 이 도구는 Spine 4.0 이후 프로젝트만 지원합니다` | 3.x 프로젝트 | 지원 범위 밖(결정 2026-11-08). 4.x 에디터에서 열어 저장하면 4.x 포맷이 된다 |
| `지원하지 않는 프로젝트 포맷 버전 '4.1.17'` | 그 포맷의 규칙 파일이 없다 | 지원 포맷으로 저장된 파일을 쓰거나 그 포맷 스키마를 새로 만든다 |
| `포맷 오류: @0x…: 모르는 필드 Skeleton.9` 등 | 표본에 없던 구조(스킨, 시퀀스, 경로·물리 제약 등)가 나왔다. **파일은 쓰지 않았다** | 그 파일로 스키마를 보강해야 한다 |
| `그래프를 다시 써도 원본과 같지 않습니다` | 읽기는 됐지만 다시 쓰기가 원본과 다르다. **파일은 쓰지 않았다** | 스키마 보강 필요 |
| `애니메이션 'xxx'이 없습니다` | 이름 오타 | `--list`로 이름 확인 |
| `이벤트 정의 'xxx'가 없습니다` | 없는 정의 이름 | `--new-event`를 붙이거나 이름 확인 |
| `이벤트 정의 'xxx'가 이미 있습니다` | `--new-event`인데 같은 이름이 있다 | `--new-event` 빼기 |
| `파일에 이벤트 정의가 하나도 없습니다` / `파일에 이벤트 키가 하나도 없습니다` | 견본이 될 정의·타임라인이 파일에 없다 | 에디터에서 정의·키를 하나 만들어 두면 그다음부터는 된다 |
| `run 8프레임에 이미 'footstep' 키가 있습니다` | 같은 정의의 키가 그 프레임에 이미 있다. Spine은 겹친 키 하나를 읽으면서 버린다(오라클로 확인) | 다른 프레임을 쓰거나 그대로 둔다 |
| `원본을 덮어쓰지 않습니다` | 결과 경로가 원본과 같음 | 다른 경로 지정 |
| `Spine CLI 익스포트 실패` | 오라클 실행 실패 | Spine 설치·버전·라이선스 확인, `--spine-version` 지정 |

## 6. 지원 범위 요약

| 된다 | 아직 안 된다 |
| --- | --- |
| 이벤트 키 추가(앞·중간·뒤, 4.3에서 정의를 품은 첫 키 앞 포함) | audio·volume·balance 지정 |
| 이벤트 키가 없는 애니메이션에 타임라인 그룹 생성 | 정의도 키도 하나 없는 파일(견본 없음) |
| 새 이벤트 정의(정의 목록 + 이벤트 폴더), 정의별 타임라인 생성 | 스켈레톤이 여럿인 파일 |
| 기존·새 정의 사용, 키별 int·float·string | 4.2.43 파일 끝의 내보내기 설정(패킹 설정)·최근 경로 목록이 있는 파일 — 실제 작업 파일 대부분(스켈레톤 데이터는 읽힘) |
| Spine 4.0.07 ~ 4.3.26의 런처로 받을 수 있는 모든 패치 버전이 저장한 파일(4.3.75-beta 포함) — 대표 버전마다 그 버전 CLI로 판정 | **Spine 3.x 프로젝트(지원 범위 밖, 거부)**, 4.2.21(런처에 없어 확인 못 함) |
