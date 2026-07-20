using Xunit;

// These tests are serial by nature, so the runner is told so rather than left to
// discover it intermittently.
//
// xUnit's default is one collection per class run in parallel. Every class here
// creates and drops its own database on a single shared server, and several drive
// DacFx comparisons or spawn a child `schemorph mcp` process per test. Run together,
// they starve that one server: logins time out in the post-login phase (seen in
// TestDatabase.Dispose, ~15s) and DacFx's GenerateScript fails, which the provider
// correctly degrades to SCHEMORPH002 — a null `sql` on the plan, which then trips
// assertions that read it. Both failures move between runs and neither reproduces
// when a class runs alone.
//
// The contention is a property of the harness, not of the tool: schemorph is a CLI
// that performs one operation per process and promises nothing about concurrent
// comparisons. Serializing the suite states the constraint the tests always had.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
