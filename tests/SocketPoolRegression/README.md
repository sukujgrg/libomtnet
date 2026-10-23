# Socket send-pool regression

Run from the repository root with .NET 8 and loopback TCP permission:

```sh
dotnet run --project tests/SocketPoolRegression/SocketPoolRegression.csproj -c Release
```

The executable links the actual pool/base implementation and has no external
package dependencies. It repeats null/disposed-socket sends beyond the four-slot
pool size, checks byte-identical small/large transfers, and checks peer reset.
All network waits are bounded. Before the ownership repair, the null and
synchronous-failure cases each lose a reusable slot; after repair all four cases
pass. The success and peer-reset cases also pass on the original implementation.
The logging stub is confined to this test project and excluded from the SDK.
