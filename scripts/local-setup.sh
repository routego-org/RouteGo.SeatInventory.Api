#!/bin/bash
# Local setup (LOCAL ONLY) for Seat Inventory's background workers. Run it once, from Git Bash, with
# DynamoDB Local (port 8000) and LocalStack (port 4566) already running in Docker:
#
#   docker run -d -p 8000:8000 amazon/dynamodb-local:2.6.1
#   docker run -d -p 4566:4566 localstack/localstack:4.4
#   bash scripts/local-setup.sh
#
# It makes sure that:
#   - the seat store table exists, with its change stream on (NEW_AND_OLD_IMAGES) — the seat-event
#     publisher reads it; a table created earlier without a stream gets one;
#   - the two topics exist: routego-inventory-events (seat events) and routego-audit-events (audit records);
#   - the hold-expiry queue exists, and parks a timer on inventory-hold-expiry-dlq after 5 deliveries;
#   - Fleet's topic routego-fleet-events exists, and three queues are subscribed to it, one per event
#     Seat Inventory applies (TripScheduled, TripCancelled, FareChanged), each with its own dead-letter queue;
#   - Booking's topic routego-booking-events exists, and inventory-booking-q (dead-letter queue
#     inventory-booking-dlq) is subscribed to it for BookingFailed and BookingCancelled.
#
# No AWS CLI is needed on this machine: every command runs inside the LocalStack container, which
# ships one. Every step is safe to repeat, and it only adds or turns things on — it never deletes.
# Real AWS resources are created by the deployment, not by this script.
set -euo pipefail

REGION="ap-south-1"
TABLE="routego-seat-inventory"
HOLD_EXPIRY_QUEUE="inventory-hold-expiry-q"
HOLD_EXPIRY_DLQ="inventory-hold-expiry-dlq"
# Must match MaxDeliveries in Api/Features/Holds/ExpireHold/ExpireHold.cs and
# Api/Common/Messaging/EventQueueService.cs.
MAX_RECEIVE_COUNT=5
# DynamoDB Local as the LocalStack container sees it (Docker Desktop's name for the host).
DYNAMODB_URL="${DYNAMODB_URL:-http://host.docker.internal:8000}"

# Git Bash would otherwise rewrite arguments that look like paths before Docker sees them.
export MSYS_NO_PATHCONV=1

container="${LOCALSTACK_CONTAINER:-$(docker ps -q --filter ancestor=localstack/localstack:4.4 | head -n 1)}"
if [ -z "$container" ]; then
  echo "No running LocalStack container found. Start one (see the top of this script) or set LOCALSTACK_CONTAINER." >&2
  exit 1
fi

dynamodb() {
  docker exec -e AWS_ACCESS_KEY_ID=local -e AWS_SECRET_ACCESS_KEY=local "$container" \
    aws --endpoint-url "$DYNAMODB_URL" --region "$REGION" dynamodb "$@"
}

awslocal() {
  docker exec "$container" awslocal --region "$REGION" "$@"
}

# The seat store table, with its change stream.
if dynamodb describe-table --table-name "$TABLE" >/dev/null 2>&1; then
  view_type=$(dynamodb describe-table --table-name "$TABLE" \
    --query "Table.StreamSpecification.StreamViewType" --output text)
  if [ "$view_type" != "NEW_AND_OLD_IMAGES" ]; then
    dynamodb update-table --table-name "$TABLE" \
      --stream-specification StreamEnabled=true,StreamViewType=NEW_AND_OLD_IMAGES >/dev/null
    echo "table $TABLE: change stream turned on (changes from now on are published)"
  else
    echo "table $TABLE: change stream already on"
  fi
else
  dynamodb create-table --table-name "$TABLE" \
    --attribute-definitions AttributeName=pk,AttributeType=S AttributeName=sk,AttributeType=S \
    --key-schema AttributeName=pk,KeyType=HASH AttributeName=sk,KeyType=RANGE \
    --billing-mode PAY_PER_REQUEST \
    --stream-specification StreamEnabled=true,StreamViewType=NEW_AND_OLD_IMAGES >/dev/null
  echo "table $TABLE: created, with its change stream on"
