# SpineEventInjector — 1차(PoC) 스펙시트

> ⛔ **이 문서는 폐기됐다(2026-11-07).** 방향이 «파이썬 PoC → Unity UI Toolkit 에디터 플러그인»으로 바뀌었다.
> 현행 스펙: `MoonFrog/Assets/Plugins/SpineEventInjector/Documentation~/SPEC.md`. 이 폴더는 초기 분석 기록으로만 남긴다.


> 작성 2026-11-07 · 1차 검토 반영 2026-11-07(편집 전략 순서, AC-3 hash 제외, 프레임 기준 비교, 오라클 실행 조건) · 상태: **M0 완료, M1 착수 전**
> 포맷 관찰 원장(실측 바이트·가설): [`FORMAT_NOTES.md`](FORMAT_NOTES.md)

---

## 1. 목적

Spine 에디터 프로젝트 파일(`.spine`)을 **직접 읽고 고쳐서** 애니메이션에 이벤트를 넣는 C++ 도구를 만든다.
사용자가 이벤트를 지정하고 **「적용하기」를 누른 뒤, 그 파일을 Spine 공식 에디터로 열면 이벤트가 정확히 들어 있어야 한다.**

배경: MoonFrog는 Unity 쪽 `AnimationEvent` 12개를 Spine 원본의 이벤트로 옮겨야 한다(`MoonFrog/docs/Spine_Moon_Event_Migration_Spec.md`).
그런데 원본 `.spine`을 손으로 고치는 과정이 병목이다. `.spine` 포맷은 공개돼 있지 않다. 그래서 1차는 **역분석(RE)으로 포맷을 밝히고 PoC를 만드는 것**이 목표다.

## 2. 1차 범위

| 포함 | 제외(2차 이후) |
| --- | --- |
| Spine **4.2** 프로젝트 파일(표본: `spineboy-pro.spine`) | Spine 3.8 프로젝트(MoonFrog 원본일 가능성 높음, §11 Q3) · 4.3 |
| 이벤트 **정의** 추가(이름, int, float, string, audio, volume, balance) | 이벤트 삭제·이름 변경 |
| 애니메이션에 이벤트 **키** 추가(프레임/시간 지정) | 이벤트 키 이동·일괄 편집, 다른 타임라인 편집 |
| 기존 이벤트 정의에 키만 추가 | 여러 스켈레톤이 든 프로젝트(표본은 1개) |
| CLI + 최소 GUI(「적용하기」 버튼) | 배치 처리, 플러그인화 |

## 3. 용어

| 용어 | 뜻 |
| --- | --- |
| 컨테이너 | 디스크의 `.spine` 바이트 전체(압축된 상태) |
| 본문(body) | 컨테이너를 raw inflate한 바이트. 모든 편집은 본문에서 한다 |
| 이벤트 정의 | 스켈레톤 수준의 이벤트 종류(JSON의 `events{}`) |
| 이벤트 키 | 애니메이션 타임라인의 한 시점(JSON의 `animations.<anim>.events[]`) |
| 오라클 | Spine 공식 CLI로 `.spine` → JSON 익스포트한 결과. «Spine이 실제로 읽은 내용»의 기준 |
| 차등 표본 | Spine CLI import로 만든, 딱 한 가지만 다른 `.spine` 한 쌍 |

## 4. 확인된 사실 (2026-11-07 실측)

