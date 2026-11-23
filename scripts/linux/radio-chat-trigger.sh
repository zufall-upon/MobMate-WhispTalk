#!/bin/sh
# MobMate Radio Chat trigger for desktop shortcuts / Steam Input.
set -eu

API="${MOBMATE_API:-http://127.0.0.1:5000}"
ACTION="${1:-show}"

post_json() {
    if command -v curl >/dev/null 2>&1; then
        curl -fsS -X POST "$API$1" -H 'Content-Type: application/json' -d "$2"
        return
    fi
    python3 - "$API$1" "$2" <<'PY'
import sys, urllib.request
url, body = sys.argv[1], sys.argv[2].encode()
req = urllib.request.Request(url, data=body, headers={'Content-Type':'application/json'}, method='POST')
sys.stdout.write(urllib.request.urlopen(req, timeout=10).read().decode())
PY
}

get_json() {
    if command -v curl >/dev/null 2>&1; then
        curl -fsS "$API$1"
        return
    fi
    python3 - "$API$1" <<'PY'
import sys, urllib.request
sys.stdout.write(urllib.request.urlopen(sys.argv[1], timeout=10).read().decode())
PY
}

usage() {
    cat <<'EOF'
Usage: radio-chat-trigger.sh <command> [args]

Commands:
  show [durationMs]    Show Radio Chat overlay (default: 2500ms)
  hide                 Hide Radio Chat overlay
  toggle               Toggle Radio Chat overlay
  status               Print /api/radio/status
  speak <page> <digit> Speak slot (page 0..2, digit 1..9; page -1 for pending)

Environment:
  MOBMATE_API          API base URL (default: http://127.0.0.1:5000)
EOF
}

case "$ACTION" in
    show)
        duration="${2:-2500}"
        case "$duration" in *[!0-9]*|'') duration=2500 ;; esac
        post_json /api/radio/preview "{\"show\":true,\"durationMs\":$duration}"
        ;;
    hide)
        post_json /api/radio/preview '{"show":false}'
        ;;
    toggle)
        status="$(get_json /api/radio/status || true)"
        case "$status" in
            *'"overlayVisible":true'*) post_json /api/radio/preview '{"show":false}' ;;
            *) post_json /api/radio/preview '{"show":true,"durationMs":2500}' ;;
        esac
        ;;
    status)
        get_json /api/radio/status
        ;;
    speak)
        page="${2:-}"
        digit="${3:-}"
        case "$page:$digit" in
            -*:[1-9]|[0-2]:[1-9]) ;;
            *) usage >&2; exit 2 ;;
        esac
        post_json /api/radio/speak "{\"page\":$page,\"digit\":$digit}"
        ;;
    -h|--help|help)
        usage
        ;;
    *)
        usage >&2
        exit 2
        ;;
esac
