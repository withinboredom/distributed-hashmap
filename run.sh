#!/bin/bash

docker-compose down -v
docker-compose up -d php-daprd csharp-daprd
docker-compose up --build csharp-writer
docker-compose up --build php-writer
