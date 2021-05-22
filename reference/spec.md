# Hash Map Specification (V0)

This is the specification for a Version 0 of a distributed hashmap for Dapr State. It's built for debugging and ironing out the foundational aspects of a fast and performant distributed hashmap. Future versions may include binary data structures, bloom filters, etc.

## Data Types

There are two fundamental data types: The header of the map, and the nodes.

### Header

The header is a JSON object containing a few keys.

- `rebuilding`: A boolean representing whether the hashmap should be rebuilt.
- `generation`: An integer representing the hashmap's generation. Each time the hashmap is rebuilt, the generation number increases.
- `maxLoad`: An integer representing the maximum number of key/values in each node. A node exceeding this many key/values will trigger a rebuild of the hashmap.
- `version`: The version of this spec the hashmap implements.

Example Header:

```json
{
  "rebuilding": false,
  "generation": 1,
  "maxLoad": 16,
  "version": 0
}
```

### Node

Each node is a JSON object containing a few keys.

- `items`: A map of key/values

Example Node:

```json
{
  "items": {
    "key": "value"
  }
}
```

### NodeMeta

Example NodeMeta:

```json
{
  "size": 1
}
```

## Operations

Implementations should strive to behave as much like ordinary hashmaps in their native language. An implementation must implement the following operations:

1. put(key, value): void
2. get(key): any
3. contains(key): bool
4. remove(key): void
5. rebuild(): void
6. size(): int

### Put

The steps to put an item into the hashmap is as follows:

1. Get the header
2. If the map is rebuilding, perform the `rebuild` operation
3. Calculate the bucket the key goes in
4. Read the bucket, or create a new one
5. Insert the key/value into the bucket
6. Update the bucket, retry as needed
7. If the size of the bucket is larger than `maxLoad`, perform the `rebuild` operation
8. Update the nodemeta with the new size.

Puts require 2 reads and 2 writes.

### Get

The steps to get an item from the hashmap is as follows:

1. Get the header
2. If the map is rebuilding, perform the `rebuild` operation
3. Calculate the bucket for the key
4. Read the bucket and return the key/value

Gets require 2 reads.

### Contains

For version 0, simply perform the `get` operation and return `true` if it exists.

### Remove

For version 0, simply `put` and remove the key/value on step 5.

### Size

Sizes are stored in NodeMeta and thus iterating over all NodeMeta must be done. Size is stored in NodeMeta to avoid contention in the Header when adding/removing keys.

### Rebuild

All producers and consumers are expected to participate in a race to rebuild. In this way, there is guaranteed to be be redundancy and finish as quickly as possible.

Here are the basic steps to rebuild:

1. Update the header to indicate a rebuild is in process
1. Starting from a random Node
1. Iterate over all key/values, `put` into the next generation
1. Repeat through all Nodes, skipping keys already `put` into the new generation
1. Once back where you started, increment the generation and set rebuilding to `false`
