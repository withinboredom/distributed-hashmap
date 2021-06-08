<?php

use Dapr\PubSub\Subscription;

return [
    'dapr.subscriptions' => [new Subscription('pubsub', 'changes', '/change')],
];
