<?php

namespace DistributedHashMap;

use ArrayAccess;
use Dapr\consistency\StrongFirstWrite;
use Dapr\exceptions\DaprException;
use DistributedHashMap\Internal\Header;
use DistributedHashMap\Internal\KeyTrigger;
use DistributedHashMap\Internal\MapInterface;
use DistributedHashMap\Internal\Node;
use JetBrains\PhpStorm\Pure;
use lastguest\Murmur;

/**
 * Class Map
 * @package DistributedHashMap
 */
class Map implements MapInterface, ArrayAccess
{
    public mixed $rebuildCallback = null;
    /**
     * @var Header The cached header for operations
     */
    private Header $header;

    private string $header_etag = '-1';

    /**
     * Map constructor.
     *
     * @param string $name The name of the hash map
     * @param string $storeName The state store name
     * @param int $expectedCapacity
     * @param int $maxLoad
     * @param \Dapr\Client\DaprClient $client
     */
    public function __construct(
        private string $name,
        private string $storeName,
        private \Dapr\Client\DaprClient $client,
        private int $expectedCapacity = 256,
        private int $maxLoad = 12,
    ) {
    }

    /**
     * @throws DaprException
     */
    public function subscribe(string $key, string $pubsubName, string $topic, array $metadata = []): void
    {
        $this->getHeaderAndMaybeRebuild();
        [$node, $etag] = $this->readBucketFor($key);

        $trigger = new KeyTrigger($pubsubName, $topic, $metadata);

        if (isset($node->triggers[$key]) && $node->triggers[$key] == $trigger) {
            return;
        }

        $node->triggers[$key] = $trigger;
        $this->writeBucket($key, $node, $etag, fn() => $this->subscribe($key, $pubsubName, $topic, $metadata));
    }

    /**
     * Cache the header and maybe participate in an active rebuild
     *
     * @return Header The current header
     */
    private function getHeaderAndMaybeRebuild(): Header
    {
        if (isset($this->header) && $this->getHeaderFromHeader()->rebuilding) {
            return $this->getHeaderFromHeader();
        }

        $header = $this->getHeaderFromStore();
        if ($header->rebuilding) {
            $this->rebuild('joining rebuild');

            return $this->getHeaderFromStore();
        }

        return $header;
    }

    /**
     * Helper to get the header
     *
     * @return Header The current header
     */
    private function getHeaderFromHeader(): Header
    {
        return $this->header;
    }

    /**
     * Reads the header from the store
     *
     * @return Header The current header
     */
    private function getHeaderFromStore(): Header
    {
        $headerKey = $this->getHeaderKey();
        ['etag' => $etag, 'value' => $header] = $this->client->getStateAndEtag(
            $this->storeName,
            $headerKey,
            Header::class,
            new StrongFirstWrite()
        );
        if (empty($etag)) {
            $header = $this->getDefaultHeader();
            try {
                $this->client->trySaveState($this->storeName, $headerKey, $header, '-1');
            } catch (DaprException) {
                // someone else beat us to writing the header
            }
        }

        $this->header      = $header;
        $this->header_etag = $etag;

        return $this->header;
    }

    private function getHeaderKey(): string
    {
        return 'DHMHeader_'.$this->name;
    }

    #[Pure] private function getDefaultHeader(): Header
    {
        return new Header(
            generation: ceil(log(max(256, $this->expectedCapacity)) / log(2)) - 7, maxLoad: $this->maxLoad
        );
    }

