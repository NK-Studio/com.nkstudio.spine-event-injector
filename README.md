# Spine Event Injector

Spine 이벤트를 Unity 안에서 심는 에디터 창입니다. 메뉴 **Tools ▸ Spine2D ▸ Spine Event Injector**, 또는 Project 창에서 Spine 스켈레톤 데이터를 우클릭해 엽니다.

## 설치

Unity Package Manager의 **Add package from git URL** 에 아래 주소를 넣습니다.

```
https://github.com/NK-Studio/com.nkstudio.spine-event-injector.git
```

### 요구 사항

- Unity 6000.0 이상
- spine-unity 런타임(`spine-csharp`·`spine-unity`·`spine-unity-editor`). spine-unity 는 git 패키지라 이 패키지의 `dependencies` 로 걸 수 없으니 프로젝트에 먼저 설치해 주세요.
- `com.unity.nuget.newtonsoft-json` (자동 설치)

Spine 프로그램은 필요하지 않습니다.

## 다루는 대상

| 대상 | 읽기·쓰기 | 미리보기 |
| --- | --- | --- |
| 프로젝트 안 SkeletonDataAsset · `.skel.bytes` · `.json` (Spine 4.2·4.3 익스포트) | 지원 | SkeletonDataAsset 으로 표시 |
| 프로젝트 밖 `.spine` 원본 (Spine 4.0 ~ 4.3 저장본, 베타 저장본 일부 포함) | 지원 | 프로젝트 옆 이미지 폴더가 있으면 표시 |

스켈레톤이 여럿인 `.spine`은 «애니메이션» 제목 옆 목록에서 편집할 스켈레톤을 고릅니다.

## 저장 정책

- 파일은 **적용하기**를 눌러야 바뀝니다. 쓰기 전에 백업합니다. 프로젝트 밖 파일은 옆에 `.bak`(맨 처음 원본만), 프로젝트 안 파일은 `Library/NKStudio.SpineEventInjector/Backups`에 남습니다.
- `.spine`에 쓰기 전에는 경고를 띄웁니다. Spine에서 그 파일을 열어 둔 채 저장하면 넣은 이벤트가 사라지기 때문입니다.
- 익스포트 데이터에 쓴 이벤트는 원본 `.spine`을 연결해 «원본에 반영»으로 옮길 수 있습니다.

## 폴더

- `Editor/Core` — Unity에 의존하지 않는 코어(`.spine` 그래프 파서·쓰기·내보내기, 익스포트 데이터 코덱, 이벤트 모델)
- `Editor/Schemas`, `Editor/Templates` — `.spine` 포맷 규칙과 이벤트 템플릿
- `Editor/Services`, `Editor/Session`, `Editor/Preview` — 불러오기·저장·원본 연결·미리보기
- `Editor/UI` — UI Toolkit(UXML·USS·C#)
- `Editor/SpineVersionConverter` — Spine 버전 변환기의 코어(`.skel.bytes` 읽기·쓰기에 사용)

## 라이선스

MIT. 글꼴과 Material 아이콘의 라이선스는 `Editor/UI/Fonts`, `Editor/UI/Icons`의 NOTICE 파일을 따릅니다.
