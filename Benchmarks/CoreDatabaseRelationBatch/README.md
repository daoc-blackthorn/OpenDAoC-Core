# CoreDatabase relation batching benchmark

Run the synthetic SQLite comparison in Release mode:

```powershell
dotnet run --project Benchmarks/CoreDatabaseRelationBatch/CoreDatabaseRelationBatch.csproj -c Release
```

To benchmark 1,000 real `DbAccount` relation loads against MySQL or MariaDB, provide a connection string through the environment. The benchmark only issues `SELECT` statements and registers table metadata without running schema checks.

```powershell
$env:CORE_DATABASE_MYSQL_CONNECTION_STRING = "server=localhost;database=...;user id=...;password=..."
dotnet run --project Benchmarks/CoreDatabaseRelationBatch/CoreDatabaseRelationBatch.csproj -c Release -- --dbaccount
```

The `--mysql-smoke` argument performs a small parameterized multi-result compatibility check against the configured server.