    /**
     * Participate or start a rebuild of the next generation
     */
    public function rebuild($reason = 'manual'): void
    {
        $header = $this->getHeaderFromHeader();
        if ( ! $header->rebuilding) {
            if (is_callable($this->rebuildCallback)) {
                $cb = $this->rebuildCallback;
                $cb('init', $reason);
            }
            $this->header->rebuilding = true;
            try {
                $this->client->trySaveState($this->storeName, $this->getHeaderKey(), $this->header, $this->header_etag);
                $this->getHeaderFromStore();
            } catch (DaprException) {
                // someone else beat us to updating the header. So we'll have to rebuild later
                return;
            }
        }
        $nextGenerationHeader = clone $header;
        $nextGenerationHeader->generation++;
        $pointer                = $start_point = rand(0, $this->getMapSize());
        $nextGeneration         = new self(
            $this->name,
            $this->storeName,
            $this->client,
            $this->expectedCapacity,
            $this->maxLoad
        );
        $nextGeneration->header = $nextGenerationHeader;

        do {
            ['value' => $node] = $this->client->getStateAndEtag(
                $this->storeName,
                'DHM_'.$this->name.'_'.$this->header->generation.'_'.$pointer,
                Node::class
            );

            $triggers = $node->triggers;
            foreach ($node->items as $key => $value) {
                $retries = 100;
                do {
                    try {
                        $nextGeneration->putRaw($key, $value, $triggers[$key] ?? null);
                        unset($triggers[$key]);
                        $retries = 0;
                    } catch (DaprException) {
                        $retries--;
                    }
                } while ($retries > 0);
            }
            foreach ($triggers as $key => $trigger) {
                $retries = 100;
                do {
                    try {
                        $nextGeneration->subscribe($key, $trigger->pubsubName, $trigger->topic, $trigger->metadata);
                        $retries = 0;
                    } catch (DaprException) {
                        $retries--;
                    }
                } while ($retries > 0);
            }

            $pointer = ($pointer + 1) % $this->getMapSize();
        } while ($pointer !== $start_point);

        $header->rebuilding = false;
        $header->generation++;
        $this->header = $header;
        if (is_callable($this->rebuildCallback)) {
            $cb = $this->rebuildCallback;
            $cb('finish', $reason);
        }
        try {
            $this->client->trySaveState($this->storeName, $this->getHeaderKey(), $this->header, $this->header_etag);
        } catch (DaprException) {
            // someone won this race
        }
        unset($this->header);
    }

    /**
     * The total number of buckets available in the current hash map
     *
     * @return int The current size of the map
     */
    #[Pure] private function getMapSize(): int
    {
        return pow(2, 7 + $this->getHeaderFromHeader()->generation);
    }

    /**
     * Puts a value into the hash map
     *
     * @param string $key The key to put
     * @param string $value The value to put
     *
     * @throws DaprException
     */
    private function putRaw(string $key, string $value, KeyTrigger|null $subscribe): array
    {
        $header = $this->getHeaderAndMaybeRebuild();
        [$node, $etag] = $this->readBucketFor($key);

        if ((isset($node->items[$key]) && $node->items[$key] === $value) && (empty($subscribe) || (isset($node->triggers[$key]) && $node->triggers[$key] == $subscribe))) {
            return ['trigger' => null, 'metadata' => []];
        }

        if ($subscribe) {
            $node->triggers[$key] = $subscribe;
        }

        $previousValue     = $node->items[$key] ?? null;
        $node->items[$key] = $value;
        $retried           = false;
        $this->writeBucket(
            $key,
            $node,
            $etag,
            function () use (&$retried, $key, $value, $subscribe) {
                $retried = $this->putRaw($key, $value, $subscribe);
            }
        );
        if ($retried) {
            return $retried;
        }

        if (count($node->items) > $header->maxLoad) {
            $this->rebuild('exceeded max load');
        }

        if ($subscribe || ($trigger = $node->triggers[$key] ?? null) === null) {
            return ['trigger' => null, 'metadata' => []];
        }

        return [
            'trigger'  => new TriggerEvent(
                $key,
                $this->name,
                $previousValue,
                $value,
                $trigger->pubsubName,
                $trigger->topic
            ),
            'metadata' => $trigger->metadata ?? [],
        ];
    }

    /**
     * @throws DaprException
     */
    private function readBucketFor(string $key): array
    {
        $bucket_key = $this->getBucketKey($key);

        $retries = 0;
        do {
            try {
                ['etag' => $etag, 'value' => $bucket] = $this->client->getStateAndEtag(
                    $this->storeName,
                    $bucket_key,
                    Node::class,
                    new StrongFirstWrite()
                );

                if (empty($etag)) {
                    $bucket = new Node();
                }

                return [$bucket, $etag];
            } catch (DaprException $e) {
                if ($retries++ > 3) {
                    throw $e;
                }
            }
        } while ($retries);
        throw new \LogicException("unreachable code");
    }

    /**
     * Get a state key for a bucket
     *
     * @param string $key The user key
     *
     * @return string The state key
     */
    private function getBucketKey(string $key): string
    {
        return 'DHM_'.$this->name.'_'.$this->getHeaderFromHeader()->generation.'_'.$this->getBucket($key);
    }

