<?php

namespace DistributedHashMap\Test;

require_once __DIR__.'/../../vendor/autoload.php';

use Dapr\App;
use Dapr\Deserialization\IDeserializer;
use Dapr\Serialization\ISerializer;
use Dapr\State\StateManager;
use DistributedHashMap\Map;

if ( ! isset($argv[1])) {
    echo "usage: integration.php [write|read]";
    exit(1);
}

const NUMBER_MESSAGES = 2000;

function fork_and_run($message, $serializer, $deserializer, $stateManager, $seed) {
    $pid = pcntl_fork();
    switch($pid) {
        case -1:
            echo "Unable to fork!\n";
            exit(1);
        case 0:
            $map = new Map(
                'php'.$seed,
                $stateManager,
                'statestore',
                $serializer,
                $deserializer,
                expectedCapacity: NUMBER_MESSAGES
            );
            $map->put('php ' . $message, $message);
            exit();
        default:
            return $pid;
    }
}

switch ($argv[1]) {
    case 'write':
        $seed = uniqid();
        if (isset($argv[2])) {
            $seed = $argv[2];
        }

        $app = App::create();
        [$serializer, $deserializer, $stateManager] = $app->run(
            fn(ISerializer $serializer, IDeserializer $deserializer, StateManager $stateManager) => [
                $serializer,
                $deserializer,
                $stateManager,
            ]
        );

        $written_messages = 0;
        $every = (int) round(NUMBER_MESSAGES * 0.1);

        $number_threads = 30;

        echo "Starting to write.\n";
        $pids = [];
        $start_time = microtime(true);
        for ($i = 0; $i < NUMBER_MESSAGES; $i++) {
            $pids[] = fork_and_run($i, $serializer, $deserializer, $stateManager, $seed);
            if(count($pids) >= $number_threads) {
                $wait = array_shift($pids);
                pcntl_waitpid($wait, $status, WNOHANG | WUNTRACED );
                if(pcntl_wifexited($status)) {
                    if(++$written_messages % $every === 0) echo "Wrote $written_messages messages.\n";
                } else {
                    $pids[] = $wait;
                }
            }
        }
        foreach($pids as $pid) {
            $wait = array_shift($pids);
            pcntl_waitpid($wait, $status, WNOHANG | WUNTRACED );
            if(pcntl_wifexited($status)) {
                if(++$written_messages % $every === 0) echo "Wrote $written_messages messages.\n";
            } else {
                $pids[] = $wait;
            }
        }
        $end_time = microtime(true);

        echo "verifying...";
        $map = new Map(
            'php'.$seed,
            $stateManager,
            'statestore',
            $serializer,
            $deserializer,
            expectedCapacity: NUMBER_MESSAGES
        );
        for($i = 0; $i < NUMBER_MESSAGES; $i++) {
            $result = $map->get('php '.$i, 'int');
            if($i !== $result) {
                echo("failed (received $result and expected $i).\n");
                exit(1);
            }
        }
        echo "verified!\n";

        $elapsed_seconds = number_format(($end_time - $start_time), 2);
        echo "Wrote $written_messages in $elapsed_seconds seconds.\n";
}
