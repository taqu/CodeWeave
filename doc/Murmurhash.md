byte\[] data = Guid.NewGuid().ToByteArray();

HashAlgorithm murmur128 = MurmurHash.Create128(managed: false); // returns a 128-bit algorithm using "unsafe" code with default seed

byte\[] hash = murmur128.ComputeHash(data);



// you can also use a seed to affect the hash

HashAlgorithm seeded128 = MurmurHash.Create128(seed: 3475832); // returns a managed 128-bit algorithm with seed

byte\[] seedResult = murmur128.ComputeHash(data);

