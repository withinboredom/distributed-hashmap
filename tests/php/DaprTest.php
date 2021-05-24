<?php

namespace DistributedHashMap\Test;

use Dapr\App;
use Dapr\Deserialization\IDeserializer;
use Dapr\Serialization\ISerializer;
use Dapr\State\StateManager;
use DistributedHashMap\Map;
use PHPUnit\Framework\TestCase;

class DaprTest extends TestCase
{
    private App $app;
    private ISerializer $serializer;
    private IDeserializer $deserializer;
    private StateManager $stateManager;

    public function setUp(): void
    {
        parent::setUp();
        $this->app = App::create();
        set_error_handler(
            function ($err_no, $err_str, $err_file, $err_line) {
                echo "Error $err_str on $err_file:$err_line\n";
                die();
            }
        );
        [$this->serializer, $this->deserializer, $this->stateManager] = $this->app->run(
            fn(ISerializer $serializer, IDeserializer $deserializer, StateManager $stateManager) => [
                $serializer,
                $deserializer,
                $stateManager,
            ]
        );
    }

    public function testLoadNullValue()
    {
        $map = $this->getMap();
        $this->assertNull($map->get('noValue'));
    }

    private function getMap(): Map
    {
        return new Map($this->getName(), $this->stateManager, 'statestore', $this->serializer, $this->deserializer);
    }

    public function testSaveAndLoadValue()
    {
        $map = $this->getMap();
        $map->put('test', 'value');
        $this->assertTrue(isset($map['test']));
        $result = $map->get('test', 'string');
        $this->assertSame('value', $result);
    }

    public function testSaveAndRemoveValue() {
        $map = $this->getMap();
        $map->put('test', 'value');
        $this->assertTrue(isset($map['test']));
        $map->remove('test');
        $this->assertFalse(isset($map['test']));
    }

    public function testRebuildingSlow() {
        $map = $this->getMap();
        for($i = 0; $i < 200; $i++) {
            $map->put('key_' . $i, $i);
        }
        $map->rebuild();
        for($i = 0; $i < 200; $i++) {
            $this->assertSame($i, $map->get('key_'.$i, 'int'));
        }
    }
}
