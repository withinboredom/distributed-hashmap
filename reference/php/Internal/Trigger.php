<?php

namespace DistributedHashMap\Internal;

class Trigger
{
    public function __construct(public bool $isDaprUrl, public bool $isPubSub, public string $destination)
    {
    }
}
