#!/bin/bash
set -e

docker-compose down -v
docker-compose up -d php-daprd csharp-daprd
docker-compose build php-writer csharp-writer php-reader csharp-reader
docker-compose run csharp-writer
docker-compose run php-writer
docker-compose run php-reader
docker-compose run csharp-reader
