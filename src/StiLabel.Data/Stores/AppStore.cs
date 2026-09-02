using System.Text.Json;
using StiLabel.Core.Catalog;
using StiLabel.Core.Services;
using StiLabel.Data.Entities;

namespace StiLabel.Data.Stores;

public sealed class AppStore : IAppStore
{
    private readonly StiLabelDb _db;

    public AppStore(StiLabelDb db) => _db = db;

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        return await _db.Client.Queryable<AppSettingRow>()
            .Where(x => x.Key == key)
            .Select(x => x.Value)
            .FirstAsync(cancellationToken);
    }

    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        var exists = await _db.Client.Queryable<AppSettingRow>().AnyAsync(x => x.Key == key, cancellationToken);
        if (exists)
        {
            await _db.Client.Updateable(new AppSettingRow { Key = key, Value = value })
                .ExecuteCommandAsync(cancellationToken);
        }
        else
        {
            await _db.Client.Insertable(new AppSettingRow { Key = key, Value = value })
                .ExecuteCommandAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<RecentFileItem>> ListRecentAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.Client.Queryable<RecentFileRow>()
            .OrderBy(x => x.Id, SqlSugar.OrderByType.Desc)
            .Take(10)
            .ToListAsync(cancellationToken);
        return rows.Select(x => new RecentFileItem { Path = x.Path, OpenedAt = x.OpenedAt }).ToList();
    }

    public async Task TouchRecentAsync(string path, CancellationToken cancellationToken = default)
    {
        await _db.Client.Deleteable<RecentFileRow>().Where(x => x.Path == path).ExecuteCommandAsync(cancellationToken);
        await _db.Client.Insertable(new RecentFileRow { Path = path, OpenedAt = DateTime.Now }).ExecuteCommandAsync(cancellationToken);
        var allIds = await _db.Client.Queryable<RecentFileRow>()
            .OrderBy(x => x.Id, SqlSugar.OrderByType.Desc)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (allIds.Count > 10)
        {
            var extra = allIds.Skip(10).ToList();
            await _db.Client.Deleteable<RecentFileRow>().In(extra).ExecuteCommandAsync(cancellationToken);
        }
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
