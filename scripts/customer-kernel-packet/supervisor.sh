set -eu
[ "$#" -ge 5 ] || exit 78
deadline="$1"; phase="$2"; token="$3"; builddir="$4"; shift 4
case "$deadline:$phase" in *[!0-9:]*) exit 78;; esac
[ "${#token}" -eq 64 ] || exit 78
case "$token" in *[!a-f0-9]*) exit 78;; esac
[ "$builddir" = /work/repo ] || exit 78
for tool in timeout date cat mkdir sleep; do command -v "$tool" >/dev/null || exit 78; done
now=$(date -u +%s); remaining=$((deadline-now))
[ "$remaining" -gt 0 ] || exit 124
export CUSTOMER_ABSOLUTE_DEADLINE="$deadline" CUSTOMER_PHASE_SECONDS="$phase" CUSTOMER_READY_TOKEN="$token" CUSTOMER_BUILD_DIRECTORY="$builddir"
exec timeout --signal=TERM --kill-after=5s "${remaining}s" /bin/sh -c '
set -eu
control="${CUSTOMER_CONTROL_DIRECTORY:?}"
mkdir -p "$control"
while [ ! -f "$control/build.ready" ]; do
    [ "$(date -u +%s)" -lt "$CUSTOMER_ABSOLUTE_DEADLINE" ] || exit 124
    sleep 0.1
done
[ ! -L "$control/build.ready" ] || exit 78
[ "$(cat "$control/build.ready")" = "$CUSTOMER_READY_TOKEN" ] || exit 78
now=$(date -u +%s); remaining=$((CUSTOMER_ABSOLUTE_DEADLINE-now))
[ "$remaining" -gt 0 ] || exit 124
budget="$CUSTOMER_PHASE_SECONDS"
[ "$remaining" -ge "$budget" ] || budget="$remaining"
cd "$CUSTOMER_BUILD_DIRECTORY"
exec timeout --signal=TERM --kill-after=5s "${budget}s" "$@"
' customer-build-gate "$@"
