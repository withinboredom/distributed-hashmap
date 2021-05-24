#!/bin/bash
set -e

docker-compose down -v
docker-compose up -d php-daprd csharp-daprd
docker-compose build csharp-writer php-writer
docker-compose run csharp-writer
docker-compose run php-writer