    /**
     * Calculate the state bucket for the given user key
     *
     * @param string $key The user key
     *
     * @return int The bucket
     */
    private function getBucket(string $key): int
    {
        return Murmur::hash3_int($key) % $this->getMapSize();
    }

    /**
     */
    private function writeBucket(string $key, Node $node, string $etag, callable $onFailure): void
    {
        try {
            if ( ! $this->client->trySaveState($this->storeName, $key, $node, $etag, new StrongFirstWrite())) {
                $onFailure();
            }

            return;
        } catch (DaprException $e) {
            $onFailure();

            return;
        }
    }

    /**
     * @throws DaprException
     */
    public function unsubscribe(string $key): void
    {
        $this->getHeaderAndMaybeRebuild();
        [$node, $etag] = $this->readBucketFor($key);

        if ( ! isset($node->triggers[$key])) {
            return;
        }

        unset($node->triggers[$key]);
        $this->writeBucket($key, $node, $etag, fn() => $this->unsubscribe($key));
    }

    /**
     * @inheritDoc
     */
    public function offsetExists($offset): bool
    {
        return $this->contains($offset);
    }

    /**
     * Whether the hashmap contains a key
     *
     * @param string $key The key to check
     *
     * @return bool True if it exists (including null)
     */
    public function contains(string $key): bool
    {
        $this->getHeaderAndMaybeRebuild();
        [$node, $etag] = $this->readBucketFor($key);

        return array_key_exists($key, $node->items);
    }

    /**
     * @inheritDoc
     */
    public function offsetGet($offset): mixed
    {
        return $this->get($offset);
    }

    /**
     * Retrieve a key from the hashmap
     *
     * @param string $key The key
     * @param string|null $type The type to deserialize as
     *
     * @return mixed The stored value or null if it doesn't exist
     */
    public function get(string $key, ?string $type = null): mixed
    {
        $this->getHeaderAndMaybeRebuild();
        [$node, $etag] = $this->readBucketFor($key);
        $value = $node->items[$key] ?? null;
        if ($value === null || ! $type) {
            return $value;
        }

        return $this->client->deserializer->from_json($type, $value);
    }

    /**
     * @inheritDoc
     */
    public function offsetSet($offset, $value): void
    {
        $this->put($offset, $value);
    }

    /**
     * Put a value in the hashmap
     *
     * @param string $key The key to put
     * @param mixed $value The value to put
     */
    public function put(string $key, mixed $value): void
    {
        $value   = $this->client->serializer->as_json($value);
        $retries = 100;
        do {
            try {
                ['trigger' => $trigger, 'metadata' => $metadata] = $this->putRaw($key, $value, null);
                $retries = 0;
            } catch (DaprException) {
                $retries--;
            }
        } while ($retries > 0);

        if ( ! empty($trigger)) {
            $this->broadcast($trigger, $metadata);
        }
    }

    private function broadcast(TriggerEvent $trigger, array $metadata)
    {
        $this->client->publishEvent($trigger->pubsubName, $trigger->topic, $trigger, $metadata);
    }

    /**
     * @inheritDoc
     */
    public function offsetUnset($offset): void
    {
        $this->remove($offset);
    }

    /**
     * Delete a key from the map
     *
     * @param string $key The key to delete
     */
    public function remove(string $key): void
    {
        $this->getHeaderAndMaybeRebuild();
        [$node, $etag] = $this->readBucketFor($key);
        if ( ! array_key_exists($key, $node->items)) {
            return;
        }
        if ($node->triggers[$key] ?? false) {
            $trigger  = new TriggerEvent(
                $key,
                $this->name,
                $node->items[$key],
                null,
                $node->triggers[$key]->pubsubName,
                $node->triggers[$key]->topic
            );
            $metadata = $node->triggers[$key]->metadata;
        }
        unset($node->items[$key]);
        $retried = false;
        $this->writeBucket(
            $key,
            $node,
            $etag,
            function () use (&$retried, $key) {
                $retried = true;
                $this->remove($key);
            }
        );
        if ( ! $retried && ! empty($trigger)) {
            $this->broadcast($trigger, $metadata ?? []);
        }
    }
}
