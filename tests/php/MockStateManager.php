<?php

namespace DistributedHashMap\Test;

use Dapr\consistency\Consistency;
use Dapr\consistency\EventualFirstWrite;
use Dapr\consistency\EventualLastWrite;
use Dapr\consistency\StrongFirstWrite;
use Dapr\consistency\StrongLastWrite;
use Dapr\exceptions\DaprException;
use Dapr\State\StateItem;
use Dapr\State\StateManager;

class MockStateManager extends StateManager
{
    /**
     * @var StateItem[]
     */
    public $state = [];

    public function __construct()
    {
    }

    public function save_state(string $store_name, StateItem $item): void
    {
        if ( ! $item->etag) {
            $item->etag = $this->get_etag();
        }

        switch ($item->consistency) {
            case new StrongFirstWrite():
            case new EventualFirstWrite():
                if (isset($this->state[$item->key]) && $item->etag !== $this->state[$item->key]->etag) {
                    throw new DaprException('etag mismatch');
                }
            case new StrongLastWrite():
            case new EventualLastWrite():
                $this->state[$item->key] = $item;
                break;
            default:
                throw new \LogicException('did not match');
        }
    }

    private function get_etag(): int
    {
        static $etag = 1;

        return $etag++;
    }

    public function load_state(
        string $store_name,
        string $key,
        mixed $default_value = null,
        array $metadata = [],
        ?Consistency $consistency = null
    ): StateItem {
        return $this->state[$key] ?? new StateItem(
                $key,
                $default_value,
                $consistency ?? new EventualLastWrite(),
                $this->get_etag(),
                $metadata
            );
    }
}
