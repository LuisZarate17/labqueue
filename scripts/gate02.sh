#!/usr/bin/env bash
# Gate 02 verification — 24 HTTP checks against a running API.
#
# Not a substitute for the xUnit suite (phase 03 owns that). This is the oracle
# those tests were checked against, kept because it exercises the five booking
# rules and the auth paths end to end in a few seconds.
#
# Prerequisites:
#   1. docker compose up -d, migrations applied, seed loaded
#   2. the API running and reachable at $API (override: API=http://... )
#   3. /tmp/ids.txt defining UNGATED, GATED and RETIRED resource ids. Generate:
#        docker compose exec -T db psql -tA -U labqueue -d labqueue \
#          -c "SELECT 'UNGATED='||id FROM resources WHERE required_certification_id IS NULL AND status='active' LIMIT 1" \
#          -c "SELECT 'GATED='||id   FROM resources WHERE required_certification_id IS NOT NULL LIMIT 1" \
#          -c "SELECT 'RETIRED='||id FROM resources WHERE status='retired' LIMIT 1" > /tmp/ids.txt

set -u
API=${API:-http://localhost:5140}
. /tmp/ids.txt
pass=0; fail=0
check() { # name expected actual
  if [ "$2" = "$3" ]; then echo "  PASS  $1 (=$3)"; pass=$((pass+1));
  else echo "  FAIL  $1 (expected $2, got $3)"; fail=$((fail+1)); fi
}
code() { curl -s -o /tmp/body.json -w "%{http_code}" "$@"; }
J='Content-Type: application/json'

echo "=== 1. register -> login ==="
EMAIL="gate-$(date +%s)@example.test"
c=$(code -X POST $API/auth/register -H "$J" -d "{\"email\":\"$EMAIL\",\"password\":\"correct-horse-battery\",\"displayName\":\"Gate Runner\"}")
check "register" 201 "$c"
MEMBER=$(sed -E 's/.*"token":"([^"]+)".*/\1/' /tmp/body.json)
MEMBER_ID=$(sed -E 's/.*"user":\{"id":"([^"]+)".*/\1/' /tmp/body.json)
c=$(code -X POST $API/auth/login -H "$J" -d "{\"email\":\"$EMAIL\",\"password\":\"correct-horse-battery\"}")
check "login" 200 "$c"
c=$(code -X POST $API/auth/login -H "$J" -d "{\"email\":\"admin@labqueue.test\",\"password\":\"labqueue-dev-password\"}")
check "admin login" 200 "$c"
ADMIN=$(sed -E 's/.*"token":"([^"]+)".*/\1/' /tmp/body.json)
AM="Authorization: Bearer $MEMBER"; AA="Authorization: Bearer $ADMIN"

echo "=== 2. unauthenticated access is rejected ==="
check "GET /reservations no token" 401 "$(code $API/reservations)"
check "GET /resources no token"    401 "$(code $API/resources)"
check "POST /resources as member"  403 "$(code -X POST $API/resources -H "$AM" -H "$J" -d '{"code":"NOPE","name":"Nope"}')"

echo "=== 3. booking rule 1 — resource must exist and be active ==="
check "unknown resource -> 404" 404 "$(code -X POST $API/reservations -H "$AM" -H "$J" -d '{"resourceId":"00000000-0000-0000-0000-0000000000ff","from":"2027-10-01T10:00:00Z","to":"2027-10-01T12:00:00Z"}')"
check "retired resource -> 409" 409 "$(code -X POST $API/reservations -H "$AM" -H "$J" -d "{\"resourceId\":\"$RETIRED\",\"from\":\"2027-10-01T10:00:00Z\",\"to\":\"2027-10-01T12:00:00Z\"}")"

echo "=== 4. booking rule 2 — window must be well formed ==="
check "9 hours -> 400"     400 "$(code -X POST $API/reservations -H "$AM" -H "$J" -d "{\"resourceId\":\"$UNGATED\",\"from\":\"2027-10-01T00:00:00Z\",\"to\":\"2027-10-01T09:00:00Z\"}")"
check "to before from -> 400" 400 "$(code -X POST $API/reservations -H "$AM" -H "$J" -d "{\"resourceId\":\"$UNGATED\",\"from\":\"2027-10-01T12:00:00Z\",\"to\":\"2027-10-01T10:00:00Z\"}")"
check "5 minutes -> 400"   400 "$(code -X POST $API/reservations -H "$AM" -H "$J" -d "{\"resourceId\":\"$UNGATED\",\"from\":\"2027-10-01T10:00:00Z\",\"to\":\"2027-10-01T10:05:00Z\"}")"

echo "=== 5. booking rule 3 — certification ==="
check "gated resource, no cert -> 403" 403 "$(code -X POST $API/reservations -H "$AM" -H "$J" -d "{\"resourceId\":\"$GATED\",\"from\":\"2027-10-02T10:00:00Z\",\"to\":\"2027-10-02T12:00:00Z\"}")"
CERT=$(curl -s -H "$AM" $API/resources/$GATED | sed -E 's/.*"requiredCertification":\{"id":"([^"]+)".*/\1/')
check "admin grants cert" 200 "$(code -X POST $API/users/$MEMBER_ID/certifications -H "$AA" -H "$J" -d "{\"certificationId\":\"$CERT\"}")"
check "gated resource, cert held -> 201" 201 "$(code -X POST $API/reservations -H "$AM" -H "$J" -d "{\"resourceId\":\"$GATED\",\"from\":\"2027-10-02T10:00:00Z\",\"to\":\"2027-10-02T12:00:00Z\"}")"

echo "=== 6. booking rule 4 — maintenance window ==="
check "admin creates maintenance" 201 "$(code -X POST $API/maintenance-windows -H "$AA" -H "$J" -d "{\"resourceId\":\"$UNGATED\",\"from\":\"2027-10-05T08:00:00Z\",\"to\":\"2027-10-05T18:00:00Z\",\"reason\":\"Gate check\"}")"
check "book over maintenance -> 409" 409 "$(code -X POST $API/reservations -H "$AM" -H "$J" -d "{\"resourceId\":\"$UNGATED\",\"from\":\"2027-10-05T10:00:00Z\",\"to\":\"2027-10-05T12:00:00Z\"}")"

echo "=== 7. booking rule 5 — overlapping confirmed reservation ==="
c=$(code -X POST $API/reservations -H "$AM" -H "$J" -d "{\"resourceId\":\"$UNGATED\",\"from\":\"2027-10-01T10:00:00Z\",\"to\":\"2027-10-01T12:00:00Z\"}")
check "first booking -> 201" 201 "$c"
BOOKING=$(sed -E 's/.*"id":"([^"]+)".*/\1/' /tmp/body.json)
check "identical window -> 409" 409 "$(code -X POST $API/reservations -H "$AM" -H "$J" -d "{\"resourceId\":\"$UNGATED\",\"from\":\"2027-10-01T10:00:00Z\",\"to\":\"2027-10-01T12:00:00Z\"}")"
check "partial overlap -> 409"  409 "$(code -X POST $API/reservations -H "$AM" -H "$J" -d "{\"resourceId\":\"$UNGATED\",\"from\":\"2027-10-01T11:00:00Z\",\"to\":\"2027-10-01T13:00:00Z\"}")"
check "abutting window -> 201"  201 "$(code -X POST $API/reservations -H "$AM" -H "$J" -d "{\"resourceId\":\"$UNGATED\",\"from\":\"2027-10-01T12:00:00Z\",\"to\":\"2027-10-01T14:00:00Z\"}")"

echo "=== 8. list and cancel ==="
check "list own reservations" 200 "$(code -H "$AM" "$API/reservations?take=10")"
echo "       mine: $(grep -o '"id"' /tmp/body.json | wc -l) reservations"
check "cancel"                204 "$(code -X DELETE $API/reservations/$BOOKING -H "$AM")"
check "cancel again -> 409"   409 "$(code -X DELETE $API/reservations/$BOOKING -H "$AM")"
check "rebook freed slot -> 201" 201 "$(code -X POST $API/reservations -H "$AM" -H "$J" -d "{\"resourceId\":\"$UNGATED\",\"from\":\"2027-10-01T10:00:00Z\",\"to\":\"2027-10-01T12:00:00Z\"}")"

echo ""
echo "=== $pass passed, $fail failed ==="
[ "$fail" = "0" ]
