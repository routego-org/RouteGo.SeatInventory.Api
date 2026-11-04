#!/usr/bin/env bash
# Build the image, start the API, then wait for /health.
# Usage (on the server): sudo bash ~/seat-inventory/deploy/ec2/start.sh
set -euo pipefail

cd "$(dirname "$0")"
REGION=ap-south-1
SHARED_NETWORK=ec2_default
SEAT_TABLE=routego-seat-inventory

if ! docker network inspect "$SHARED_NETWORK" >/dev/null 2>&1; then
  echo "ERROR: Docker network $SHARED_NETWORK not found. Is the Postgres container (ec2-postgres-1) running?" >&2
  exit 1
fi

# The seat publisher reads the table's stream; without the table the API starts but every seat
# call fails, so stop here with a clear message instead.
echo ">> Checking the DynamoDB table $SEAT_TABLE"
if ! aws dynamodb describe-table --table-name "$SEAT_TABLE" --region "$REGION" \
  --query 'Table.StreamSpecification.StreamViewType' --output text | grep -q NEW_AND_OLD_IMAGES; then
  echo "ERROR: table $SEAT_TABLE is missing, has no NEW_AND_OLD_IMAGES stream, or the instance role cannot read it." >&2
  exit 1
fi

echo ">> Building image (first time takes 5-10 minutes)"
docker build -t routego/seat-inventory:latest ../..

echo ">> Starting the API"
if ! docker compose up -d; then
  echo "ERROR: API start failed. Last logs:" >&2
  docker compose ps -a
  docker compose logs --tail 50 seat-inventory
  exit 1
fi

echo ">> Waiting for the API to be healthy"
for _ in $(seq 1 30); do
  if curl -fs http://localhost:5004/health >/dev/null 2>&1; then
    docker compose ps -a
    echo ">> DEPLOY OK — Seat Inventory API is healthy on port 5004"
    exit 0
  fi
  sleep 2
done

echo "ERROR: Seat Inventory API did not become healthy. Last logs:" >&2
docker compose ps -a
docker compose logs --tail 50 seat-inventory
exit 1
