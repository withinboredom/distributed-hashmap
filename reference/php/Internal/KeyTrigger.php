<?php

namespace DistributedHashMap\Internal;

class KeyTrigger
{
    public function __construct(public string $pubsubName, public string $topic, public array $metadata)
    {
    }
}