| # | 사실 | 근거 |
| --- | --- | --- |
| F1 | 컨테이너 = **zlib 헤더 없는 raw deflate 스트림 하나**. 뒤에 남는 바이트 없음 | `inflate(wbits=-15)`가 63,533B → 181,156B를 EOF까지 정확히 소비 |
| F2 | 본문을 **level 6 raw deflate로 다시 압축하면 원본과 바이트 동일** | Python zlib 1.2.12, C++ macOS SDK zlib 1.2.12 둘 다 일치. Spine 4.2.43이 import로 저장한 표본 2개도 일치(`ctest` 4/4) |
| F3 | 본문의 문자열은 **ASCII에 마지막 글자만 0x80 비트를 켜는 방식**(예: `footstep` → `…65 f0`) | `sei strings`. Kryo 직렬화의 ASCII 경로와 같은 규칙 |
| F4 | float는 **빅엔디언 IEEE754**(1.0 = `3f 80 00 00`) | 본문 곳곳의 `3f800000`, 1.5 = `3fc00000` 실측 |
| F5 | 본문 앞부분에 버전 문자열 `4.0.07`이 두 번 있다(0x04, 0x2d). 익스포트 JSON의 `spine`은 `4.2.22` | 프로젝트 **포맷** 버전과 저장한 **에디터** 버전이 따로 있다는 뜻으로 보임 |
| F6 | 이벤트 정의 목록은 «개수 + 레코드 나열» 구조. 이벤트 하나를 넣으면 개수가 `01`→`02`로 바뀌고 레코드 하나(≈56B)가 끼어든다 | `research/diff-001-add-event` |
| F7 | 🚨**객체 하나를 넣으면 파일 뒤쪽의 참조 번호가 밀린다**(`20 0d 01 01 portal-root` → `20 0f …`). 차이가 첫 0x33바이트 이후 파일 끝 143바이트 전까지 전역에 퍼진다 | diff-001: 공통 접두 0x33, 공통 접미 143B |
| F8 | 이벤트 키 시간은 **초 단위 float로 저장되지 않는다**(0.2333·0.5667·1.2를 float로 찾으면 0건) | 프레임 정수(varint)일 가능성(§11 가설 H4) |
| F9 | JSON → import로 만든 프로젝트는 원본보다 본문이 26% 작다(134,454B vs 181,156B) | 에디터 전용 데이터가 JSON 왕복에서 빠진다 → **JSON 경유 편집은 대안이 아니다**(§7 방법 C) |
| F10 | 로컬 환경: Spine Launcher 4.3.06, 에디터 **4.2.43 Professional** 설치됨. 헤드리스 CLI 익스포트가 5초 걸림 | `~/Library/Application Support/Spine/updates/4.2.43` |

## 5. 기능 요구사항

| ID | 요구 | 마일스톤 |
| --- | --- | --- |
| FR-1 | `.spine`을 열어 본문을 얻고, 무변경 저장이 바이트 동일해야 한다 | M0 ✅ |
| FR-2 | 스켈레톤의 이벤트 정의와 애니메이션별 이벤트 키를 나열한다(`sei events`) | M2 |
| FR-3 | 새 이벤트 정의를 추가한다. 필드: name(필수), int, float, string, audio, volume(기본 1), balance(기본 0) | M3 |
| FR-4 | 지정한 애니메이션의 지정 **프레임**(정수)에 이벤트 키를 추가한다. 입력 기본 단위는 프레임이고, 초를 입력하면 스켈레톤 FPS로 환산해 정수 프레임에 맞지 않으면 거부한다. 키별 int/float/string 덮어쓰기는 선택 | M3 |
| FR-5 | 이름 중복, 없는 애니메이션, 범위 밖 프레임 같은 입력 오류는 **저장 전에** 거부한다 | M3 |
| FR-6 | 저장할 때 `<파일>.bak`을 만들고(없을 때만) 임시 파일에 쓴 뒤 rename한다 | M0 ✅(`saveBody`) |
| FR-7 | GUI: 파일 열기 → 이벤트·애니메이션 목록 → 입력 폼 → **「적용하기」** → 결과 표시(오라클 결과 포함) | M4 |
| FR-8 | 저장 직후 다시 열어 FR-2로 읽히는지 자기검증한다. 실패하면 `.bak`에서 복원하고 오류를 보고한다 | M3 |

## 6. 비기능 요구사항

