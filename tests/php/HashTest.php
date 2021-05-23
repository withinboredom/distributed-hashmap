<?php

namespace DistributedHashMap\Test;

use lastguest\Murmur;
use PHPUnit\Framework\TestCase;

class HashTest extends TestCase {
    public function testHash() {
        $hash = Murmur::hash3_int('test');
        $this->assertSame(3127628307, $hash);
    }
}