fi

# The two topics the publisher sends to. Creating a topic that exists returns the existing one.
awslocal sns create-topic --name routego-inventory-events >/dev/null
awslocal sns create-topic --name routego-audit-events >/dev/null
echo "topics: routego-inventory-events, routego-audit-events"

# Creates a queue and its dead-letter queue, with the redrive policy that parks a message after
# MAX_RECEIVE_COUNT deliveries, and prints the queue's ARN.
create_queue_with_dlq() {
  local queue="$1"
  local dlq="$2"
  awslocal sqs create-queue --queue-name "$dlq" >/dev/null
  local dlq_url dlq_arn queue_url
  dlq_url=$(awslocal sqs get-queue-url --queue-name "$dlq" --query QueueUrl --output text)
  dlq_arn=$(awslocal sqs get-queue-attributes --queue-url "$dlq_url" \
    --attribute-names QueueArn --query Attributes.QueueArn --output text)
  awslocal sqs create-queue --queue-name "$queue" >/dev/null
  queue_url=$(awslocal sqs get-queue-url --queue-name "$queue" --query QueueUrl --output text)
  awslocal sqs set-queue-attributes --queue-url "$queue_url" \
    --attributes "{\"RedrivePolicy\":\"{\\\"deadLetterTargetArn\\\":\\\"${dlq_arn}\\\",\\\"maxReceiveCount\\\":\\\"${MAX_RECEIVE_COUNT}\\\"}\"}"
  awslocal sqs get-queue-attributes --queue-url "$queue_url" \
    --attribute-names QueueArn --query Attributes.QueueArn --output text
}

# The hold-expiry queue and its dead-letter queue.
create_queue_with_dlq "$HOLD_EXPIRY_QUEUE" "$HOLD_EXPIRY_DLQ" >/dev/null
echo "queue: $HOLD_EXPIRY_QUEUE (parks a timer on $HOLD_EXPIRY_DLQ after $MAX_RECEIVE_COUNT deliveries)"

# Fleet's events: one queue per event type. Raw delivery makes the message body the RouteGo envelope
# itself, not an SNS wrapper; the filter lets only that queue's event type through. Subscribing again
# with the same attributes returns the existing subscription.
fleet_topic=$(awslocal sns create-topic --name routego-fleet-events --query TopicArn --output text)
for event_type in TripScheduled TripCancelled FareChanged; do
  case "$event_type" in
    TripScheduled) name="inventory-trip-scheduled" ;;
    TripCancelled) name="inventory-trip-cancelled" ;;
    FareChanged) name="inventory-fare-changed" ;;
  esac
  queue_arn=$(create_queue_with_dlq "${name}-q" "${name}-dlq")
  awslocal sns subscribe --topic-arn "$fleet_topic" --protocol sqs --notification-endpoint "$queue_arn" \
    --attributes "{\"RawMessageDelivery\":\"true\",\"FilterPolicy\":\"{\\\"eventType\\\":[\\\"${event_type}\\\"]}\"}" >/dev/null
  echo "queue: ${name}-q (parks on ${name}-dlq after $MAX_RECEIVE_COUNT deliveries), subscribed to routego-fleet-events for $event_type"
done

# Booking's events: BookingFailed and BookingCancelled share one queue, as in the system queue catalog.
booking_topic=$(awslocal sns create-topic --name routego-booking-events --query TopicArn --output text)
booking_queue_arn=$(create_queue_with_dlq inventory-booking-q inventory-booking-dlq)
awslocal sns subscribe --topic-arn "$booking_topic" --protocol sqs --notification-endpoint "$booking_queue_arn" \
  --attributes "{\"RawMessageDelivery\":\"true\",\"FilterPolicy\":\"{\\\"eventType\\\":[\\\"BookingFailed\\\",\\\"BookingCancelled\\\"]}\"}" >/dev/null
echo "queue: inventory-booking-q (parks on inventory-booking-dlq after $MAX_RECEIVE_COUNT deliveries), subscribed to routego-booking-events for BookingFailed, BookingCancelled"
