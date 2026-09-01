using System.Text.Json;
using StiLabel.Core.Catalog;
using StiLabel.Core.Services;
using StiLabel.Data.Entities;

namespace StiLabel.Data.Stores;

public sealed class AppStore : IAppStore
{
    private readonly StiLabelDb _db;

    public AppStore(StiLabelDb db) => _db = db;

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var value = _db.Client.Queryable<AppSettingRow>()
            .Where(x => x.Key == key)
            .Select(x => x.Value)
            .First();
        return Task.FromResult(value);
    }

    public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        var exists = _db.Client.Queryable<AppSettingRow>().Any(x => x.Key == key);
        if (exists)
        {
            _db.Client.Updateable(new AppSettingRow { Key = key, Value = value })
                .ExecuteCommand();
        }
        else
        {
            _db.Client.Insertable(new AppSettingRow { Key = key, Value = value })
                .ExecuteCommand();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RecentFileItem>> ListRecentAsync(CancellationToken cancellationToken = default)
    {
        var rows = _db.Client.Queryable<RecentFileRow>()
            .OrderBy(x => x.OpenedAt, SqlSugar.OrderByType.Desc)
            .Take(10)
            .ToList()
            .Select(x => new RecentFileItem { Path = x.Path, OpenedAt = x.OpenedAt })
            .ToList();
        return Task.FromResult<IReadOnlyList<RecentFileItem>>(rows);
    }

    public Task TouchRecentAsync(string path, CancellationToken cancellationToken = default)
    {
        _db.Client.Deleteable<RecentFileRow>().Where(x => x.Path == path).ExecuteCommand();
        _db.Client.Insertable(new RecentFileRow { Path = path, OpenedAt = DateTime.Now }).ExecuteCommand();
        var extra = _db.Client.Queryable<RecentFileRow>()
            .OrderBy(x => x.OpenedAt, SqlSugar.OrderByType.Desc)
            .Skip(10)
            .Select(x => x.Id)
            .ToList();
        if (extra.Count > 0)
        {
            _db.Client.Deleteable<RecentFileRow>().In(extra).ExecuteCommand();
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<SampleRow>> LoadSampleRowsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(AppPaths.SampleData))
        {
            return [];
        }

        await using var stream = File.OpenRead(AppPaths.SampleData);
        var rows = await JsonSerializer.DeserializeAsync<List<Dictionary<string, string>>>(stream, cancellationToken: cancellationToken)
                   ?? [];
        return rows.Select(v => new SampleRow { Values = new Dictionary<string, string>(v, StringComparer.OrdinalIgnoreCase) }).ToList();
    }

    public async Task SaveSampleRowsAsync(IReadOnlyList<SampleRow> rows, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(AppPaths.SampleData);
        await JsonSerializer.SerializeAsync(
            stream,
            rows.Select(r => r.Values).ToList(),
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);
    }
}
