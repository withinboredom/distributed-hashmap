<?php

namespace DistributedHashMap\Internal;

interface MapInterface {
    public function put(string $key, mixed $value): void;
    public function get(string $key, string|null $type = null): mixed;
    public function contains(string $key): bool;
    public function remove(string $key): void;
    public function rebuild(): void;
    public function size(): int;
}
