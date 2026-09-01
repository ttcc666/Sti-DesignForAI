using StiLabel.Core.Catalog;
using StiLabel.Core.Services;
using StiLabel.Data.Entities;

namespace StiLabel.Data.Stores;

public sealed class FieldCatalog : IFieldCatalog
{
    private readonly StiLabelDb _db;

    public FieldCatalog(StiLabelDb db) => _db = db;

    public Task<IReadOnlyList<FieldItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        var items = _db.Client.Queryable<FieldDefinitionRow>()
            .OrderBy(x => x.SortOrder)
            .ToList()
            .Select(ToItem)
            .ToList();
        return Task.FromResult<IReadOnlyList<FieldItem>>(items);
    }

    public Task<FieldItem?> FindAsync(string keyOrName, CancellationToken cancellationToken = default)
    {
        var row = FindRow(keyOrName);
        return Task.FromResult(row is null ? null : ToItem(row));
    }

    public Task<FieldItem> UpsertAsync(
        string displayName,
        string? key = null,
        string dataType = "text",
        CancellationToken cancellationToken = default)
    {
        var name = displayName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("字段名不能为空。", nameof(displayName));
        }

        var type = string.IsNullOrWhiteSpace(dataType) ? "text" : dataType.Trim().ToLowerInvariant();
        var row = FindRow(name) ?? (!string.IsNullOrWhiteSpace(key) ? FindRow(key.Trim()) : null);
        if (row is not null)
        {
            row.DisplayName = name;
            row.DataType = type;
            _db.Client.Updateable(row).ExecuteCommand();
            return Task.FromResult(ToItem(row));
        }

        var unique = UniqueKey(MakeKey(string.IsNullOrWhiteSpace(key) ? name : key.Trim()));
        var maxOrder = _db.Client.Queryable<FieldDefinitionRow>().Any()
            ? _db.Client.Queryable<FieldDefinitionRow>().Max(x => x.SortOrder)
            : 0;
        row = new FieldDefinitionRow
        {
            Key = unique,
            DisplayName = name,
            DataType = type,
            Required = false,
            SortOrder = maxOrder + 1
        };
        row.Id = _db.Client.Insertable(row).ExecuteReturnIdentity();
        return Task.FromResult(ToItem(row));
    }

    public Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        _db.Client.Deleteable<FieldDefinitionRow>().Where(x => x.Id == id).ExecuteCommand();
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _db.Client.Deleteable<FieldDefinitionRow>().ExecuteCommand();
        return Task.CompletedTask;
    }

    private FieldDefinitionRow? FindRow(string keyOrName) =>
        _db.Client.Queryable<FieldDefinitionRow>()
            .Where(x => x.Key == keyOrName || x.DisplayName == keyOrName)
            .Take(1)
            .ToList()
            .FirstOrDefault();

    private string UniqueKey(string key)
    {
        var current = key;
        var n = 2;
        while (_db.Client.Queryable<FieldDefinitionRow>().Any(x => x.Key == current))
        {
            current = key + n;
            n++;
        }

        return current;
    }

    private static string MakeKey(string name)
    {
        var ascii = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray()).Trim('_');
        if (ascii.Length >= 1 && char.IsAsciiLetter(ascii[0]))
        {
            return ascii;
        }

        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in name)
            {
                hash = (hash ^ c) * 16777619;
            }

            return "F" + hash.ToString("x8");
        }
    }

    private static FieldItem ToItem(FieldDefinitionRow row) => new()
    {
        Id = row.Id,
        Key = row.Key,
        DisplayName = row.DisplayName,
        DataType = row.DataType,
        Required = row.Required,
        SortOrder = row.SortOrder
    };
}
