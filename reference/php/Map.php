<?php

namespace DistributedHashMap;

use ArrayAccess;
use Dapr\consistency\StrongFirstWrite;
use Dapr\DaprClient;
use Dapr\Deserialization\IDeserializer;
use Dapr\exceptions\DaprException;
use Dapr\PubSub\Topic;
use Dapr\Serialization\ISerializer;
use Dapr\State\IManageState;
use Dapr\State\StateItem;
use DistributedHashMap\Internal\Header;
use DistributedHashMap\Internal\KeyTrigger;
use DistributedHashMap\Internal\MapInterface;
use DistributedHashMap\Internal\Node;
use JetBrains\PhpStorm\Pure;
use lastguest\Murmur;
use Psr\Log\LoggerInterface;

/**
 * Class Map
 * @package DistributedHashMap
 */
class Map implements MapInterface, ArrayAccess
{
    /**
     * @var StateItem The cached header for operations
     */
    private StateItem $header;

    /**
     * Map constructor.
     *
     * @param string $name The name of the hash map
     * @param IManageState $stateManager The state manager
     * @param string $storeName The state store name
     * @param ISerializer $serializer The serializer
     * @param IDeserializer $deserializer The deserializer
     */
    public function __construct(
        private string $name,
        private IManageState $stateManager,
        private string $storeName,
        private ISerializer $serializer,
        private IDeserializer $deserializer,
        private LoggerInterface $logger,
        private int $expectedCapacity = 256,
        private int $maxLoad = 12
    ) {
    }

    public function subscribe(string $key, string $pubsubName, string $topic, array $metadata = []): void
    {
        $this->getHeaderAndMaybeRebuild();
        $bucket = $this->stateManager->load_state(
            $this->storeName,
            rawurlencode($this->getBucketKey($key)),
            new Node(),
            consistency: new StrongFirstWrite()
        );
        $bucket->etag ??= "-1";
        $node = $this->getNodeFromItem($bucket);

        $trigger = new KeyTrigger($pubsubName, $topic, $metadata);

        if (isset($node->triggers[$key]) && $node->triggers[$key] == $trigger) {
            return;
        }

        $node->triggers[$key] = $trigger;

        $this->stateManager->save_state($this->storeName, $bucket);
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
            $this->rebuild();

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
        return $this->header->value;
    }

