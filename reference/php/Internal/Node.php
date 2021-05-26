<?php

namespace DistributedHashMap\Internal;

use Dapr\Deserialization\Attributes\ArrayOf;

class Node
{
    /**
     * Node constructor.
     *
     * @param string[] $items
     * @param KeyTrigger[] $triggers
     */
    public function __construct(
        #[ArrayOf('string')] public array $items = [],
        #[ArrayOf(KeyTrigger::class)] public array $triggers = []
    ) {
    }
}