- C++20, CMake ≥ 3.24, Ninja. macOS arm64를 먼저 지원(Windows는 2차).
- 외부 의존성은 **zlib만**(시스템). GUI(M4)는 Dear ImGui + GLFW를 FetchContent로 들인다(§11 Q1 승인 후).
- 알 수 없는 바이트는 **해석하지 않고 그대로 보존**한다. 편집기는 «아는 것만 고치고 나머지는 원문 복사» 원칙을 따른다.
- 원본 표본에 대해 «파싱 → 직렬화 → 압축»이 바이트 동일해야 한다(M2 완료 조건).

## 7. 역분석 방법

| 방법 | 내용 | 역할 |
| --- | --- | --- |
| **A. 정적 관찰** | 본문 덤프(`sei unpack`), 문자열·float 패턴, 반복 구조 | 레코드 경계와 태그 후보 찾기 |
| **B. 차등 분석**(주력) | `tools/make_diff_pair.py`로 «한 가지만 다른» `.spine` 쌍을 Spine CLI import로 만들고 `sei diff`로 비교. 변형: 정의 추가 / 키 추가 / int·float·string·audio 각각 / 키 시간 0·1프레임·N프레임 | 필드별 인코딩과 참조 번호 규칙 확정 |
| **C. JSON 왕복**(대조용) | 익스포트 JSON에 이벤트를 넣고 import | 정답 표본 생성용. **납품 경로로는 쓰지 않는다**(F9: 에디터 데이터 손실) |
| **D. 오라클 판정** | `tools/oracle_export.sh`가 Spine 4.2.43 CLI로 JSON 익스포트 → 이벤트 출력 | 모든 수용 테스트의 자동 판정 기준 |

- 분석 대상은 **우리 데이터 파일**에 한정한다. Spine 에디터 실행 파일을 디컴파일하지 않는다(§10 R5).
- 차등 표본은 `research/diff-NNN-<설명>/`에 base·probe JSON과 `.spine`을 함께 보존한다. 확정된 사실은 `FORMAT_NOTES.md`로 옮긴다.

## 8. 아키텍처

```
src/core/DeflateCodec      raw inflate/deflate (level 6)                  ✅ M0
src/core/SpineProjectFile  컨테이너 ↔ 본문, 안전 저장(.bak + rename)      ✅ M0
src/core/KryoStream        varint·zigzag·BE float·0x80 종결 문자열 R/W   ✅ M0(기본 타입)
src/core/SpineDocument     본문 → 객체 트리(알 수 없는 구간은 원문 보존)  M2
src/core/RefTable          객체 참조 번호 수집·재번호(F7 대응)            M2
src/core/EventEditor       정의/키 추가, 검증, 직렬화                     M3
src/cli/main.cpp           sei info|unpack|pack|roundtrip|strings|diff    ✅ M0
                           sei events / sei inject                         M2 / M3
src/gui/                   Dear ImGui 프런트엔드(「적용하기」)             M4
tools/oracle_export.sh     Spine CLI 오라클                                ✅
tools/make_diff_pair.py    차등 표본 생성                                  ✅
```

**편집 전략**(M2에서 하나로 확정한다):

1. **부분 파싱 + 참조 재번호**(1순위): 이벤트 구간과 애니메이션 구간만 해석한다. 나머지는 원문으로 복사하면서 참조 번호 필드만 찾아 고친다. 참조 번호를 **빠짐없이 찾을 수 있다는 것**을 증명해야 쓸 수 있다. 증명 방법: 원본 표본에서 «찾은 참조 필드에 +1 → 다시 -1» 왕복이 바이트 동일하고, 차등 표본 probe의 참조 필드 위치를 base에서 예측해 전부 맞혀야 한다.
2. **전체 파싱**(2순위): 본문 전체를 객체 트리로 읽고 다시 쓴다. 가장 안전하지만 모든 타입을 알아야 한다. 1에서 참조 필드를 구분하지 못하는 구간이 남으면 그 구간만 2로 넘긴다.
3. **끝에 덧붙이기**(가설만 검증, 구현 계획 없음): 새 객체를 마지막 번호로 받아 앞쪽 번호가 밀리지 않게 하는 방식이다. 🚨**diff-001이 이미 반대 근거다.** 새 정의는 이벤트 목록의 «끝»에 들어갔는데도 그 뒤 참조 번호가 전부 밀렸다(F7). 번호를 직렬화 순서대로 매긴다면(Kryo 참조 방식, 가설 H3) 이 전략은 성립하지 않는다. M1 표본 6번으로 확인만 하고, 반증되면 지운다.

