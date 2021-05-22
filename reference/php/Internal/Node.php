<?php

namespace DistributedHashMap\Internal;

use Dapr\Deserialization\Attributes\ArrayOf;

class Node
{
    public function __construct(#[ArrayOf('string')] public array $items = [])
    {
    }
}
