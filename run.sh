#!/bin/bash
set -e

docker-compose down -v
docker-compose build --pull subscriptions
docker-compose up -d php-daprd csharp-daprd subscriptions
# uncomment the below line to ensure all messages get received
#docker-compose logs -f subscriptions &
docker-compose build --pull php-writer csharp-writer php-reader csharp-reader php-unit php-sub-validator
docker-compose run php-unit
docker-compose run php-writer
docker-compose run csharp-writer
docker-compose run php-reader
docker-compose run csharp-reader
echo waiting 10s for all events to be processed
sleep 10
docker-compose run php-sub-validator
