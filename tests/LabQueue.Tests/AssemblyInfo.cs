using Xunit;

// Each test class starts its own Postgres container and publishes its connection string
// through process environment variables (Program.cs reads Jwt:Key before builder.Build(),
// so nothing later can supply it). Process-wide state means classes must not overlap.
//
// It also keeps the concurrency reproducer from competing with another class for CPU,
// which would stagger the fifty requests it needs to release together.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
