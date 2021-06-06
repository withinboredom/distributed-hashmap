<?php

namespace DistributedHashMap\Internal;

use Dapr\Serialization\Attributes\AlwaysObject;

class KeyTrigger
{
    public function __construct(
        public string $pubsubName,
        public string $topic,
        #[AlwaysObject]
        public array|null $metadata
    ) {
    }
}
