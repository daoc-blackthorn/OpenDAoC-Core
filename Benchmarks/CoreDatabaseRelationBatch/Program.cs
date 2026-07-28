using System.Data.Common;
using System.Diagnostics;
using DOL.Database;
using DOL.Database.Attributes;
using DOL.Database.Handlers;
using MySqlConnector;

if (args is ["--mysql-smoke"])
{
    RunMySqlSmokeTest();
    return;
}

if (args is ["--dbaccount"])
{
    RunDbAccountBenchmark();
    return;
}

const int ParentCount = 20;
const int WarmupIterations = 200;
const int MeasurementIterations = 2_000;
const int MeasurementRounds = 5;

string batchedFile = Path.Combine(Path.GetTempPath(), $"dol-relation-batched-{Guid.NewGuid():N}.sqlite");
string sequentialFile = Path.Combine(Path.GetTempPath(), $"dol-relation-sequential-{Guid.NewGuid():N}.sqlite");

try
{
    CountingSqliteObjectDatabase batched = CreateDatabase<CountingSqliteObjectDatabase>(batchedFile);
    SequentialSqliteObjectDatabase sequential = CreateDatabase<SequentialSqliteObjectDatabase>(sequentialFile);
    Seed(batched);
    Seed(sequential);

    BenchmarkResult sequentialResult = RunBenchmark("Sequential", sequential);
    BenchmarkResult batchedResult = RunBenchmark("Multi-result batch", batched);

    Console.WriteLine("CoreDatabase relation loading benchmark (SQLite, Release)");
    Console.WriteLine($"Graph: 1 parent, 3 sibling relations, 1 nested relation; {MeasurementIterations:N0} operations x {MeasurementRounds} rounds");
    Console.WriteLine();
    Console.WriteLine($"{"Mode",-20} {"Mean",12} {"P50",12} {"P95",12} {"CPU/op",12} {"Allocated/op",16} {"Connections/op",16}");
    Print(sequentialResult);
    Print(batchedResult);
    Console.WriteLine();
    Console.WriteLine($"Latency change:    {(batchedResult.MeanMicroseconds / sequentialResult.MeanMicroseconds - 1) * 100:F1}%");
    Console.WriteLine($"CPU change:        {(batchedResult.CpuMicroseconds / sequentialResult.CpuMicroseconds - 1) * 100:F1}%");
    Console.WriteLine($"Allocation change: {(batchedResult.AllocatedBytes / sequentialResult.AllocatedBytes - 1) * 100:F1}%");
}
finally
{
    TryDelete(batchedFile);
    TryDelete(sequentialFile);
}

static T CreateDatabase<T>(string databaseFile) where T : CountingSqliteObjectDatabase
{
    T database = (T)Activator.CreateInstance(typeof(T), $"Data Source={databaseFile};Version=3;Pooling=False");
    database.RegisterDataObject(typeof(BenchmarkGrandchild));
    database.RegisterDataObject(typeof(BenchmarkFirstChild));
    database.RegisterDataObject(typeof(BenchmarkSecondChild));
    database.RegisterDataObject(typeof(BenchmarkThirdChild));
    database.RegisterDataObject(typeof(BenchmarkParent));
    return database;
}

static void Seed(IObjectDatabase database)
{
    for (int parentIndex = 0; parentIndex < ParentCount; parentIndex++)
    {
        string parentId = $"parent-{parentIndex}";
        database.AddObject(new BenchmarkParent { Id = parentId });

        for (int childIndex = 0; childIndex < 5; childIndex++)
        {
            string childId = $"first-{parentIndex}-{childIndex}";
            database.AddObject(new BenchmarkFirstChild { Id = childId, ParentId = parentId, Value = childIndex });

            for (int grandchildIndex = 0; grandchildIndex < 2; grandchildIndex++)
            {
                database.AddObject(new BenchmarkGrandchild
                {
                    Id = $"grandchild-{parentIndex}-{childIndex}-{grandchildIndex}",
                    ChildId = childId,
                    Value = grandchildIndex
                });
            }
        }

        for (int childIndex = 0; childIndex < 3; childIndex++)
            database.AddObject(new BenchmarkSecondChild { Id = $"second-{parentIndex}-{childIndex}", ParentId = parentId, Value = childIndex });

        for (int childIndex = 0; childIndex < 4; childIndex++)
            database.AddObject(new BenchmarkThirdChild { Id = $"third-{parentIndex}-{childIndex}", ParentId = parentId, Value = childIndex });
    }
}

