#!/usr/bin/env bash
# End-to-end scenario on a real Jellyfin 12.3 (Docker):
#   install the plugin from a repository -> add APIs through the wizard endpoints -> the "Live Hub" channel and the
#   Live TV channels/guide appear -> a stream opens through the relay and ffmpeg produces HLS segments.
# Usage: tests/integration/run.sh <artifacts-dir containing livehub_*.zip>
set -euo pipefail

ARTIFACTS="${1:-artifacts}"
HERE="$(cd "$(dirname "$0")" && pwd)"
COMPOSE=(docker compose -f "$HERE/docker-compose.yml")
JF=http://127.0.0.1:18096
PASS="$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 20)"   # throw-away test credential
AUTH='MediaBrowser Client="LiveHubIT", Device="ci", DeviceId="livehub-it", Version="1"'
TOKEN=""
fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok - $*"; }

jf() { # method path [json]
  local method="$1" path="$2" body="${3:-}"
  local hdr="$AUTH"; [ -n "$TOKEN" ] && hdr="$AUTH, Token=\"$TOKEN\""
  if [ -n "$body" ]; then
    curl -fsS -X "$method" "$JF$path" -H "Authorization: $hdr" -H 'Content-Type: application/json' -d "$body"
  else
    curl -fsS -X "$method" "$JF$path" -H "Authorization: $hdr"
  fi
}

# ---- repository: manifest generated for the downloaded zip, served by nginx at http://repo
ZIP="$(ls "$ARTIFACTS"/livehub_*.zip | head -n1)"
[ -f "$ZIP" ] || fail "no plugin zip in $ARTIFACTS"
VERSION="$(basename "$ZIP" .zip | sed 's/^livehub_//')"
mkdir -p "$HERE/repo"; cp "$ZIP" "$HERE/repo/"
MD5="$(md5sum "$ZIP" | cut -d' ' -f1)"
GUID="19816826-9c4f-49a5-9222-e00484264789"
jq -n --arg guid "$GUID" --arg v "$VERSION" --arg sum "$MD5" --arg url "http://repo/$(basename "$ZIP")" \
  '[{guid:$guid,name:"Live Hub",description:"Live Hub",overview:"Live Hub",owner:"livehub",category:"LiveTV",imageUrl:"",
     versions:[{version:$v,changelog:"it",targetAbi:"12.3.0.0",sourceUrl:$url,checksum:$sum,timestamp:"2026-01-01T00:00:00Z"}]}]' > "$HERE/repo/manifest.json"

trap '"${COMPOSE[@]}" down -v >/dev/null 2>&1 || true' EXIT
"${COMPOSE[@]}" up -d --build

echo "waiting for Jellyfin..."
for _ in $(seq 1 90); do curl -fsS "$JF/System/Info/Public" >/dev/null 2>&1 && break; sleep 2; done
curl -fsS "$JF/System/Info/Public" | jq -e '.Version | startswith("12.3")' >/dev/null || fail "Jellyfin 12.3 not running"
ok "Jellyfin 12.3 is up"

# ---- startup wizard + login
jf POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
jf GET /Startup/User >/dev/null
jf POST /Startup/User "{\"Name\":\"admin\",\"Password\":\"$PASS\"}" >/dev/null
jf POST /Startup/Complete >/dev/null
TOKEN="$(jf POST /Users/AuthenticateByName "{\"Username\":\"admin\",\"Pw\":\"$PASS\"}" | jq -r .AccessToken)"
UID_="$(jf GET /Users | jq -r '.[0].Id')"
[ -n "$TOKEN" ] && ok "signed in"

# ---- install from the repository (Dashboard -> Plugins -> Repositories equivalent)
jf POST /Repositories '[{"Name":"Live Hub IT","Url":"http://repo/manifest.json","Enabled":true}]' >/dev/null
jf GET /Packages | jq -e '.[] | select(.name=="Live Hub")' >/dev/null || fail "plugin not offered by the repository"
curl -fsS -X POST "$JF/Packages/Installed/Live%20Hub?assemblyGuid=$GUID&version=$VERSION&repositoryUrl=http%3A%2F%2Frepo%2Fmanifest.json" -H "Authorization: $AUTH, Token=\"$TOKEN\"" >/dev/null
sleep 5
"${COMPOSE[@]}" restart jellyfin >/dev/null
for _ in $(seq 1 90); do curl -fsS "$JF/System/Info/Public" >/dev/null 2>&1 && break; sleep 2; done
for _ in $(seq 1 30); do jf GET /Plugins | jq -e '.[] | select(.Name=="Live Hub")' >/dev/null 2>&1 && break; sleep 2; done
jf GET /Plugins | jq -e '.[] | select(.Name=="Live Hub")' >/dev/null || fail "plugin not loaded"
ok "plugin installed from the repository and loaded"

# ---- an unseen API connected with the wizard only
DET="$(jf POST /LiveHub/Admin/Wizard/Detect '{"url":"http://mock:5055/api1/events.json","allowPrivateNetwork":true}')"
echo "$DET" | jq -e '.Format=="RestJson" and (.Definition != null)' >/dev/null || fail "wizard did not detect the REST API"
DEF="$(echo "$DET" | jq -c '.Definition | .id="wizard-api1" | .name="API 1 (wizard)"')"
jf POST /LiveHub/Admin/Wizard/Preview "{\"definition\":$DEF}" | jq -e '.Valid and (.Rows|length)>0' >/dev/null || fail "wizard preview empty"
jf POST /LiveHub/Admin/Providers "$DEF" >/dev/null
ok "API added through the wizard"

