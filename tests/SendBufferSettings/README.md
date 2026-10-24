# Send buffer settings regression

Run from the repository root with .NET 8 and loopback TCP permission:

```sh
dotnet run --project tests/SendBufferSettings/SendBufferSettings.csproj -c Release
```

Tests use the actual sender, settings and channel implementations. They check
default/invalid values, valid requests, sender isolation and later connections,
unchanged receiver/discovery-server buffers, and requested/actual-size logging.
OS buffer adjustments are compared with a reference socket. Discovery is replaced
by a local fixture; no sources are advertised and no native codec is required.
All network waits are bounded and settings use a temporary directory.
