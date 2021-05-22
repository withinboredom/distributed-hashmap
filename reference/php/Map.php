<?php

namespace DistributedHashMap;

use Dapr\Deserialization\Deserializer;
use Dapr\exceptions\DaprException;
use Dapr\Serialization\Serializer;
use Dapr\State\StateItem;
use Dapr\State\StateManager;
use DistributedHashMap\Internal\Header;
use DistributedHashMap\Internal\MapInterface;
use DistributedHashMap\Internal\Node;
use DistributedHashMap\Internal\NodeMeta;
use lastguest\Murmur;

class Map implements MapInterface
{
    private StateItem $header;

    public function __construct(
        private string $name,
        private StateManager $stateManager,
        private string $store_name,
        private Serializer $serializer,
        private Deserializer $deserializer
    ) {
    }

    public function put(string $key, mixed $value): void
    {
        $this->putRaw($key, $this->serializer->as_json($value));
    }

    private function putRaw(string $key, string $value): void
    {
        $header     = $this->getHeaderAndMaybeRebuild();
        $bucket_key = $this->getBucketKey($key);
        $nodeItem   = $this->stateManager->load_state($this->store_name, $bucket_key, default_value: new Node());
        $node       = $this->getNodeFromItem($nodeItem);
        if ($node->items[$key] && $node->items[$key] === $value) {
            return;
        }
        $node->items[$key] = $value;
        $nodeItem->value   = $this->serializer->as_json($node);
        $this->stateManager->save_state($this->store_name, $nodeItem);

        $this->updateNodeMeta($bucket_key, new NodeMeta(count($node->items)));
        if (count($node->items) > $header->maxLoad) {
            $this->rebuild();
        }
    }

    private function getHeaderAndMaybeRebuild(): Header
    {
        if ($this->header && $this->getHeaderFromHeader()->rebuilding) {
            return $this->getHeaderFromHeader();
        }

        $header = $this->getHeaderFromStore();
        if ($header->rebuilding) {
            $this->rebuild();

            return $this->getHeaderFromStore();
        }

        return $header;
    }

    private function getHeaderFromHeader(): Header
    {
        return $this->header->value;
    }

    private function getHeaderFromStore(): Header
    {
        $headerKey    = 'DHMHeader_'.$this->name;
        $this->header = $this->stateManager->load_state($this->store_name, $headerKey, default_value: new Header());
        if ( ! $this->header->value instanceof Header) {
            $this->header->value = $this->deserializer->from_json(Header::class, $this->header->value);
        }

        return $this->header->value;
    }

    public function rebuild(): void
    {
        $header = $this->getHeaderFromHeader();
        if ( ! $header->rebuilding) {
            $header->rebuilding = true;
            $this->stateManager->save_state($this->store_name, $this->header);
        }
        $nextGenerationHeader = clone $header;
        $nextGenerationHeader->generation++;
        $pointer                       = $start_point = rand(0, $this->getMapSize());
        $nextGeneration                = new self(
            $this->name,
            $this->stateManager,
            $this->store_name,
            $this->serializer,
            $this->deserializer
        );
        $nextGeneration->header        = clone $this->header;
        $nextGeneration->header->value = $nextGenerationHeader;

        do {
            $bucket = $this->stateManager->load_state(
                $this->store_name,
                'DHM_'.$this->name.'_'.$this->getHeaderFromHeader()->generation.'_'.$pointer
            );
            $node   = $this->getNodeFromItem($bucket);
            foreach ($node->items as $key => $value) {
                try {
                    $nextGeneration->putRaw($key, $value);
                } catch (DaprException $exception) {
                    // try again
                    $pointer--;
                    break;
                }
            }

            $pointer = ($pointer + 1) % $this->getMapSize();
        } while ($pointer !== $start_point);

        $header->rebuilding = false;
        $header->generation++;
        $this->header->value = $this->serializer->as_json($header);
        $this->stateManager->save_state($this->store_name, $this->header);
    }

    private function getMapSize(): int
    {
        return pow(2, 7 + $this->getHeaderFromHeader()->generation);
    }

    private function getNodeFromItem(StateItem $item): Node
    {
        if ( ! $item->value instanceof Node) {
            $item->value = $this->deserializer->from_json(Node::class, $item->value);
        }

        return $item->value;
    }

    private function getBucketKey(string $key): string
    {
        return 'DHM_'.$this->name.'_'.$this->getHeaderFromHeader()->generation.'_'.$this->getBucket($key);
    }

    private function getBucket(string $key): int
    {
        return Murmur::hash3_int($key) % $this->getMapSize();
    }

    private function updateNodeMeta(string $key, NodeMeta $meta): void
    {
        $key = $key.'_meta';
        $this->stateManager->save_state($this->store_name, new StateItem($key, $this->serializer->as_json($meta)));
    }

    public function get(string $key, ?string $type = null): mixed
    {
        $this->getHeaderAndMaybeRebuild();
        $bucket_key = $this->getBucketKey($key);
        $bucket     = $this->stateManager->load_state($this->store_name, $bucket_key, new Node());
        $node       = $this->getNodeFromItem($bucket);
        $value      = $node->items[$key] ?? null;
        if ($value === null || ! $type) {
            return $value;
        }

        return $this->deserializer->from_json($type, $value);
    }

    public function contains(string $key): bool
    {
        $this->getHeaderAndMaybeRebuild();
        $bucket = $this->stateManager->load_state($this->store_name, $this->getBucketKey($key), new Node());
        $node   = $this->getNodeFromItem($bucket);

        return array_key_exists($key, $node->items);
    }

    public function remove(string $key): void
    {
        $this->getHeaderAndMaybeRebuild();
        $bucket_key = $this->getBucketKey($key);
        $bucket     = $this->stateManager->load_state($this->store_name, $bucket_key, default_value: new Node());
        $node       = $this->getNodeFromItem($bucket);
        unset($node->items[$key]);
        $bucket->value = $this->serializer->as_json($node);
        $this->stateManager->save_state($this->store_name, $bucket);

        $this->updateNodeMeta($bucket_key, new NodeMeta(count($node->items)));
    }

    public function size(): int
    {
        // todo: this is terrible performance
        $size = 0;
        foreach ($this->iterateMetaKeys() as $metaKey) {
            $bucket = $this->stateManager->load_state($this->store_name, $metaKey, new NodeMeta());
            if ( ! $bucket->value instanceof NodeMeta) {
                $this->deserializer->from_json(NodeMeta::class, $bucket->value);
            }
            $size += $bucket->value->size;
        }

        return $size;
    }

    private function iterateMetaKeys(): \Generator
    {
        $prefix = 'DHM_'.$this->name.'_'.$this->getHeaderFromHeader()->generation.'_';
        $this->getHeaderAndMaybeRebuild();
        $size = $this->getMapSize();
        for ($i = 0; $i < $size; $i++) {
            yield $prefix.$i.'_meta';
        }
    }
}
