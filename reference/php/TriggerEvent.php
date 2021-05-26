<?php

namespace DistributedHashMap;

class TriggerEvent
{
    public function __construct(
        public string $key,
        public string $mapName,
        public mixed $previousValue,
        public mixed $newValue,
        public string $pubsubName,
        public string $topic
    ) {
    }
}
