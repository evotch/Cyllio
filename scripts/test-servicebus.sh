#!/usr/bin/env bash
set -euo pipefail

curl --fail --silent --show-error --retry 30 --retry-all-errors --retry-delay 2 http://localhost:5300/health > /dev/null
response=$(curl --fail --silent --show-error --retry 10 --retry-all-errors --retry-delay 2 -X POST http://localhost:8083/dev/servicebus/probe)
event_id=$(printf '%s' "$response" | python3 -c 'import json,sys; print(json.load(sys.stdin)["eventId"])')
for attempt in {1..30}; do
  logs=$(docker compose logs --no-color --since 5m notification realtime)
  if [[ "$logs" == *"$event_id by notification"* && "$logs" == *"$event_id by realtime"* ]]; then
    printf 'Service Bus probe %s reached both subscriptions.\n' "$event_id"
    exit 0
  fi
  sleep 2
done
printf 'Probe %s was not received by both consumers.\n' "$event_id" >&2
docker compose logs --tail 50 servicebus notification realtime
exit 1
