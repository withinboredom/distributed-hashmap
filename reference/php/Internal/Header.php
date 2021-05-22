<?php

namespace DistributedHashMap\Internal;

class Header
{
    public function __construct(
        public bool $rebuilding = false,
        public int $generation = 1,
        public int $maxLoad = 12,
        public int $version = 0
    ) {
    }
}