    /**
     * Reads the header from the store
     *
     * @return Header The current header
     */
    private function getHeaderFromStore(): Header
    {
        $headerKey    = 'DHMHeader_'.$this->name;
        $this->header = $this->stateManager->load_state(
            $this->storeName,
            rawurlencode($headerKey),
            default_value: $this->getDefaultHeader(),
            consistency: new StrongFirstWrite()
        );
        if ($this->header->etag === null) {
            $this->header->etag = "-1";
            try {
                $this->stateManager->save_state($this->storeName, $this->header);
            } catch (DaprException) {
                // someone else beat us to writing the header
            }
        }
        if ( ! $this->header->value instanceof Header) {
            $this->header->value = $this->deserializer->from_value(Header::class, $this->header->value);
        }

        return $this->header->value;
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
    public function rebuild(): void
    {
        $header = $this->getHeaderFromHeader();
        if ( ! $header->rebuilding) {
            $this->header->value->rebuilding = true;
            $this->stateManager->save_state($this->storeName, $this->header);
        }
        $nextGenerationHeader = clone $header;
        $nextGenerationHeader->generation++;
        $pointer                       = $start_point = rand(0, $this->getMapSize());
        $nextGeneration                = new self(
            $this->name,
            $this->stateManager,
            $this->storeName,
            $this->serializer,
            $this->deserializer,
            $this->logger,
            $this->expectedCapacity,
            $this->maxLoad
        );
        $nextGeneration->header        = clone $this->header;
        $nextGeneration->header->value = $nextGenerationHeader;

        do {
            $bucket = $this->stateManager->load_state(
                $this->storeName,
                rawurlencode('DHM_'.$this->name.'_'.$this->getHeaderFromHeader()->generation.'_'.$pointer),
                default_value: new Node(),
                consistency: new StrongFirstWrite()
            );
            $node   = $this->getNodeFromItem($bucket);
            foreach ($node->items as $key => $value) {
                $retries = 100;
                do {
                    try {
                        $nextGeneration->putRaw($key, $value, $node->triggers[$key] ?? null);
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
        $this->header->value = $header;
        try {
            $this->stateManager->save_state($this->storeName, $this->header);
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
     * Get a node from a state item
     *
     * @param StateItem $item The item
     *
     * @return Node The node
     */
    private function getNodeFromItem(StateItem $item): Node
    {
        if ( ! $item->value instanceof Node) {
            $item->value = $this->deserializer->from_value(Node::class, $item->value);
        }

        return $item->value;
    }

    /**
     * Puts a value into the hash map
     *
     * @param string $key The key to put
     * @param string $value The value to put
     */
    private function putRaw(string $key, string $value, KeyTrigger|null $subscribe): array
    {
        $header         = $this->getHeaderAndMaybeRebuild();
        $bucket_key     = $this->getBucketKey($key);
        $nodeItem       = $this->stateManager->load_state(
            $this->storeName,
            rawurlencode($bucket_key),
            default_value: new Node(),
            consistency: new StrongFirstWrite()
        );
        $nodeItem->etag ??= '-1';
        $node           = $this->getNodeFromItem($nodeItem);

        if ((isset($node->items[$key]) && $node->items[$key] === $value) && (empty($subscribe) || (isset($node->triggers[$key]) && $node->triggers[$key] == $subscribe))) {
            return ['trigger' => null, 'metadata' => []];
        }

        if ($subscribe) {
            $node->triggers[$key] = $subscribe;
        }

        $previousValue     = $node->items[$key] ?? null;
        $node->items[$key] = $value;
        $this->stateManager->save_state($this->storeName, $nodeItem);

        if (count($node->items) > $header->maxLoad) {
            $this->rebuild();
        }

        if ($subscribe || ($trigger = $node->triggers[$key] ?? null) === null) {
            return ['trigger' => null, 'metadata' => []];
        }

        return ['trigger' => new TriggerEvent($key, $this->name, $previousValue, $value, $trigger->pubsubName, $trigger->topic), 'metadata' => $trigger->metadata];
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

    public function unsubscribe(string $key): void
    {
        $this->getHeaderAndMaybeRebuild();
        $bucket = $this->stateManager->load_state(
            $this->storeName,
            rawurlencode($this->getBucketKey($key)),
            new Node(),
            consistency: new StrongFirstWrite()
        );
        $node   = $this->getNodeFromItem($bucket);

        if ( ! isset($node->triggers[$key])) {
            return;
        }

        unset($node->triggers[$key]);

        $this->stateManager->save_state($this->storeName, $bucket);
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
        $bucket = $this->stateManager->load_state(
            $this->storeName,
            rawurlencode($this->getBucketKey($key)),
            new Node(),
            consistency: new StrongFirstWrite()
        );
        $node   = $this->getNodeFromItem($bucket);

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
        $bucket_key = $this->getBucketKey($key);
        $bucket     = $this->stateManager->load_state(
            $this->storeName,
            rawurlencode($bucket_key),
            new Node(),
            consistency: new StrongFirstWrite()
        );
        $node       = $this->getNodeFromItem($bucket);
        $value      = $node->items[$key] ?? null;
        if ($value === null || ! $type) {
            return $value;
        }

        return $this->deserializer->from_json($type, $value);
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
        $value   = $this->serializer->as_json($value);
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
        $topic = new Topic($trigger->pubsubName, $trigger->topic, DaprClient::get_client(), $this->logger);
        $topic->publish($trigger, $metadata);
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
        $retries = 100;
        do {
            try {
                $this->getHeaderAndMaybeRebuild();
                $bucket_key   = $this->getBucketKey($key);
                $bucket       = $this->stateManager->load_state(
                    $this->storeName,
                    rawurlencode($bucket_key),
                    default_value: new Node(),
                    consistency: new StrongFirstWrite()
                );
                $bucket->etag ??= '-1';
                $node         = $this->getNodeFromItem($bucket);
                if ( ! array_key_exists($key, $node->items)) {
                    return;
                }
                if ($node->triggers[$key] ?? false) {
                    $this->broadcast(
                        new TriggerEvent(
                            $key,
                            $this->name,
                            $node->items[$key],
                            null,
                            $node->triggers[$key]->pubsubName,
                            $node->triggers[$key]->topic
                        ),
                        $node->triggers[$key]->metadata
                    );
                }
                unset($node->items[$key]);
                $this->stateManager->save_state($this->storeName, $bucket);
                $retries = 0;
            } catch (DaprException) {
                $retries--;
            }
        } while ($retries > 0);
    }
}
