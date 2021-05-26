<?php

namespace DistributedHashMap\Test;

use Dapr\Deserialization\DeserializationConfig;
use Dapr\Deserialization\Deserializer;
use Dapr\Serialization\SerializationConfig;
use Dapr\Serialization\Serializer;
use DistributedHashMap\Map;
use PHPUnit\Framework\TestCase;
use Psr\Log\NullLogger;

class FunctionTest extends TestCase
{
    private Serializer $serializer;
    private Deserializer $deserializer;
    private MockStateManager $stateManager;

    public function setUp(): void
    {
        parent::setUp();
        $this->serializer   = new Serializer(new SerializationConfig(), new NullLogger());
        $this->deserializer = new Deserializer(new DeserializationConfig(), new NullLogger());
        $this->stateManager = new MockStateManager();
    }

    public function testCreate(): void
    {
        $t1 = $this->getMap();
        $t1->put('test', ['value']);
        $this->assertTrue(isset($this->stateManager->state['DHM_test_1_19']), 'bucket should be created');
        $this->assertSame(['value'], $t1->get('test', 'array'));
    }

    private function getMap(string $name = 'test'): Map
    {
        return new Map($name, $this->stateManager, 'store', $this->serializer, $this->deserializer, new NullLogger());
    }

    public function testCountAndResize(): void
    {
        $map = $this->getMap();
        for ($i = 0; $i < 500; $i++) {
            $map->put('key.'.$i, $i);
            $this->assertTrue($map->contains('key.'.$i));
        }
        $map->rebuild();
        for ($i = 0; $i < 500; $i++) {
            $result = $map->get('key.'.$i, 'int');
            $this->assertSame($i, $result);
        }
    }
}
