using StiLabel.Core.Catalog;
using StiLabel.Data;
using StiLabel.Data.Entities;
using StiLabel.Data.Stores;
using Xunit;

namespace StiLabel.Tests;

public class StoreAsyncTests
{
    private readonly StiLabelDb _db;
    private readonly FieldCatalog _fields;
    private readonly AppStore _store;

    public StoreAsyncTests()
    {
        _db = new StiLabelDb();
        _db.Initialize();
        _fields = new FieldCatalog(_db);
        _store = new AppStore(_db);
    }

    [Fact]
    public async Task FieldCatalog_AsyncLifecycle_CrudOperationsWorkCorrectly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;

        // 1. 清空字典
        await _fields.ClearAsync(token);
        var initialList = await _fields.ListAsync(token);
        Assert.Empty(initialList);

        // 2. 插入字段 (Upsert)
        var f1 = await _fields.UpsertAsync("物料编码", "MaterialCode", "text", token);
        Assert.NotNull(f1);
        Assert.Equal("MaterialCode", f1.Key);
        Assert.Equal("物料编码", f1.DisplayName);
        Assert.Equal("text", f1.DataType);
        Assert.True(f1.Id > 0);

        var f2 = await _fields.UpsertAsync("数量", "Qty", "number", token);
        Assert.NotNull(f2);
        Assert.Equal("Qty", f2.Key);
        Assert.Equal("数量", f2.DisplayName);
        Assert.Equal("number", f2.DataType);

        var f3 = await _fields.UpsertAsync("生产日期", null, "date", token);
        Assert.NotNull(f3);
        Assert.False(string.IsNullOrWhiteSpace(f3.Key));
        Assert.Equal("生产日期", f3.DisplayName);
        Assert.Equal("date", f3.DataType);

        // 3. 列表查询 (ListAsync)
        var list = await _fields.ListAsync(token);
        Assert.Equal(3, list.Count);
        Assert.Equal("MaterialCode", list[0].Key);
        Assert.Equal("Qty", list[1].Key);

        // 4. 按 Key / DisplayName 查找 (FindAsync)
        var foundByKey = await _fields.FindAsync("MaterialCode", token);
        Assert.NotNull(foundByKey);
        Assert.Equal("物料编码", foundByKey.DisplayName);

        var foundByName = await _fields.FindAsync("数量", token);
        Assert.NotNull(foundByName);
        Assert.Equal("Qty", foundByName.Key);

        var notFound = await _fields.FindAsync("NonExistent", token);
        Assert.Null(notFound);

        // 5. 更新已有字段 (UpsertAsync)
        var updated = await _fields.UpsertAsync("物料名称及规格", "MaterialCode", "text", token);
        Assert.Equal(f1.Id, updated.Id);
        Assert.Equal("物料名称及规格", updated.DisplayName);

        var verifyUpdated = await _fields.FindAsync("MaterialCode", token);
        Assert.NotNull(verifyUpdated);
        Assert.Equal("物料名称及规格", verifyUpdated.DisplayName);

        // 6. 删除字段 (DeleteAsync)
        await _fields.DeleteAsync(f2.Id, token);
        var afterDelete = await _fields.ListAsync(token);
        Assert.Equal(2, afterDelete.Count);
        Assert.DoesNotContain(afterDelete, f => f.Id == f2.Id);

        // 7. 再次清空
        await _fields.ClearAsync(token);
        var finalList = await _fields.ListAsync(token);
        Assert.Empty(finalList);
    }

    [Fact]
    public async Task AppStore_AsyncSettingsAndRecent_OperationsWorkCorrectly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;

        // 1. 设置存取 (SetAsync / GetAsync)
        var testKey = "TestSetting_" + Guid.NewGuid().ToString("N")[..6];
        await _store.SetAsync(testKey, "InitialValue", token);

        var val1 = await _store.GetAsync(testKey, token);
        Assert.Equal("InitialValue", val1);

        await _store.SetAsync(testKey, "UpdatedValue", token);
        var val2 = await _store.GetAsync(testKey, token);
        Assert.Equal("UpdatedValue", val2);

        var missing = await _store.GetAsync("NonExistentKey_" + Guid.NewGuid(), token);
        Assert.Null(missing);

        // 2. 最近文件记录 (TouchRecentAsync / ListRecentAsync)
        await _db.Client.Deleteable<RecentFileRow>().ExecuteCommandAsync(token);
        var prefix = "C:\\Templates\\test_" + Guid.NewGuid().ToString("N")[..6] + "_";
        for (var i = 1; i <= 12; i++)
        {
            await _store.TouchRecentAsync($"{prefix}{i}.mrt", token);
            await Task.Delay(10, token); // 确保时间戳递增
        }

        var recentList = await _store.ListRecentAsync(token);
        Assert.NotEmpty(recentList);
        Assert.True(recentList.Count <= 10, "最近文件上限不超过 10 条");
        Assert.Equal($"{prefix}12.mrt", recentList[0].Path); // 最新打开的在最前面
    }

    [Fact]
    public async Task AppStore_AsyncSampleRows_SaveAndLoadWorkCorrectly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;

        var sampleRows = new List<SampleRow>
        {
            new()
            {
                Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MaterialCode"] = "A-001",
                    ["MaterialName"] = "电阻",
                    ["Qty"] = "500"
                }
            },
            new()
            {
                Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MaterialCode"] = "B-002",
                    ["MaterialName"] = "电容",
                    ["Qty"] = "1000"
                }
            }
        };

        // 异步保存与读取
        await _store.SaveSampleRowsAsync(sampleRows, token);
        var loaded = await _store.LoadSampleRowsAsync(token);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("A-001", loaded[0].Values["MaterialCode"]);
        Assert.Equal("电阻", loaded[0].Values["MaterialName"]);
        Assert.Equal("500", loaded[0].Values["Qty"]);

        Assert.Equal("B-002", loaded[1].Values["MaterialCode"]);
        Assert.Equal("电容", loaded[1].Values["MaterialName"]);
        Assert.Equal("1000", loaded[1].Values["Qty"]);
    }
}
