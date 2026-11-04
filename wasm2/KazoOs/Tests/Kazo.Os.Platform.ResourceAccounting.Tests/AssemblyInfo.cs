// The context-switch hook is process-wide (one AsyncLocal for the whole runtime): tests that install
// it must never overlap with tests that open execution scopes.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
