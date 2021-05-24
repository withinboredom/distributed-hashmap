<?php

namespace DistributedHashMap;

abstract class TriggerType {
    public const ON_KEY_CHANGE = 1;
    public const ON_KEY_REMOVE = 2;
    public const ON_CHANGE = 4;
    public const ON_REMOVE = 8;
}
