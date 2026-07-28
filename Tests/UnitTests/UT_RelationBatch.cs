using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using DOL.Database;
using DOL.Database.Attributes;
using DOL.Database.Handlers;
using NUnit.Framework;

namespace DOL.Tests.UnitTests;

[TestFixture]
public class UT_RelationBatch
{
    private readonly List<string> _databaseFiles = [];

    [TearDown]
    public void TearDown()
    {
        foreach (string databaseFile in _databaseFiles)
        {
            if (File.Exists(databaseFile))
                File.Delete(databaseFile);
        }

        _databaseFiles.Clear();
    }

    [Test]
    public void MultipleRelations_AreLoadedFromOneMultiResultCommand()
    {
        CountingSqliteObjectDatabase database = CreateDatabase<CountingSqliteObjectDatabase>();
        Seed(database);
        database.ResetConnectionCount();

        RelationBatchParent parent = database.SelectObject<RelationBatchParent>(DB.Column(nameof(RelationBatchParent.Id)).IsEqualTo("parent-1"));

        Assert.That(parent.FirstChildren.Select(child => child.Value), Is.EquivalentTo(new[] { "first-a", "first-b" }));
        Assert.That(parent.SecondChildren.Select(child => child.Value), Is.EqualTo(new[] { "second-a" }));
        Assert.That(parent.CachedChild.Value, Is.EqualTo("cached-a"));
        Assert.That(parent.CachedChildren.Select(child => child.Value), Is.EquivalentTo(new[] { "cached-a", "cached-b" }));
        Assert.That(database.ConnectionCount, Is.EqualTo(2), "Expected one root select and one multi-result relation select.");
    }

