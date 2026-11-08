# .spine 바이너리 프로젝트 포맷 선행 사례 조사 (2026-11-08)
> 조사 방법: GitHub 저장소·코드·이슈, Esoteric 포럼, 영어권·중국어·일본어·한국어 커뮤니티, 주변 도구를 6갈래로 검색(원시 143건, 중복 제거 124건)하고, 관련도 2 이상 78건은 저장소 파일·글 전문을 직접 읽어 검증했다. 코드는 읽기만 했고 클론·실행하지 않았다. 핵심 저장소(Anim2D, spine233-file-parser, openbone, Spine-Forge-X)의 존재·최근 활동·라이선스는 별도로 다시 확인했다(Anim2D·spine233은 LICENSE 파일과 README에 MIT 명시).


## 1. 한 줄 결론

`.spine` 바이너리를 직접 읽고 쓰는 공개 사례는 세 곳뿐입니다. 이벤트 키나 이벤트 정의를 새로 넣으면서 참조 id를 다시 매기는 구현은 어디에도 없습니다.
- **읽기 + 쓰기:** neko233-com/spine233-file-parser 하나입니다. 같은 크기 값 덮어쓰기와 마지막 애니메이션 삭제만 합니다. 키, 타임라인, 이벤트 추가는 "객체 그래프 전체 재번호가 필요하다"며 일부러 거부합니다.
- **스키마 기반 읽기 전용:** turtlegameworks/Anim2D(3.8과 4.2만)입니다.
- **헤더나 설정만 읽기:** Spine-Forge-X(3.8 설정), openbone(헤더)입니다.

그대로 가져다 쓸 만한 코드는 없습니다. 바이트 사실 교차검증, 테스트 샘플 확보, 함정 목록 정도가 쓸모입니다.

## 2. 분류표

