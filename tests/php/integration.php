<?php

namespace DistributedHashMap\Test;

require_once __DIR__.'/../../vendor/autoload.php';

use Dapr\App;
use Dapr\Deserialization\IDeserializer;
use Dapr\Serialization\ISerializer;
use Dapr\State\StateManager;
use DistributedHashMap\Map;
use Psr\Log\NullLogger;

if ( ! isset($argv[1])) {
    echo "usage: integration.php [write|read]";
    exit(1);
}

const NUMBER_MESSAGES = 2000;



function fork_and_run($message, $serializer, $deserializer, $stateManager, $seed, $delete = false)
{
    $pid = pcntl_fork();
    set_error_handler(
        function ($err_no, $err_str, $err_file, $err_line) {
            echo "ERROR: $err_str in $err_file:$err_line";
            exit(1);
        }
    );
    switch ($pid) {
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
                new NullLogger(),
            //expectedCapacity: NUMBER_MESSAGES
            );
            if ( ! $delete) {
                //echo "Subscribing to $message...";
                try {
                    $map->rebuildCallback = fn($status, $reason) => match ($status) {
                        'init' => print("Rebuilding (subscribe) at $message: $reason\n"),
                        'finish' => print("Finished (subscribe) at $message: $reason\n")
                    };
                    $map->subscribe('php '.$message, 'pubsub', 'changes');
                } catch (\Throwable $exception) {
                    echo "Failed to subscribe due to {$exception->getMessage()}\n";
                    throw $exception;
                }
                //echo "done\nPutting $message...";
                try {
                    $map->rebuildCallback = fn($status, $reason) => match ($status) {
                        'init' => print("Rebuilding (put) at $message: $reason\n"),
                        'finish' => print("Finished (put) at $message: $reason\n")
                    };
                    $map->put('php '.$message, $message);
                } catch (\Throwable $exception) {
                    echo "Failed to put due to {$exception->getMessage()}\n";
                    throw $exception;
                }
                //echo "done with $message\n";
            } else {
                $map->remove('php '.$message);
            }
            exit();
        default:
            return $pid;
    }
}

switch ($argv[1]) {
    case 'validate':

        $timeout = 500;
        $start = time();

        do {
            $failed = false;
            sleep(1);
            foreach (['c#', 'php'] as $lang) {
                $lang_encoded = rawurlencode($lang);
                $total        = number_format(NUMBER_MESSAGES, 0);
                $stats        = json_decode(file_get_contents("http://localhost/stats/$lang_encoded"), true);
                $created      = number_format($stats['createdCount'] ?? 0, 0);
                $dupes        = number_format($stats['createdDupeCount'] ?? 0, 0);
                if ($created !== $total) {
                    echo "$lang broadcast $created events and expected $total\n";
                    $failed = true;
                } elseif ($dupes) {
                    echo "$lang broadcast $dupes duplicate events, expected 0\n";
                    $failed = true;
                } else {
                    echo "$lang broadcast $created events with no duplicates\n";
                }
            }
        } while(time() - $start < $timeout && $failed);
        exit((int) $failed);
    case 'read':
        $seed = uniqid();
        if (isset($argv[2])) {
            $seed = $argv[2];
        }
        $app = App::create();
        set_error_handler(
            function ($err_no, $err_str, $err_file, $err_line) {
                echo "ERROR: $err_str in $err_file:$err_line";
                exit(1);
            }
        );
        [$serializer, $deserializer, $stateManager] = $app->run(
            fn(ISerializer $serializer, IDeserializer $deserializer, StateManager $stateManager) => [
                $serializer,
                $deserializer,
                $stateManager,
            ]
        );

        echo "Starting verification\n";

        $langs = ['php', 'c#'];

        foreach ($langs as $lang) {
            $map = new Map(
                $lang.$seed,
                $stateManager,
                'statestore',
                $serializer,
                $deserializer,
                new NullLogger(),
            //expectedCapacity: NUMBER_MESSAGES
            );
            echo "Verifying $lang: ";
            $start_time = microtime(true);
            for ($i = 0; $i < NUMBER_MESSAGES; $i++) {
                $verification = $map->get("$lang $i", 'int');
                $contains     = $map->contains("$lang $i");
                if ($i !== $verification || ! $contains) {
                    echo "Failed read verification for $lang and got $verification instead of $i\n";
                    exit(1);
                }
            }
            $elapsed_seconds = microtime(true) - $start_time;
            echo "Done in $elapsed_seconds seconds\n";
        }

        break;
    case 'delete':
        $delete = true;
    case 'write':
        $seed = uniqid();
        if (isset($argv[2])) {
            $seed = $argv[2];
        }

        $app = App::create();
        set_error_handler(
            function ($err_no, $err_str, $err_file, $err_line) {
                echo "ERROR: $err_str in $err_file:$err_line";
                exit(1);
            }
        );
        [$serializer, $deserializer, $stateManager] = $app->run(
            fn(ISerializer $serializer, IDeserializer $deserializer, StateManager $stateManager) => [
                $serializer,
                $deserializer,
                $stateManager,
            ]
        );

        $written_messages = 0;
        $every            = (int)round(NUMBER_MESSAGES * 0.1);

        $number_threads = 10;

        echo "Starting to write.\n";
        $pids       = [];
        $start_time = microtime(true);
        for ($i = 0; $i < NUMBER_MESSAGES; $i++) {
            $pids[] = fork_and_run($i, $serializer, $deserializer, $stateManager, $seed, delete: $delete ?? false);
            waiting:
            if (count($pids) >= $number_threads) {
                $wait = array_shift($pids);
                pcntl_waitpid($wait, $status, WUNTRACED);
                if (pcntl_wifexited($status)) {
                    if (pcntl_wexitstatus($status) !== 0) {
                        echo "Child failed with a non-zero exit!";
                        exit(1);
                    }
                    if (++$written_messages % $every === 0) {
                        echo "Wrote $written_messages messages.\n";
                    }
                } else {
                    $pids[] = $wait;
                    goto waiting;
                }
            }
        }
        foreach ($pids as $pid) {
            $wait = array_shift($pids);
            pcntl_waitpid($wait, $status, WNOHANG | WUNTRACED);
            if (pcntl_wifexited($status)) {
                if (++$written_messages % $every === 0) {
                    echo "Wrote $written_messages messages.\n";
                }
            } else {
                $pids[] = $wait;
            }
        }
        $end_time = microtime(true);

        $elapsed_seconds = number_format(($end_time - $start_time), 2);
        echo "Wrote $written_messages in $elapsed_seconds seconds.\n";

        echo "verifying...";
        $map = new Map(
            'php'.$seed,
            $stateManager,
            'statestore',
            $serializer,
            $deserializer,
            new NullLogger(),
        );

        $start_time = microtime(true);
        for ($i = 0; $i < NUMBER_MESSAGES; $i++) {
            $result = $map->get('php '.$i, 'int');
            if ($i !== $result) {
                echo("failed (received $result and expected $i).\n");
                exit(1);
            }
        }
        $elapsed_seconds = number_format(microtime(true) - $start_time, 2);
        echo "verified in $elapsed_seconds seconds!\n";
}