static BenchmarkResult RunBenchmark(string name, CountingSqliteObjectDatabase database)
{
    int keyIndex = 0;

    for (int i = 0; i < WarmupIterations; i++)
        Consume(Load(database, ref keyIndex));

    List<BenchmarkResult> rounds = new(MeasurementRounds);

    for (int round = 0; round < MeasurementRounds; round++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        database.ResetConnectionCount();

        long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
        TimeSpan cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        long[] samples = new long[MeasurementIterations];
        long totalStart = Stopwatch.GetTimestamp();
        int checksum = 0;

        for (int i = 0; i < MeasurementIterations; i++)
        {
            long operationStart = Stopwatch.GetTimestamp();
            checksum += Consume(Load(database, ref keyIndex));
            samples[i] = Stopwatch.GetTimestamp() - operationStart;
        }

        long totalTicks = Stopwatch.GetTimestamp() - totalStart;
        TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuStart;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
        Array.Sort(samples);

        rounds.Add(new BenchmarkResult(
            name,
            TicksToMicroseconds(totalTicks) / MeasurementIterations,
            TicksToMicroseconds(samples[MeasurementIterations / 2]),
            TicksToMicroseconds(samples[(int)(MeasurementIterations * 0.95)]),
            cpu.TotalMicroseconds / MeasurementIterations,
            (double)allocated / MeasurementIterations,
            (double)database.ConnectionCount / MeasurementIterations,
            checksum));
    }

    return rounds.OrderBy(result => result.MeanMicroseconds).ElementAt(MeasurementRounds / 2);
}

static BenchmarkParent Load(CountingSqliteObjectDatabase database, ref int keyIndex)
{
    string key = $"parent-{keyIndex++ % ParentCount}";
    return database.SelectObject<BenchmarkParent>(DB.Column(nameof(BenchmarkParent.Id)).IsEqualTo(key));
}

static int Consume(BenchmarkParent parent)
{
    return parent.FirstChildren.Length
        + parent.FirstChildren.Sum(child => child.Grandchildren.Length)
        + parent.SecondChildren.Length
        + parent.ThirdChildren.Length;
}

static double TicksToMicroseconds(long ticks) => ticks * 1_000_000d / Stopwatch.Frequency;

static void Print(BenchmarkResult result)
{
    Console.WriteLine($"{result.Name,-20} {result.MeanMicroseconds,9:F2} us {result.P50Microseconds,9:F2} us {result.P95Microseconds,9:F2} us {result.CpuMicroseconds,9:F2} us {result.AllocatedBytes,13:N0} B {result.Connections,16:F2}");
}

static void TryDelete(string path)
{
    if (File.Exists(path))
        File.Delete(path);
}

static void RunMySqlSmokeTest()
{
    string connectionString = Environment.GetEnvironmentVariable("CORE_DATABASE_MYSQL_CONNECTION_STRING");

    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("CORE_DATABASE_MYSQL_CONNECTION_STRING must contain the MySQL connection string.");

    using MySqlConnection connection = new(connectionString);
    connection.Open();
    using MySqlCommand command = connection.CreateCommand();
    command.CommandText = "SELECT @q0_a; SELECT @q1_a";
    command.Parameters.AddWithValue("@q0_a", 17);
    command.Parameters.AddWithValue("@q1_a", 29);
    using MySqlDataReader reader = command.ExecuteReader();

    if (!reader.Read() || reader.GetInt32(0) != 17 || !reader.NextResult() || !reader.Read() || reader.GetInt32(0) != 29)
        throw new InvalidOperationException("MySqlConnector did not return the expected ordered result sets.");

    Console.WriteLine("MySQL multi-result smoke test passed.");
}