→ 실험 순서는 1 → 2다. 3은 M1에서 가설 판정만 한다.

## 9. 마일스톤

| M | 내용 | 완료 조건 |
| --- | --- | --- |
| **M0** ✅ | 프로젝트 골격, 컨테이너 코덱, 기본 타입 R/W, RE 보조 CLI, 오라클·차등 스크립트 | `ctest` 4/4 통과(왕복 바이트 동일 3건 포함) |
| **M1** | 차등 분석으로 이벤트 정의 레코드·이벤트 키 레코드·참조 번호 규칙 해독. 전략 3 가설 판정 | `FORMAT_NOTES.md`에 필드표 확정. 표본 6쌍 이상에서 예측한 바이트와 실제가 일치. 참조 번호가 직렬화 순서인지 결론 |
| **M2** | `SpineDocument`·`RefTable`, `sei events` | spineboy의 events가 오라클과 같음(정의 1개, 키 5개: jump 1, run 2, walk 2). 파싱→직렬화 왕복이 바이트 동일. 참조 필드 탐지가 §8 전략 1의 증명 조건을 통과 |
| **M3** | `EventEditor`, `sei inject`, 저장 후 자기검증 | §10 AC-1~AC-4 통과 |
| **M4** | GUI(「적용하기」) | AC-5 통과 |
| **M5** | 사람 검증 + 정리 | AC-6 통과, README·FORMAT_NOTES 최신화 |

## 10. 수용 기준

| AC | 시나리오 | 판정 |
| --- | --- | --- |
| AC-1 | spineboy에 새 정의 `sei_test`(int 7, float 1.5, string "hello")를 추가하고 `run` **3프레임**에 키를 추가 | 오라클 JSON의 `events.sei_test`가 값까지 일치하고, `animations.run.events`에 `name:sei_test`이면서 `round(time × fps) == 3`인 키가 정확히 1개 있음 |
| AC-2 | 기존 정의 `footstep`으로 `idle` 0프레임에 키 추가 | 오라클에서 idle 키 1개(`time` 생략 또는 0), 다른 애니메이션의 키는 프레임 기준으로 변화 없음 |
| AC-3 | 무관 데이터 보존 | 주입 전후 오라클 JSON에서 **`skeleton.hash`**, `events`, 해당 애니메이션의 `events`를 뺀 나머지가 **완전히 같음**. `hash`는 내용이 바뀌면 같이 바뀌는 값이라 비교에서 뺀다 |
| AC-4 | 잘못된 입력(없는 애니메이션, 중복 정의명, 음수 프레임) | 파일을 건드리지 않고 0이 아닌 종료 코드 |
| AC-5 | GUI에서 같은 작업 후 「적용하기」 | AC-1과 같은 오라클 결과 |
| AC-6 | **사람 검증**: 결과 파일을 Spine 4.2.43 에디터에서 연다 | 경고 없이 열리고, Events 트리와 Dopesheet의 해당 프레임에 이벤트가 보이고, 재생할 때 발생함 |

AC-1~AC-5는 `ctest` 통합 테스트로 자동화하고 라벨 `oracle`을 붙인다. AC-6은 자동화하지 않는다.

**시간 비교 규칙:** 오라클 JSON은 시간을 초 단위 소수 4자리로 반올림한다(예: 7/30초 → `0.2333`). 그래서 시간끼리 직접 비교하지 않고 항상 `round(time × fps)`로 프레임을 구해 비교한다. fps는 프로젝트의 스켈레톤 FPS다(JSON에 없으면 기본 30).

**오라클 실행 조건:** 오라클 테스트는 다음이 모두 있어야 돈다.

