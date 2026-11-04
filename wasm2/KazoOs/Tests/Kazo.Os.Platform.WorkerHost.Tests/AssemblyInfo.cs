// The worker tests measure real timing (heartbeats, kill timeouts) and share the test commands'
// release flag: they run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
