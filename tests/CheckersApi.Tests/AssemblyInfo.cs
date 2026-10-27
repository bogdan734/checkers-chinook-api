using Xunit;

// The timing assertions (strong answer under 600 ms, 504 within the hard limit) must not compete with other CPU-bound tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
