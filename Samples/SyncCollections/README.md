# Sync collections standalone demo

Run from the repository root:

```powershell
dotnet run --project Samples/SyncCollections/SyncCollections.csproj -c Release
```

The build target invokes the shared Cecil weaver. The program starts two separate
Host/Client runtimes over real local TCP/DMTP, binds explicitly constructed objects,
receives List/Dictionary/HashSet snapshots, invokes a Client-to-Host RPC, observes
collection/scalar Hooks, and verifies that a direct Client mutation is rejected.

Expected final line: `Sync collections demo passed.`

This sample intentionally trusts its one localhost client. Production admission and
business authorization remain application-owned. The runtime does not marshal callbacks
to a UI thread; the sample only prints to the console.

See [the guide](../../Docs/sync-collections-guide.md) for batching, DTO ownership,
fingerprints, lifecycle, limits and the protocol upgrade requirement.
