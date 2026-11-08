#!/usr/bin/env bash
# PoC 판정 묶음: 세 포맷(4.0.07 / 4.3.00 / 4.2.43) 표본에 시나리오별로 주입하고 Spine CLI 오라클로 판정한다.
#   poc/run_checks.sh [결과 폴더]
# 표본 경로는 환경변수로 바꿀 수 있다:
#   SB42=~/Downloads/spineboy-4.2/spineboy-pro.spine   (Spine 4.2.22 저장, 포맷 4.0.07)
#   SB43=~/Downloads/spineboy-4.3/spineboy-pro.spine   (Spine 4.3.75-beta 저장, 포맷 4.3.00)
#   SB4243=research/diff-001-add-event/base.spine     (Spine 4.2.43 CLI 저장, 포맷 4.2.43)
set -uo pipefail
cd "$(dirname "$0")/.."
OUT=${1:-$(mktemp -d "${TMPDIR:-/tmp}/sei-checks.XXXXXX")}
mkdir -p "$OUT"
SB42=${SB42:-$HOME/Downloads/spineboy-4.2/spineboy-pro.spine}
SB43=${SB43:-$HOME/Downloads/spineboy-4.3/spineboy-pro.spine}
SB4243=${SB4243:-research/diff-001-add-event/base.spine}

pass=0; fail=0
run() {   # run <표본> <이름> 인자…
  local src=$1 name=$2; shift 2
  [ -f "$src" ] || { echo "건너뜀 $name (표본 없음)"; return; }
  if python3 -I poc/spine_event_poc.py "$src" "$OUT/$name.spine" "$@" --oracle >"$OUT/$name.log" 2>&1; then
    pass=$((pass+1)); echo "통과 $name"
  else
    fail=$((fail+1)); echo "실패 $name"; tail -5 "$OUT/$name.log" | sed 's/^/    /'
  fi
}

for v in 42 43 4243; do
  eval src=\$SB$v
  [ -n "$src" ] || { echo "건너뜀 $v (SB$v 미지정)"; continue; }
  run "$src" "$v-run8"            --anim run --frame 8
  run "$src" "$v-run2-first"      --anim run --frame 2
  run "$src" "$v-walk30-last"     --anim walk --frame 30
  run "$src" "$v-walk3-values"    --anim walk --frame 3 --int 7 --float 1.5 --string hello
  run "$src" "$v-jump10-beforedef" --anim jump --frame 10
  run "$src" "$v-idle5-newgroup"  --anim idle --frame 5
  run "$src" "$v-aim0-newgroup"   --anim aim --frame 0
  run "$src" "$v-newdef-only"     --event hit --new-event --def-int 3 --def-string boom
  run "$src" "$v-run3-newdef"     --anim run --frame 3 --event hit --new-event --def-float 0.5
  run "$src" "$v-aim2-newdef"     --anim aim --frame 2 --event hit --new-event
done
# 버전별 spineboy 표본(research/versions): 그 버전 CLI 자신으로 판정
VERS=${VERS:-"4.0.64 4.1.24 4.2.00 4.2.22 4.3.00 4.3.03 4.3.04 4.3.05 4.3.26"}
for ver in $VERS; do
  src=research/versions/sb-$ver.spine
  [ -f "$src" ] || src=research/versions/probe/sb-$ver.spine   # 경계 탐색 표본
  run "$src" "v$ver-run8"           --anim run --frame 8 --spine-version $ver
  run "$src" "v$ver-walk3-values"   --anim walk --frame 3 --int 7 --float 1.5 --string hello --spine-version $ver
  run "$src" "v$ver-jump10"         --anim jump --frame 10 --spine-version $ver
  run "$src" "v$ver-idle5-newgroup" --anim idle --frame 5 --spine-version $ver
  run "$src" "v$ver-run3-newdef"    --anim run --frame 3 --event hit --new-event --def-float 0.5 --spine-version $ver
  run "$src" "v$ver-aim2-newdef"    --anim aim --frame 2 --event hit --new-event --spine-version $ver
  run "$src" "v$ver-aim0-newgroup"  --anim aim --frame 0 --spine-version $ver
done
echo "합계: 통과 $pass, 실패 $fail (결과: $OUT)"
[ "$fail" -eq 0 ]
