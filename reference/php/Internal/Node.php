<?php

namespace DistributedHashMap\Internal;

use Dapr\Deserialization\Attributes\ArrayOf;
use Dapr\Serialization\Attributes\AlwaysObject;

class Node
{
    /**
     * Node constructor.
     *
     * @param string[] $items
     * @param KeyTrigger[] $triggers
     */
    public function __construct(
        #[ArrayOf('string')]
        public array $items = [],

        #[ArrayOf(KeyTrigger::class)]
        #[AlwaysObject]
        public array $triggers = []
    ) {
    }
}