- Spine 에디터 설치(`/Applications/Spine.app` 또는 환경변수 `SPINE_BIN`)
- 업데이트 **4.2.43**이 로컬에 받아져 있음(`~/Library/Application Support/Spine/updates/4.2.43`). 없으면 런처가 내려받으려고 하므로 네트워크가 필요하다
- **Professional 라이선스가 활성화**돼 있음(CLI 익스포트·가져오기는 Pro 기능)

하나라도 없으면 CMake가 `oracle` 테스트를 등록하지 않고 그 사실을 configure 로그에 남긴다. CI나 다른 PC에서는 `ctest -LE oracle`(프리셋 `fast`)로 오라클 없이 돌린다. 오라클이 등록되지 않은 환경의 «통과»는 M3·M4 완료 근거가 될 수 없다.

## 11. 가설과 열린 질문

**가설(M1에서 검증):**

- H1 본문은 Kryo 계열 직렬화다(F3). 태그 바이트 + 값 형태의 필드 나열로 보인다.
- H2 이벤트 정의 레코드의 int 필드는 zigzag varint다(diff-001의 int 7 → `0e` 후보).
- H3 F7의 `0d`→`0f`는 객체 ID가 zigzag로 +1 된 것이다.
- H4 이벤트 키 시간은 프레임 정수다(스켈레톤 FPS 기준, 기본 30).
- H5 `4.0.07`은 프로젝트 포맷 버전이고, 4.0~4.2 에디터가 공유한다.

**사용자 결정이 필요한 것:**

- Q1 GUI 기술: Dear ImGui + GLFW(FetchContent로 GitHub에서 받음)로 해도 되는가?
- Q2 Windows 지원이 1차에 필요한가?
- Q3 MoonFrog의 실제 대상(달 `anim_env_moon`)은 3.8 프로젝트일 가능성이 높다(현재 저장소 스켈레톤이 3.8.75에서 변환됨). 2차에서 3.8 포맷을 다룰지, 원본을 4.2로 올려서 저장한 뒤 이 도구를 쓸지 정해야 한다.

## 12. 위험

| R | 위험 | 대응 |
| --- | --- | --- |
| R1 | 참조 번호 재번호 누락 → Spine이 파일을 못 열거나 엉뚱한 객체를 가리킴 | §8 전략 3부터 시도. 매 주입마다 AC-3 오라클 전체 비교 |
| R2 | 정체를 모르는 필드 | 원문 보존 원칙(§6). 모르는 구간을 바꾸는 편집은 거부 |
| R3 | 에디터 버전마다 포맷이 다름(4.2 vs 4.3) | 1차는 4.2.43 오라클로 고정. 4.3으로 열어 저장하면 포맷이 올라갈 수 있다는 점을 README에 경고 |
| R4 | zlib 버전에 따라 재압축 바이트가 달라짐 | 바이트 동일성은 테스트용 지표일 뿐이다. 판정 기준은 «Spine이 열리는가»(오라클) |
| R7 | 오라클이 로컬 Spine 설치·4.2.43 업데이트·Pro 라이선스에 묶여 있음 | 조건 미충족 시 자동 제외(§10). 판정 결과를 기록할 때 오라클이 실제로 돌았는지 함께 적는다 |
| R5 | 라이선스: Spine 에디터 EULA는 에디터 소프트웨어 자체의 역설계를 제한할 수 있다 | 우리 데이터 파일만 분석하고 에디터 바이너리는 손대지 않는다. 사내 배포 전에 EULA 조항을 확인한다 |
| R6 | 원본 파일 손상 | `.bak` 자동 생성, 원자적 저장, 저장 후 자기검증 실패 시 복원(FR-6·FR-8) |

## 13. 빌드·실행

```bash
cmake --preset debug && cmake --build --preset debug && ctest --preset debug
./build/debug/sei info ~/Downloads/spineboy-4.2/spineboy-pro.spine
tools/oracle_export.sh ~/Downloads/spineboy-4.2/spineboy-pro.spine
```