    [Test]
    public void MultiResultAndSequentialLoading_ReturnEquivalentGraphs()
    {
        CountingSqliteObjectDatabase batched = CreateDatabase<CountingSqliteObjectDatabase>();
        SequentialSqliteObjectDatabase sequential = CreateDatabase<SequentialSqliteObjectDatabase>();
        Seed(batched);
        Seed(sequential);
        batched.ResetConnectionCount();
        sequential.ResetConnectionCount();

        RelationBatchParent batchedParent = batched.SelectObject<RelationBatchParent>(DB.Column(nameof(RelationBatchParent.Id)).IsEqualTo("parent-1"));
        RelationBatchParent sequentialParent = sequential.SelectObject<RelationBatchParent>(DB.Column(nameof(RelationBatchParent.Id)).IsEqualTo("parent-1"));

        Assert.That(batchedParent.FirstChildren.Select(child => child.Value), Is.EquivalentTo(sequentialParent.FirstChildren.Select(child => child.Value)));
        Assert.That(batchedParent.SecondChildren.Select(child => child.Value), Is.EquivalentTo(sequentialParent.SecondChildren.Select(child => child.Value)));
        Assert.That(batchedParent.CachedChildren.Select(child => child.Value), Is.EquivalentTo(sequentialParent.CachedChildren.Select(child => child.Value)));
        Assert.Multiple(() =>
        {
            Assert.That(batched.ConnectionCount, Is.EqualTo(2));
            Assert.That(sequential.ConnectionCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void MultipleParents_MapEachRelationToTheCorrectParent()
    {
        CountingSqliteObjectDatabase database = CreateDatabase<CountingSqliteObjectDatabase>();
        Seed(database);
        Assert.That(database.AddObject(new RelationBatchParent { Id = "parent-2", CachedChildId = "cached-c" }), Is.True);
        Assert.That(database.AddObject(new DataObject[]
        {
            new RelationBatchFirstChild { ParentId = "parent-2", Value = "first-c" },
            new RelationBatchSecondChild { ParentId = "parent-2", Value = "second-b" },
            new RelationBatchCachedChild { Id = "cached-c", ParentId = "parent-2", Value = "cached-c" }
        }), Is.True);

        List<RelationBatchParent> parents = database.SelectAllObjects<RelationBatchParent>();
        RelationBatchParent first = parents.Single(parent => parent.Id == "parent-1");
        RelationBatchParent second = parents.Single(parent => parent.Id == "parent-2");

        Assert.Multiple(() =>
        {
            Assert.That(first.FirstChildren.Select(child => child.Value), Is.EquivalentTo(new[] { "first-a", "first-b" }));
            Assert.That(second.FirstChildren.Select(child => child.Value), Is.EqualTo(new[] { "first-c" }));
            Assert.That(first.SecondChildren.Select(child => child.Value), Is.EqualTo(new[] { "second-a" }));
            Assert.That(second.SecondChildren.Select(child => child.Value), Is.EqualTo(new[] { "second-b" }));
            Assert.That(first.CachedChildren.Select(child => child.Value), Is.EquivalentTo(new[] { "cached-a", "cached-b" }));
            Assert.That(second.CachedChildren.Select(child => child.Value), Is.EqualTo(new[] { "cached-c" }));
        });
    }

    private T CreateDatabase<T>() where T : CountingSqliteObjectDatabase
    {
        string databaseFile = Path.Combine(Path.GetTempPath(), $"dol-relation-batch-{Guid.NewGuid():N}.sqlite");
        _databaseFiles.Add(databaseFile);
        T database = (T)Activator.CreateInstance(typeof(T), $"Data Source={databaseFile};Version=3;Pooling=False");
        database.RegisterDataObject(typeof(RelationBatchFirstChild));
        database.RegisterDataObject(typeof(RelationBatchSecondChild));
        database.RegisterDataObject(typeof(RelationBatchCachedChild));
        database.RegisterDataObject(typeof(RelationBatchParent));
        return database;
    }

    private static void Seed(IObjectDatabase database)
    {
        Assert.That(database.AddObject(new RelationBatchParent { Id = "parent-1", CachedChildId = "cached-a" }), Is.True);
        Assert.That(database.AddObject(new DataObject[]
        {
            new RelationBatchFirstChild { ParentId = "parent-1", Value = "first-a" },
            new RelationBatchFirstChild { ParentId = "parent-1", Value = "first-b" },
            new RelationBatchSecondChild { ParentId = "parent-1", Value = "second-a" },
            new RelationBatchCachedChild { Id = "cached-a", ParentId = "parent-1", Value = "cached-a" },
            new RelationBatchCachedChild { Id = "cached-b", ParentId = "parent-1", Value = "cached-b" }
        }), Is.True);
    }

    public class CountingSqliteObjectDatabase : SqliteObjectDatabase
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

    public sealed class SequentialSqliteObjectDatabase : CountingSqliteObjectDatabase
    {
        public SequentialSqliteObjectDatabase(string connectionString) : base(connectionString) { }

        protected override List<List<DataObject>> MultipleSelectObjectsImpl(IReadOnlyList<SelectQuery> queries)
        {
            return queries
                .Select(query => MultipleSelectObjectsImpl(query.TableHandler, [query.WhereClause]).Single())
                .ToList();
        }
    }
}

[DataTable(TableName = "RelationBatchParent")]
public class RelationBatchParent : DataObject
{
    [PrimaryKey]
    public string Id { get; set; }

    [DataElement]
    public string CachedChildId { get; set; }

    [Relation(LocalField = nameof(Id), RemoteField = nameof(RelationBatchFirstChild.ParentId), AutoLoad = true)]
    public RelationBatchFirstChild[] FirstChildren { get; set; }

    [Relation(LocalField = nameof(Id), RemoteField = nameof(RelationBatchSecondChild.ParentId), AutoLoad = true)]
    public RelationBatchSecondChild[] SecondChildren { get; set; }

    [Relation(LocalField = nameof(CachedChildId), RemoteField = nameof(RelationBatchCachedChild.Id), AutoLoad = true)]
    public RelationBatchCachedChild CachedChild { get; set; }

    [Relation(LocalField = nameof(Id), RemoteField = nameof(RelationBatchCachedChild.ParentId), AutoLoad = true)]
    public RelationBatchCachedChild[] CachedChildren { get; set; }
}

[DataTable(TableName = "RelationBatchFirstChild")]
public class RelationBatchFirstChild : DataObject
{
    [PrimaryKey(AutoIncrement = true)]
    public int Id { get; set; }

    [DataElement(AllowDbNull = false, Index = true)]
    public string ParentId { get; set; }

    [DataElement(AllowDbNull = false)]
    public string Value { get; set; }
}

[DataTable(TableName = "RelationBatchSecondChild")]
public class RelationBatchSecondChild : DataObject
{
    [PrimaryKey(AutoIncrement = true)]
    public int Id { get; set; }

    [DataElement(AllowDbNull = false, Index = true)]
    public string ParentId { get; set; }

    [DataElement(AllowDbNull = false)]
    public string Value { get; set; }
}

[DataTable(TableName = "RelationBatchCachedChild", PreCache = true)]
public class RelationBatchCachedChild : DataObject
{
    [PrimaryKey]
    public string Id { get; set; }

    [DataElement(AllowDbNull = false, Index = true)]
    public string ParentId { get; set; }

    [DataElement(AllowDbNull = false)]
    public string Value { get; set; }
}