| 이름(링크) | 무엇을 하나 | .spine 처리 방식 | 버전 | 활동성 | 우리에게 쓸모 |
|---|---|---|---|---|---|
| [neko233-com/spine233-file-parser](https://github.com/neko233-com/spine233-file-parser) | Go 라이브러리. raw-DEFLATE를 풀었다가 다시 압축합니다(압축 해제한 payload는 바이트 단위로 같음). 바이트 시그니처를 스캔해 애니메이션 맵, 본, 타임라인, 이벤트 타임라인과 정의를 찾습니다. | 읽기+쓰기. 쓰기는 같은 크기 float 덮어쓰기, 이름 문자열 교체, 마지막 애니메이션 삭제뿐입니다. 삽입은 거부합니다. | 어댑터 키: 4.2.43, 4.3.6/.8/.11/.14/.15/.17/.19/.23/.26. 4.3.x는 4.3.26을 빼면 모두 같은 휴리스틱으로 넘깁니다. 4.0/4.1은 없습니다. | 2026-07 생성, 2026-10-10 마지막 push, 커밋 33개, 스타 1개 | 중. 바이트 상수를 대조하는 용도입니다. 휴리스틱이고 특정 에셋에 과적합되어 있어 파싱 전략으로는 채택하지 않습니다. MIT. |
| [neko233-com/spine233-agent-cli](https://github.com/neko233-com/spine233-agent-cli) | 위 라이브러리를 감싼 CLI와 MCP(도구 22개). 미리보기를 먼저 하고, 다시 풀어 payload가 바이트 단위로 같은지 확인합니다. 입력 파일은 덮어쓰지 않습니다. | 읽기+쓰기(라이브러리와 같은 제한) | 데모 파일은 4.3.0 | 2026-07-23 하루만 활동, 스타 2개 | 낮음~중. 안전장치 패턴을 참고할 수 있습니다. 4.3.0 샘플(hero, alien, raptor)도 있습니다. |
| [turtlegameworks/Anim2D spine_project](https://github.com/turtlegameworks/Anim2D/tree/main/src/anim2d/formats/spine_project) | Python 클린룸 리더. 버전별 클래스 id와 필드 태그 테이블(schema38/42)로 Kryo 그래프를 파싱해 Spine JSON으로 바꿉니다. | 읽기 전용 | 3.8, 4.2. 4.2는 4.2.43 프로젝트 하나로만 테스트했습니다. 4.0/4.1/4.3은 거부합니다. | 2026-10-22 공개, 2026-11-05 마지막 push | **가장 높음.** 문법과 스키마를 교차검증할 수 있는 유일한 자료입니다. 4.2 이벤트 키는 디코딩하지 못합니다. MIT. |
| [cuongcuongmanh7/Spine-Forge-X](https://github.com/cuongcuongmanh7/Spine-Forge-X) (`spine_project.rs`) | 3.8 export와 packAtlas 설정을 바이트 패턴으로 읽습니다. 버전 문자열은 3.8과 4.x 모두에서 읽습니다. 일괄 export와 업그레이드는 CLI로 합니다. | 읽기 전용(휴리스틱) | 설정은 3.8.x만, 버전 문자열은 3.8.99와 4.3.17에서 관찰 | 2026-06 ~ 2026-07, 스타 0개 | 낮음~중. zigzag 정수와 헤더 패턴의 근거입니다. MIT. |
| [Samlinb/openbone](https://github.com/Samlinb/openbone) (`SpineNativeHeader.java`) | 헤더만 검사합니다. 객체 그래프는 디코딩하지 않습니다. 변환은 사용자가 지정한 CLI로 합니다. | 헤더 읽기 + CLI 경유 | 헤더는 버전 무관. 대상 버전은 3.8.99, 4.0.64, 4.1.24, 4.2.22, 4.3.26 | 2026-10 생성, 스타 1개 | 낮음. 헤더 구조를 확인하는 근거입니다. 라이선스가 없으므로 사실만 참고합니다. |
| [rig-c/core #540](https://github.com/rig-c/core/issues/540) | inflate한 다음 float32 패턴을 grep해서 import 손실인지 export 손실인지 가릅니다. 구조는 해석하지 않습니다. | 읽기(grep) + CLI | 4.3.26 | 2026-09 | 낮음. 함정을 알려 줍니다(아래 4장). |
| [yuyueplus/spine-mcp](https://github.com/yuyueplus/spine-mcp) | 본체는 JSON 편집 후 CLI로 import합니다. native-bridge는 난독화된 에디터 클래스를 리플렉션으로 호출하는 Java agent입니다. | CLI 경유 + 에디터 내부 호출 | 4.3.23 한 빌드(해시 고정) | 2026-11 | **native-bridge는 우리 정책(디컴파일 금지)과 충돌합니다. 참고하지 않습니다.** CLI 오라클 설계만 참고합니다. |
| CLI 왕복 MCP와 도구들: [UmutFarukDilli/spine-mcp](https://github.com/UmutFarukDilli/spine-mcp), [zhoushengmin/spine-mcp-server](https://github.com/zhoushengmin/spine-mcp-server), [Attrom/spine-mpc-attrom](https://github.com/Attrom/spine-mpc-attrom), [1425sd/spine-MCP](https://github.com/1425sd/spine-MCP), [sathyarajshetigar/spine-cli](https://github.com/sathyarajshetigar/spine-cli), [egorfedorov/spine-mcp](https://github.com/egorfedorov/spine-mcp), [kumikilongyeyo/gpt-spine-mcp](https://github.com/kumikilongyeyo/gpt-spine-mcp), [nihatcagri44/spine-motion-mcp](https://github.com/nihatcagri44/spine-motion-mcp), [hoycdanny/kiro-spine2d-accelerator](https://github.com/hoycdanny/kiro-spine2d-accelerator), [claudeskydream-hash/sky-spine-mcp](https://github.com/claudeskydream-hash/sky-spine-mcp), [slowchop/spinal phase0a](https://github.com/slowchop/spinal/tree/main/tools/spinal-phase0a), [youtiaoguagua/animation-2d](https://github.com/youtiaoguagua/animation-2d/blob/3335a8f0aff9b7fc2a0b8be48f9e26157cc555ba/server/api/spine-project.js) | JSON export → JSON 편집(일부는 이벤트 추가) → `-i x.json -o y.spine -r` 순서로 처리합니다. 대부분 .spine이 바이너리이고 문서화되지 않았다고 명시합니다. | CLI 경유 | 3.8.75 ~ 4.3.26 | 대부분 2026년 하루~수일짜리 신규 저장소 | 낮음. 대체 경로이자 CLI 오라클 레시피, 함정 목록으로만 씁니다. |
| 일괄 export와 복원: [spine-scripts](https://github.com/EsotericSoftware/spine-scripts)(공식), [dreamstring/SpineForge](https://github.com/dreamstring/SpineForge), [HeartPhase/Spine-Batch-Update](https://github.com/HeartPhase/Spine-Batch-Update), [lixxix/spinetool](https://github.com/lixxix/spinetool), [tung28111999/Spine-Upgrade-Pipeline-TUN](https://github.com/tung28111999/Spine-Upgrade-Pipeline-TUN), [magjangin/asset-tool](https://github.com/magjangin/asset-tool/blob/f50bbb24d9b3967618f7624127cd6cb481174e19/AssetTool.Core/SpineRenderer.cs), [RedrawSpine](https://github.com/Zhangyangrui916/RedrawSpine/blob/01fdd849311c36ce6972e484d80f7225721d212b/spine2restposepsd.py), [Genshin-Tools SpineAuto](https://github.com/Dongyifengs/Genshin-Tools/blob/40b8d4577e4d401be667e102cff2c5ff1e1abf6f/SpineAuto/SpineAuto.py), [Qiita 글](https://qiita.com/iris23562/items/e2c6282e7a9457740b04), [Live2DHub 4540](https://live2dhub.com/t/topic/4540) | CLI로 일괄 export하거나 json/skel을 .spine으로 복원합니다. | CLI 경유 | 3.6 ~ 4.3 | 다양함 | 샘플 파일 출처로 쓸모 있습니다(4장). |
| 유료 도구: [Gumroad Batch Tools](https://zlpkurama.gumroad.com/l/batchtools), [Bilibili BV1EM4m167xE](https://www.bilibili.com/video/BV1EM4m167xE/), [BV1xk4y1x7nQ](https://www.bilibili.com/video/BV1xk4y1x7nQ/) | 비공개 스크립트이며 CLI 래퍼로 추정됩니다. "버전 변경"은 JSON에만 적용합니다. | CLI 경유(추정) | 4.0.64, 4.1.23 | 2023~2024 | 없음. 구매하거나 실행하지 않습니다. |
| GUI 자동화: [포럼 8833 AutoHotkey](https://it.esotericsoftware.com/forum/d/8833) | 키 입력 매크로로 키프레임을 만듭니다. | GUI 자동화 | 3.6~3.8 시기 | 휴면 | 반면교사입니다. 타이밍에 따라 결과가 흔들립니다. |
| 공식 이슈: [spine-editor #16](https://github.com/EsotericSoftware/spine-editor/issues/16), [#379](https://github.com/EsotericSoftware/spine-editor/issues/379) | #16은 CLI JSON import(3.7.17-beta부터)입니다. #379는 CLI 애니메이션 import로, `--from/--to/-a/--replace/-r` 옵션이 4.3.73-beta에 들어갔고 `--merge`는 4.3.74-beta에 들어갔습니다. | CLI 경유 | 3.7.17+, 4.3.73-beta+ | #379는 2026-04-25에 종료 | 중. 4.3.73 이상에서 쓸 수 있는 공식 대체 경로이자 오라클입니다. |

## 3. Esoteric 공식 입장

저작권 규칙 때문에 원문 인용은 한 개만 넣고 나머지는 요지만 옮겼습니다. 원문은 링크에서 확인할 수 있습니다.

- **포맷이 문서화되어 있는가:**
  - 2020년 Nate는 이 파일이 "not a file format with a documented structure"라고 했습니다. 구조는 에디터의 메모리 자료구조를 그대로 따르고 버전마다 바뀌며, Spine 안에도 고정 포맷 파서가 따로 없다고 덧붙였습니다. ([13885](https://esotericsoftware.com/forum/d/13885))
  - 2013년에는 문서도 버전 표기도 없는 바이너리이고 에디터 전용이라고 했습니다. ([539](https://esotericsoftware.com/forum/d/539-best-approach-for-converting-to-spine-format)) "버전 표기 없음"은 지금 파일 헤더에 버전 문자열이 있는 것과 맞지 않으므로 당시 사정으로 봐야 합니다.
- **안정성과 호환성:**
  - 이후 모든 버전이 옛 프로젝트를 열 수 있어야 해서 구조가 복잡하고 바이너리로 유지한다고 했습니다. 사람이 읽는 형식은 export 데이터의 몫이라고 했습니다. ([2196](https://esotericsoftware.com/forum/d/2196-spine-project-file-binary-or-text), [2780](https://esotericsoftware.com/forum/d/2780-request-save-project-file-in-ascii-option), 2014)
  - 포맷이 매우 복잡하고, JSON으로는 참조(reference)를 저장할 수 없다고 했습니다. ([18145](https://esotericsoftware.com/forum/d/18145-project-versioning), 2022)
  - 구버전용 프로젝트 저장은 기술적으로 매우 어렵다고 했습니다. 빠른 개발을 가능하게 하는 방식이 바로 그 이유라고 설명했습니다. ([15004](https://esotericsoftware.com/forum/d/15004-feature-request-save-in-older-version), 2020)
  - 바이너리를 직접 편집하는 것은 현실적이지 않다며 JSON → CLI import를 권했습니다. 이 경우 본 아이콘, export 설정, 선택 그룹이 사라지고 새 skeleton으로 들어간다고 했습니다. ([15614](https://esotericsoftware.com/forum/d/15614), 2021)
  - export한 데이터로는 프로젝트를 손실 없이 복원할 수 없고, import는 같은 버전에서만 가능하다고 했습니다. ([28308](https://esotericsoftware.com/forum/d/28308-can-a-spine-binary-file-be-fully-restored-to-its-original-project-file), 2025)
- **스크립팅 API:**
  - 공개 API는 유지보수 부담이 커서 CLI를 먼저 만들겠다고 했습니다. ([3147](https://esotericsoftware.com/forum/d/3147-feature-request-batch-scripting), 2014)
  - 문서화된 스크립팅 API와 플러그인은 단기간에는 어렵다고 했습니다. 아이디어 수준으로 "실행 중인 에디터를 조작하는 CLI"와 REST API를 언급했습니다. ([23924](https://esotericsoftware.com/forum/d/23924), [24134](https://esotericsoftware.com/forum/d/24134-why-cant-spine-editor-support-plug-in-systems), 2023)
  - Misaki는 에디터 전반을 개선한 뒤에 스크립팅과 플러그인을 검토할 수도 있다고 했습니다. ([29275](https://esotericsoftware.com/forum/d/29275), 2025-10)
  - [26424](https://esotericsoftware.com/forum/d/26424-planned-script-support-for-spine-tools), [28210](https://esotericsoftware.com/forum/d/28210-any-time-saving-scripts-for-animators-in-editor)의 답변은 Spinebot(자동 응답 봇)이 쓴 것이므로 공식 발언으로 인용하지 않습니다.
- **변경 이력([changelog](https://esotericsoftware.com/spine-changelog), [archive](https://esotericsoftware.com/spine-changelog/archive)):**
  - 프로젝트 크기가 줄었거나 직렬화가 바뀐 버전: 3.8.47, 4.2.21, 4.3.03, 4.3.04(모두 stable), 4.3.67-beta
  - 4.3.09-beta에 `--extra-project-checks` 추가
  - 4.3.91-beta에서 로드, 저장, import 때의 무결성 검사 강화
  - 1.8.16부터 고아(orphan) 이벤트를 로드할 때 정리
  - 4.0.17부터 잘못된 애니메이션이 있으면 저장 거부
  - 4.0.14-beta부터 `-r`의 입력과 출력이 같으면 다시 저장(resave)

## 4. 자동 학습기 계획에 주는 시사점

**교차검증할 포맷 사실 (모두 디컴파일 없이 저장 파일에서 추론한 것):**
- 파일 전체가 하나의 raw-deflate 스트림이고 래퍼가 없습니다. 여러 출처에서 독립적으로 확인됐습니다.
- 헤더 구조(openbone 기준): varint 형식 번호(13~1024, fixture에서는 72) → int32 `0xB020B020` → Kryo 버전 문자열.
  - Spine-Forge-X가 관찰한 값은 `47 B0 20 B0 20 "4.3.1"|B7`이고, 문자열은 4.3.17입니다.
  - **이 문자열이 에디터 패치 버전인지 우리가 쓰는 "4.3.00" 같은 포맷 문자열인지 확인해야 합니다.**
- 참조 마커(Anim2D): 0 = null, 1 = new, n≥2 = 객체 id n-2로의 역참조. 다형 값은 class id 다음에 참조 마커가 옵니다.
- **4.2에서는 참조를 두 공간으로 따로 번호 매깁니다.** 값 공간(문자열, enum, Color, List, 배열 등)과 엔티티 공간입니다. 일부 필드의 문자열과 enum은 참조 추적 대상이 아닙니다. 재번호 설계에 직접 영향을 줍니다.
- 인코딩:
  - 정수는 zigzag(`optimizePositive=false`)인 경우와 일반 varint인 경우가 섞여 있습니다. Anim2D는 Event int, EventKey int를 zigzag로 봅니다.
  - float는 BE f32입니다.
  - 문자열은 마지막 바이트의 최상위 비트를 켜는 ASCII 형식이거나, UTF-8 길이 varint 형식입니다.
- 이벤트 스키마(Anim2D, 3.8과 4.2 테이블이 같음. 4.2는 3.8을 복사한 것이라 미검증):
  - Event: class 0x0a. 태그 2 이름, 3 int, 4 float, 5 string, 6 audio, 8 volume, 9 balance
  - EventKey: class 0x23. 태그 1 Event 참조
  - Skeleton 태그 0xa = 이벤트 정의 목록
  - 4.2 Timeline/Key는 class 132/133
- spine233 바이트 상수(4.2/4.3):
  - 타임라인 `84 01 01`, 키 `85 01 01`, 이벤트 타임라인 타입 12
  - 4.3.23 이벤트 정의 prefix `23 01 0a 02 01`, suffix `03 00 0a 6c`
  - 첫 wire ref id = 4
  - 객체 종료 `7e 00`
  - 애니메이션 맵이 payload 끝에 있습니다.
- 이벤트 키 시간은 초가 아니라 **프레임 번호**입니다. spine233은 30fps를 하드코딩해 변환하지만 Dopesheet FPS를 따라야 합니다.
- export와 packer 설정 객체도 그래프 안에 들어 있습니다(Kryo 오류 로그 [10687](https://esotericsoftware.com/forum/d/10687-error-while-saving-)). 스키마가 이 객체들을 건너뛸 수 있어야 합니다.

**버전 관련:**
- 패치 버전 사이에서도 직렬화가 바뀝니다. 4.3에서는 4.3.03/04 전후와 beta들이 다르고, spine233은 4.3 안에서 legacy v1~v5 레이아웃을 구분합니다. 따라서 패치 단위 스키마가 필요합니다.
- changelog상 버전 목록:
  - 4.0.00~4.0.64 (.12, .32, .42, .63 없음)
  - 4.1.00~4.1.24
  - 4.2.00~4.2.43 (.05, .07, .10, .16, .41 없음)
  - 4.3.00~4.3.26
  - 그 밖에 beta 다수
- 확보할 수 있는 샘플:
  - SpineForge `Examples/`(spine-runtimes 공식): 4.0.07, 4.1.04-beta, 4.1.17, 4.1.20, 4.2.21, 4.2.32-beta, 4.2.56-beta, 4.2.61-beta. owl-pro는 헤더가 "0.0.0"인 이상 사례입니다.
  - spine-runtimes 4.3 브랜치: 4.3.0
  - RedrawSpine c821_00: 약 4.1.23. 라이선스가 없으므로 저장소에 넣지 않고 로컬 확인용으로만 쓴다.
  - TV_screen: 3.8.9
- coin-pro.spine의 버전은 출처마다 다릅니다. "4.2 예제"라는 기록과 "inflate해 보니 4.0.0"이라는 기록이 있어 직접 확인이 필요합니다.

**함정:**
- 에디터는 로드와 저장 때 무결성을 검사합니다. 끊어진 참조가 있으면 저장에 실패하고(Nate, [6451](https://esotericsoftware.com/forum/d/6451-error-saving-project)), 고아 이벤트는 정리되며, 이벤트 폴더 항목도 검사합니다.
- 그래서 이벤트 정의는 폴더 구조에도 등록해야 할 가능성이 있습니다. 주입한 파일은 열기 → 다시 저장 → `--extra-project-checks`까지 거쳐 검증해야 합니다.
- 4.3.26은 왕복 과정에서 애니메이션을 이름순으로 다시 정렬합니다. slider의 애니메이션 참조가 순번(ordinal) 기반이라 영향을 받습니다.
- CLI import는 매번 바이트가 달라집니다(내부 id에 무작위 값). 바이트 단위 골든 비교는 쓸 수 없고 구조나 JSON으로 비교해야 합니다.
- CLI 관련:
  - `-r`이 없으면 도움말만 출력하고 종료 코드 0으로 끝납니다. `--replace`가 없으면 skeleton이 name2 같은 이름으로 추가됩니다.
  - 에디터 창이 열려 있으면 멈출 수 있고, Trial 라이선스는 쓰기가 막힙니다.
  - `-u`로 버전을 고정하지 않으면 마지막으로 실행한 버전으로 저장될 수 있습니다.
  - 4.3.38-beta 전까지는 CLI export가 일부 자동 수정(삼각분할)을 하지 않았습니다.

**접근법 평가:**
- 공개된 어느 프로젝트도 참조 재번호를 동반한 삽입을 구현하지 않았습니다. spine233은 이 부분에서 멈췄습니다. 우리가 이 지점에서 앞서 있다는 뜻이고, 동시에 이 부분이 가장 어렵다는 사실도 확인됩니다.
- 4.3.73-beta 이상에서는 공식 대체 경로가 있습니다. `-i edited.json -o original.spine --to <skel> -a <anim> --replace -r` 형태입니다. 다만 다음이 확인되지 않았습니다.
  - 이벤트 정의가 함께 넘어오는지
  - 애니메이션 단위로 통째로 교체할 때 무엇이 손실되는지
  - 옵션이 문서에 없고 4.3.74에서 이미 바뀌었다는 점
- 4.0~4.2와 4.3.73 미만에는 이 경로가 없으므로 바이너리 경로가 계속 필요합니다. 이 경로는 오라클로 활용할 수 있습니다.
- CLI의 `-i`(info, 이벤트 정의 이름과 개수 출력), `-e json` 재export, `-u <정확한 버전>`을 이용한 버전별 참조 파일 생성은 모두 허용된 오라클 범위 안입니다.
- **정책 충돌:** yuyueplus native-bridge는 난독화된 에디터 클래스를 리플렉션으로 호출하므로 배제합니다. 그 밖의 항목은 모두 블랙박스 관찰이나 CLI만 쓰므로 문제가 없습니다.

## 5. 못 찾은 것 / 검색 한계

- 4.0과 4.1 바이너리 스키마를 다룬 공개 사례는 없습니다. 4.3 스키마 기반 리더도 없습니다(spine233은 휴리스틱).
- 4.2와 4.3의 EventKey가 타임라인 안에서 어떤 레이아웃인지 스키마 수준으로 기술한 자료가 없습니다.
- 이벤트 정의를 폴더나 Skeleton에 등록하는 방식을 다룬 자료가 없습니다.
- 4.3.73+ CLI 애니메이션 import가 이벤트 정의를 가져오는지 아무도 검증하지 않았습니다.
- 헤더 버전 문자열(에디터 버전)과 우리의 포맷 문자열이 어떤 관계인지 확정하지 못했습니다.
- 검색 한계:
  - Esoteric 포럼은 JS로 렌더링되어 Flarum API로 읽었습니다. 일부 항목은 요약 모델을 거친 인용입니다.
  - naver 블로그는 차단됐고, Zhihu와 CSDN은 403/521을 반환해 스니펫만 확인했습니다. Gitee API는 빈 결과였습니다.
  - GitHub 코드 검색은 따옴표와 플래그 처리 문제로 누락이 있을 수 있습니다.
  - 유료와 비공개 도구는 내부를 확인하지 않았습니다.
  - 코드는 읽기만 했고 클론하거나 실행하지 않았습니다.

## 6. 이 조사가 «미확인»으로 남긴 것 중 우리 실험으로 이미 답한 것

| 질문 | 우리 결과 | 근거 |
| --- | --- | --- |
| 헤더 버전 문자열 = 에디터 패치 버전인가 | **그렇다.** 4.0.64·4.1.24·4.2.00·4.2.43·4.3.00·4.3.26 CLI가 저장한 파일의 문자열이 각각 그 버전. 단 같은 `4.3.00` 문자열이라도 4.3.75-beta 저장본과 4.3.00 정식 저장본의 구조가 다르다 → 문자열만으로 스키마를 고를 수 없다 | `docs/FORMAT_NOTES.md`, 버전별 spineboy 표본 |
| 4.2·4.3의 이벤트 키 레이아웃 | 해독 완료: 4.3식 고정 배치 키(클래스 133)의 종류 12 → Event(1 정의, 2 int zigzag, 3 float, 4 string, 5·6), 타임라인 추가 객체 = EventData | `docs/POC_RESEARCH_SPEC.md` 5-7 |
| 이벤트 정의를 폴더·스켈레톤에 등록하는 방식 | Skeleton.10 정의 목록 + 이벤트 폴더(정의 필드 10)의 자식 목록 둘 다에 등록. 4.2.43에서 Spine 저장본과 **바이트 일치** | `research/diff-001-add-event`, POC_RESEARCH_SPEC 7-2 |
| 참조 재번호를 동반한 삽입 | 구현·검증 완료(그래프 재직렬화). 4.0.07·4.2.43·4.3.00 × 10개 시나리오 30건 오라클 통과, 에디터 화면 확인 | `poc/run_checks.sh` |
| 4.3.73+ CLI 애니메이션 import가 이벤트 정의를 가져오나 | **안 가져온다.** 4.3.26에서 `Event not found` 경고만 내고 키를 버린 채 «Complete». 스켈레톤 `--merge`로도 안 넘어온다. 4.2.43 이하에서는 `-a`·`--replace`가 무시되고 스켈레톤이 통째로 추가된다 | POC_RESEARCH_SPEC 8-2 |
| 4.3.26 저장본은 암호화인가 | 아니다. 문자열이 그대로 보이고 압축 특성도 4.3.00과 비슷하다. 포맷 문자열 뒤에 16바이트 값이 추가될 뿐 | 이번 세션 측정 |
| 4.0·4.1에서 CLI가 이 Mac(Apple Silicon)에서 도는가 | 4.0.64·4.1.24 모두 실행되고 spineboy 표본 생성 성공(3.8은 x86 런처 문제로 실패) | 이번 세션 |

## 7. 우리 계획에 바로 쓸 것

1. **Anim2D `schema38.py`·`schema42.py`(MIT)**: 클래스 id·필드 태그 표를 우리 4.2.43 스키마와 대조해 교차검증한다(코드를 가져오지 않고 사실만 대조).
2. **spine233-file-parser의 4.3.x 레이아웃 구분(legacy v1~v5)과 바이트 상수(MIT)**: 4.3 패치 버전 사이 경계 후보로 쓴다. 자동 학습기의 이분 탐색 출발점.
3. **changelog의 직렬화 변경 버전**(3.8.47, 4.2.21, 4.3.03, 4.3.04, 4.3.67-beta): 스키마 경계 후보.
4. **검증 단계 보강**: 결과 파일을 `--extra-project-checks`(4.3.09-beta+)와 «열기 → 다시 저장»으로도 확인한다. CLI 왕복 결과는 내부 id가 매번 달라 바이트 비교 대신 JSON·구조 비교를 쓴다.
5. **배제**: yuyueplus/spine-mcp의 native-bridge(난독화된 에디터 클래스를 리플렉션 호출) — 우리 원칙(에디터 디컴파일·내부 호출 금지)과 충돌.
