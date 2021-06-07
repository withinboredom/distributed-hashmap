<?php

namespace DistributedHashMap\Subscriptions;

require_once __DIR__.'/../../vendor/autoload.php';

use Dapr\App;
use Dapr\Attributes\FromBody;
use Dapr\Deserialization\IDeserializer;
use Dapr\PubSub\CloudEvent;
use Dapr\Serialization\ISerializer;
use Dapr\State\StateManager;
use DI\ContainerBuilder;
use Psr\Log\LoggerInterface;

$app = App::create(configure: fn(ContainerBuilder $builder) => $builder->addDefinitions(__DIR__.'/config.php'));
$app->post(
    '/change',
    function (
        #[FromBody] CloudEvent $event,
        StateManager $stateManager,
        ISerializer $serializer,
        IDeserializer $deserializer,
        LoggerInterface $logger
    ) {
        file_put_contents("php://stderr", print_r($event->data, true));
        [$lang, $key] = explode(' ', $event->data['key']);
        if ( ! is_dir("/tmp/$lang")) {
            @mkdir("/tmp/$lang");
        }
        $created = $event->data['previousValue'] === null;
        $deleted = $event->data['newValue'] === null;
        $changed = $created ? 'created' : ($deleted ? 'deleted' : 'changed');
        if(!is_dir("/tmp/$lang/$changed")) {
            @mkdir("/tmp/$lang/$changed");
        }
        touch("/tmp/$lang/$changed/$key");
    }
);
$app->get(
    "/stats/{lang}",
    function (string $lang) {
        $lang = rawurldecode($lang);

        return [
            'createdCount' => count(glob("/tmp/$lang/created/*")),
            'deletedCount' => count(glob("/tmp/$lang/deleted/*")),
            'changedCount' => count(glob("/tmp/$lang/changed/*")),
        ];
    }
);
$app->start();