# ---- the six mock APIs (definitions with the mock base URL)
for f in "$HERE"/../fixtures/definitions/*.json; do
  jf POST /LiveHub/Admin/Providers "$(jq -c '.variables.mockBase="http://mock:5055"' "$f")" >/dev/null
done
jf POST /LiveHub/Admin/Sync >/dev/null
for _ in $(seq 1 30); do
  N="$(jf GET /LiveHub/Admin/Diagnostics | jq '.Catalog.Events')"; [ "$N" -ge 8 ] && break; sleep 2
done
DIAG="$(jf GET /LiveHub/Admin/Diagnostics)"
echo "$DIAG" | jq -e '.Catalog.Events >= 8 and .Catalog.Channels >= 6' >/dev/null || fail "catalog empty: $DIAG"
echo "$DIAG" | jq -e '[.Providers[] | select(.LastError != null)] | length == 0' >/dev/null || fail "provider errors: $(echo "$DIAG" | jq -c '[.Providers[]|{ProviderId,LastError}]')"
ok "all mock APIs synchronised ($(echo "$DIAG" | jq -r '.Catalog.Events') events, $(echo "$DIAG" | jq -r '.Catalog.Channels') channels)"

# ---- native Channel
CH="$(jf GET "/Channels?userId=$UID_" | jq -r '.Items[] | select(.Name=="Live Hub") | .Id')"
[ -n "$CH" ] || fail "Live Hub channel missing"
jf GET "/Channels/$CH/Items?userId=$UID_" | jq -e '(.Items|length)==8' >/dev/null || fail "channel folders"
LIVE="$(jf GET "/Channels/$CH/Items?userId=$UID_" | jq -r '.Items[] | select(.Name=="In diretta ora" or .Name=="Live now") | .Id')"
ITEM="$(jf GET "/Channels/$CH/Items?userId=$UID_&folderId=$LIVE" | jq -r '.Items[0].Id')"
[ -n "$ITEM" ] && [ "$ITEM" != null ] || fail "no live item"
ok "Live Hub channel with 8 folders and live events"

# ---- Live TV: channels + guide created automatically
for _ in $(seq 1 60); do
  C="$(jf GET "/LiveTv/Channels?userId=$UID_&limit=100" | jq '.Items|length')"; [ "$C" -ge 10 ] && break; sleep 3
done
[ "$C" -ge 10 ] || fail "Live TV channels missing ($C)"
SLOT="$(jf GET "/LiveTv/Channels?userId=$UID_&limit=100" | jq -r '.Items[] | select(.Name|test("^(Calcio|Football) 1$|^Calcio 1$")) | .Id' | head -n1)"
[ -n "$SLOT" ] || fail "slot channel Calcio 1 missing"
jf GET "/LiveTv/Programs?userId=$UID_&channelIds=$SLOT&limit=5" | jq -e '(.Items|length)>0' >/dev/null || fail "no guide data on the slot"
ok "Live TV: $C channels with guide data"

# ---- playback through the relay
PROFILE='{"MaxStreamingBitrate":3000000,"DirectPlayProfiles":[],"TranscodingProfiles":[{"Container":"ts","Type":"Video","VideoCodec":"h264","AudioCodec":"aac","Protocol":"hls","Context":"Streaming","BreakOnNonKeyFrames":true}],"CodecProfiles":[],"SubtitleProfiles":[]}'
PI="$(jf POST "/Items/$ITEM/PlaybackInfo?userId=$UID_&autoOpenLiveStream=true&maxStreamingBitrate=3000000" "{\"UserId\":\"$UID_\",\"DeviceProfile\":$PROFILE,\"AutoOpenLiveStream\":true,\"EnableDirectPlay\":false,\"EnableDirectStream\":false}")"
echo "$PI" | jq -e '(.MediaSources|length)>=1 and (.MediaSources[0].LiveStreamId != null)' >/dev/null || fail "live stream did not open: $PI"
ok "stream opened through the relay: $(echo "$PI" | jq -r '.MediaSources[0].Name')"

TURL="$(echo "$PI" | jq -r '.MediaSources[0].TranscodingUrl')"
MASTER="$(curl -fsS "$JF$TURL")" || fail "transcode master"
MEDIA_REL="$(echo "$MASTER" | grep -v '^#' | head -n1)"
BASE="${TURL%master.m3u8*}"
if MEDIA="$(curl -fsS --retry 6 --retry-delay 3 --retry-all-errors "$JF$BASE$MEDIA_REL")"; then
  SEG="$(echo "$MEDIA" | grep -v '^#' | head -n1)"
  curl -fsS "$JF$BASE$SEG" -o /tmp/livehub-seg.ts && [ "$(head -c1 /tmp/livehub-seg.ts | xxd -p)" = "47" ] \
    && ok "ffmpeg produced an MPEG-TS segment from the relay" || fail "bad transcoded segment"
else
  fail "ffmpeg did not produce a playlist (public test streams unreachable?)"
fi

echo "integration scenario passed"
