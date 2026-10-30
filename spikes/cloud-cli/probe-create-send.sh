#!/usr/bin/env bash
# Spike: create a Claude Code cloud session and send it a message through the
# undocumented /v1/sessions API, using your claude.ai login. Prints only the
# session id and the assistant's replies; never prints the token.
set -euo pipefail

API=https://api.anthropic.com
ENV_ID=env_011v4b3WBhRMaGGti6Qw747c   # environment the first probe session ran in

read_json() { python -c "import json,os,sys;d=json.load(open(os.path.expanduser(sys.argv[1])));print(eval(sys.argv[2]))" "$@"; }
TOKEN=$(read_json '~/.claude/.credentials.json' "d['claudeAiOauth']['accessToken']")
ORG=$(read_json '~/.claude.json' "d.get('oauthAccount',{}).get('organizationUuid','')")
HDRS=(-H "Authorization: Bearer $TOKEN" -H "anthropic-version: 2023-06-01"
      -H "anthropic-beta: ccr-byoc-2025-07-29" -H "x-organization-uuid: $ORG"
      -H "content-type: application/json")

user_event() { # $1 = text; prints a user event in the shape the CLI sends
  python -c "import json,sys,uuid;print(json.dumps({'uuid':str(uuid.uuid4()),'session_id':'','type':'user','parent_tool_use_id':None,'message':{'role':'user','content':sys.argv[1]}}))" "$1"
}

replies() { # $1 = session id; prints assistant text from the newest 50 events
  curl -s "$API/v1/sessions/$1/events?limit=50" "${HDRS[@]}" | python -c "
import json,sys
for e in json.load(sys.stdin).get('data',[]):
    if e.get('type')=='assistant':
        for c in e['message'].get('content',[]):
            if c.get('type')=='text': print('  assistant:', c['text'])"
}

echo "== 1. create session"
FIRST=$(user_event "Fyrd API probe. Do not use any tools. Reply with exactly: FYRD-CREATE-OK")
CREATE=$(curl -s -w '\n%{http_code}' -X POST "$API/v1/sessions" "${HDRS[@]}" -d @- <<EOF
{"title":"Fyrd API probe 2","tags":["fyrd-probe"],"environment_id":"$ENV_ID",
 "events":[{"type":"event","data":$FIRST}],
 "session_context":{"sources":[],"outcomes":[],"environment_variables":{},
   "model":"claude-haiku-5-5","append_system_prompt":"You are a Fyrd probe peon."}}
EOF
)
CODE=$(tail -n1 <<<"$CREATE"); BODY=$(sed '$d' <<<"$CREATE")
echo "HTTP $CODE"
if [[ "$CODE" != 200 && "$CODE" != 201 ]]; then echo "$BODY" | head -c 800; exit 1; fi
SID=$(python -c "import json,sys;print(json.load(sys.stdin)['id'])" <<<"$BODY")
echo "session: $SID  (https://claude.ai/code/$SID)"

echo "== 2. wait 60s, then read status + replies"
sleep 60
curl -s "$API/v1/sessions/$SID" "${HDRS[@]}" | python -c "
import json,sys;d=json.load(sys.stdin);m=d.get('external_metadata',{})
print('  status:', d.get('session_status') or d.get('status'), '| summary:', m.get('post_turn_summary',{}).get('status_category'))"
replies "$SID"

echo "== 3. send a follow-up event"
NEXT=$(user_event "Second probe message. Do not use any tools. Reply with exactly: FYRD-SEND-OK")
curl -s -w '  HTTP %{http_code}\n' -o /dev/null -X POST "$API/v1/sessions/$SID/events" "${HDRS[@]}" \
  -d "{\"events\":[$NEXT]}"

echo "== 4. wait 45s, then read replies"
sleep 45
replies "$SID"
