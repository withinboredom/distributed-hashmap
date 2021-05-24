# distributed-hashmap

A Distributed hashmap for Dapr.

This is a simplistic lock-free implementation of a hashmap that allows fast concurrent writes. It works almost like a
regular hashmap except instead of using memory, it uses Dapr State.

## Implementations:

- PHP [source](reference/php) [Packagist](https://packagist.org/packages/withinboredom/distributed-hashmap)
- C# [source](reference/csharp) [Nuget](https://www.nuget.org/packages/DistributedHashMap/0.0.1)

## Limitations

There are several limitations with the current version:

### Size

There's no way to get the number of keys in the hashmap without causing contention or having to iterate over every
bucket. Therefore, it simply is not a provided function. Open an issue if you want this anyway.

### Rebuilding

When creating the hashmap, please try to guess at the number of keys you may have stored and the maximum load of each
bucket. If the maximum keys are set too small or the maximum load is too large, you may end up with too much contention
or unexpected rebuilds of the hashmap.

Once `maxLoad` number of items are in a hashmap bucket, a rebuild is triggered. This means every reader/writer of the
hashmap will immediately start copying all keys from the hashmap into a new generation of the hashmap. Every
reader/writer needs to participate to ensure redundancy because they do not coordinate. Old keys from previous
generations are not deleted. While this is pretty fast, it still takes several minutes once the size of the hashmap
grows beyond ~100,000 items.

### Concurrency

In my experiments, there isn't many issues with concurrency except how different languages approach parallel tasks. For
example, forking in PHP results in very little overhead allowing over 2000 threads to concurrently write to a hashmap
with very little overhead, while C# tends to bog down after the number of actively writing threads goes over the number
of physical cores on the machine.

### Performance

Performance is pretty good but could be better, here's continuous writing for each platform:

| language | operation | number of items | threads | time (s) |
| -------- | --------- | --------------- | ------- | -------- |
| C#       | writes    | 1,000           | 10      | 1.79     |
| C#       | reads     | 1,000           | 1       | 0.94     |
| C#       | writes    | 10,000          | 10      | 15.17    |
| C#       | reads     | 10,000          | 1       | 10.62    |
| PHP      | writes    | 1,000           | 10      | 1.82     |
| PHP      | reads     | 1,000           | 1       | 1.97     |
| PHP      | writes    | 10,000          | 10      | 18.54    |
| PHP      | reads     | 10,000          | 1       | 21.97    |

The largest difference with reading in PHP is that there's no async/await, so running multithreading reads results in
around the same performance characteristics as C#, indicating that the bottleneck is with the sidecar.