static void RunDbAccountBenchmark()
{
    const int warmupIterations = 100;
    const int measurementIterations = 1_000;
    string connectionString = Environment.GetEnvironmentVariable("CORE_DATABASE_MYSQL_CONNECTION_STRING");

    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("CORE_DATABASE_MYSQL_CONNECTION_STRING must contain the MySQL connection string.");

    string[] accountNames;

    using (MySqlConnection connection = new(connectionString))
    {
        connection.Open();
        using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT `Name` FROM `Account` ORDER BY `Name` LIMIT 1000";
        using MySqlDataReader reader = command.ExecuteReader();
        List<string> names = [];

        while (reader.Read())
            names.Add(reader.GetString(0));

        accountNames = names.ToArray();
    }

    if (accountNames.Length == 0)
        throw new InvalidOperationException("The Account table contains no rows to benchmark.");

    CountingMySqlObjectDatabase batched = new(connectionString);
    SequentialMySqlObjectDatabase sequential = new(connectionString);
    RegisterDbAccountGraph(batched);
    RegisterDbAccountGraph(sequential);

    for (int i = 0; i < warmupIterations; i++)
    {
        ConsumeAccount(sequential.SelectObject<DbAccount>(DB.Column(nameof(DbAccount.Name)).IsEqualTo(accountNames[i % accountNames.Length])));
        ConsumeAccount(batched.SelectObject<DbAccount>(DB.Column(nameof(DbAccount.Name)).IsEqualTo(accountNames[i % accountNames.Length])));
    }

    DbAccountBenchmarkResult sequentialResult = MeasureDbAccounts("Sequential", sequential, accountNames, measurementIterations);
    DbAccountBenchmarkResult batchedResult = MeasureDbAccounts("Multi-result batch", batched, accountNames, measurementIterations);

    Console.WriteLine("CoreDatabase DbAccount relation loading benchmark (MariaDB/MySQL, Release)");
    Console.WriteLine($"Measured loads per mode: {measurementIterations:N0}; distinct accounts sampled: {accountNames.Length:N0}");
    Console.WriteLine();
    Console.WriteLine($"{"Mode",-20} {"Mean",12} {"P50",12} {"P95",12} {"CPU/op",12} {"Allocated/op",16} {"Connections/op",16}");
    PrintDbAccount(sequentialResult);
    PrintDbAccount(batchedResult);
    Console.WriteLine();
    Console.WriteLine($"Latency change:    {(batchedResult.MeanMicroseconds / sequentialResult.MeanMicroseconds - 1) * 100:F1}%");
    Console.WriteLine($"CPU change:        {(batchedResult.CpuMicroseconds / sequentialResult.CpuMicroseconds - 1) * 100:F1}%");
    Console.WriteLine($"Allocation change: {(batchedResult.AllocatedBytes / sequentialResult.AllocatedBytes - 1) * 100:F1}%");
}

static void RegisterDbAccountGraph(CountingMySqlObjectDatabase database)
{
    database.RegisterReadOnly(
        typeof(DbAccount),
        typeof(DbCoreCharacter),
        typeof(DbBans),
        typeof(DbAccountXCustomParam),
        typeof(DbCoreCharacterXCustomParam));
}

static DbAccountBenchmarkResult MeasureDbAccounts(string name, CountingMySqlObjectDatabase database, string[] accountNames, int iterations)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    database.ResetConnectionCount();

    long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
    TimeSpan cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
    long[] samples = new long[iterations];
    long totalStart = Stopwatch.GetTimestamp();
    int checksum = 0;

    for (int i = 0; i < iterations; i++)
    {
        long operationStart = Stopwatch.GetTimestamp();
        DbAccount account = database.SelectObject<DbAccount>(DB.Column(nameof(DbAccount.Name)).IsEqualTo(accountNames[i % accountNames.Length]));
        checksum += ConsumeAccount(account);
        samples[i] = Stopwatch.GetTimestamp() - operationStart;
    }

    long totalTicks = Stopwatch.GetTimestamp() - totalStart;
    TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuStart;
    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
    Array.Sort(samples);

    return new DbAccountBenchmarkResult(
        name,
        TicksToMicroseconds(totalTicks) / iterations,
        TicksToMicroseconds(samples[iterations / 2]),
        TicksToMicroseconds(samples[(int)(iterations * 0.95)]),
        cpu.TotalMicroseconds / iterations,
        (double)allocated / iterations,
        (double)database.ConnectionCount / iterations,
        checksum);
}

static int ConsumeAccount(DbAccount account)
{
    return (account.Characters?.Length ?? 0)
        + (account.Characters?.Sum(character => character.CustomParams?.Length ?? 0) ?? 0)
        + (account.BannedAccount?.Length ?? 0)
        + (account.CustomParams?.Length ?? 0);
}

static void PrintDbAccount(DbAccountBenchmarkResult result)
{
    Console.WriteLine($"{result.Name,-20} {result.MeanMicroseconds,9:F2} us {result.P50Microseconds,9:F2} us {result.P95Microseconds,9:F2} us {result.CpuMicroseconds,9:F2} us {result.AllocatedBytes,13:N0} B {result.Connections,16:F2}");
}

internal sealed record BenchmarkResult(
    string Name,
    double MeanMicroseconds,
    double P50Microseconds,
    double P95Microseconds,
    double CpuMicroseconds,
    double AllocatedBytes,
    double Connections,
    int Checksum);

internal sealed record DbAccountBenchmarkResult(
    string Name,
    double MeanMicroseconds,
    double P50Microseconds,
    double P95Microseconds,
    double CpuMicroseconds,
    double AllocatedBytes,
    double Connections,
    int Checksum);

