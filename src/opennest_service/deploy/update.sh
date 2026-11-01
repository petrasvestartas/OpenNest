#!/usr/bin/env bash
# Update the running service without killing a job: pull, build the new image while the old one keeps
# serving, wait until no job is queued or running, then swap the container (a few seconds of downtime).
#
#     /opt/OpenNest/src/opennest_service/deploy/update.sh
#
# Run it detached from a laptop SSH session so a dropped connection cannot interrupt it:
#     ssh root@SERVER 'nohup /opt/OpenNest/src/opennest_service/deploy/update.sh > /root/update.log 2>&1 &'
set -euo pipefail
cd "$(dirname "$0")"
git -C ../../.. pull -q
echo "building $(git -C ../../.. log --oneline -1)"
docker compose build
PW=$(grep '^OPENNEST_PASSWORD=' .env | cut -d= -f2-)
DOMAIN=$(grep '^OPENNEST_DOMAIN=' .env | cut -d= -f2-)
while :; do
    # the service port is not published on the host; go through Caddy like a browser does
    active=$(curl -s -H "Authorization: Bearer $PW" "https://$DOMAIN/api/jobs" 2>/dev/null | python3 -c 'import json,sys; print(json.load(sys.stdin).get("active", 0))' 2>/dev/null || echo 0)
    [ "$active" = "0" ] && break
    echo "$(date +%H:%M:%S) waiting: $active job(s) queued or running"
    sleep 20
done
docker compose up -d
echo "swapped: $(docker compose ps --format '{{.Name}} {{.Status}}' | tr '\n' ' ')"
