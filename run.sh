#!/bin/bash
set -e

docker-compose down -v
docker-compose build subscriptions
docker-compose up -d php-daprd csharp-daprd subscriptions
docker-compose build php-writer csharp-writer php-reader csharp-reader php-unit php-sub-validator
docker-compose run php-unit
docker-compose run csharp-writer
docker-compose run php-writer
docker-compose run php-reader
docker-compose run csharp-reader
docker-compose run php-sub-validator