internal class CountingSqliteObjectDatabase : SqliteObjectDatabase
{
    public int ConnectionCount { get; private set; }

    public CountingSqliteObjectDatabase(string connectionString) : base(connectionString) { }

    public void ResetConnectionCount() => ConnectionCount = 0;

    protected override void OpenConnection(DbConnection connection)
    {
        ConnectionCount++;
        base.OpenConnection(connection);
    }
}

internal sealed class SequentialSqliteObjectDatabase : CountingSqliteObjectDatabase
{
    public SequentialSqliteObjectDatabase(string connectionString) : base(connectionString) { }

    protected override List<List<DataObject>> MultipleSelectObjectsImpl(IReadOnlyList<SelectQuery> queries)
    {
        return queries.Select(query => MultipleSelectObjectsImpl(query.TableHandler, [query.WhereClause]).Single()).ToList();
    }
}

internal class CountingMySqlObjectDatabase : MySqlObjectDatabase
{
    public int ConnectionCount { get; private set; }

    public CountingMySqlObjectDatabase(string connectionString) : base(connectionString) { }

    public void RegisterReadOnly(params Type[] dataObjectTypes)
    {
        foreach (Type dataObjectType in dataObjectTypes)
        {
            string tableName = AttributeUtil.GetTableOrViewName(dataObjectType);

            if (!TableDatasets.ContainsKey(tableName))
                TableDatasets.Add(tableName, new DataTableHandler(dataObjectType));
        }
    }

    public void ResetConnectionCount() => ConnectionCount = 0;

    protected override void OpenConnection(DbConnection connection)
    {
        ConnectionCount++;
        base.OpenConnection(connection);
    }
}

internal sealed class SequentialMySqlObjectDatabase : CountingMySqlObjectDatabase
{
    public SequentialMySqlObjectDatabase(string connectionString) : base(connectionString) { }

    protected override List<List<DataObject>> MultipleSelectObjectsImpl(IReadOnlyList<SelectQuery> queries)
    {
        return queries.Select(query => MultipleSelectObjectsImpl(query.TableHandler, [query.WhereClause]).Single()).ToList();
    }
}

[DataTable(TableName = "RelationBenchmarkParent")]
internal sealed class BenchmarkParent : DataObject
{
    [PrimaryKey]
    public string Id { get; set; }

    [Relation(LocalField = nameof(Id), RemoteField = nameof(BenchmarkFirstChild.ParentId), AutoLoad = true)]
    public BenchmarkFirstChild[] FirstChildren { get; set; }

    [Relation(LocalField = nameof(Id), RemoteField = nameof(BenchmarkSecondChild.ParentId), AutoLoad = true)]
    public BenchmarkSecondChild[] SecondChildren { get; set; }

    [Relation(LocalField = nameof(Id), RemoteField = nameof(BenchmarkThirdChild.ParentId), AutoLoad = true)]
    public BenchmarkThirdChild[] ThirdChildren { get; set; }
}

[DataTable(TableName = "RelationBenchmarkFirstChild")]
internal sealed class BenchmarkFirstChild : DataObject
{
    [PrimaryKey]
    public string Id { get; set; }

    [DataElement(AllowDbNull = false, Index = true)]
    public string ParentId { get; set; }

    [DataElement]
    public int Value { get; set; }

    [Relation(LocalField = nameof(Id), RemoteField = nameof(BenchmarkGrandchild.ChildId), AutoLoad = true)]
    public BenchmarkGrandchild[] Grandchildren { get; set; }
}

[DataTable(TableName = "RelationBenchmarkGrandchild")]
internal sealed class BenchmarkGrandchild : DataObject
{
    [PrimaryKey]
    public string Id { get; set; }

    [DataElement(AllowDbNull = false, Index = true)]
    public string ChildId { get; set; }

    [DataElement]
    public int Value { get; set; }
}

[DataTable(TableName = "RelationBenchmarkSecondChild")]
internal sealed class BenchmarkSecondChild : DataObject
{
    [PrimaryKey]
    public string Id { get; set; }

    [DataElement(AllowDbNull = false, Index = true)]
    public string ParentId { get; set; }

    [DataElement]
    public int Value { get; set; }
}

[DataTable(TableName = "RelationBenchmarkThirdChild")]
internal sealed class BenchmarkThirdChild : DataObject
{
    [PrimaryKey]
    public string Id { get; set; }

    [DataElement(AllowDbNull = false, Index = true)]
    public string ParentId { get; set; }

    [DataElement]
    public int Value { get; set; }
}
