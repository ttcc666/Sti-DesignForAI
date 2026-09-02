using StiLabel.Core.Catalog;
using StiLabel.Core.Services;
using StiLabel.Data.Entities;

namespace StiLabel.Data.Stores;

public sealed class FieldCatalog : IFieldCatalog
{
    private readonly StiLabelDb _db;

    public FieldCatalog(StiLabelDb db) => _db = db;

    public async Task<IReadOnlyList<FieldItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.Client.Queryable<FieldDefinitionRow>()
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);
        return rows.Select(ToItem).ToList();
    }

    public async Task<FieldItem?> FindAsync(string keyOrName, CancellationToken cancellationToken = default)
    {
        var row = await FindRowAsync(keyOrName, cancellationToken);
        return row is null ? null : ToItem(row);
    }

    public async Task<FieldItem> UpsertAsync(
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
        var row = await FindRowAsync(name, cancellationToken);
        if (row is null && !string.IsNullOrWhiteSpace(key))
        {
            row = await FindRowAsync(key.Trim(), cancellationToken);
        }

        if (row is not null)
        {
            row.DisplayName = name;
            row.DataType = type;
            await _db.Client.Updateable(row).ExecuteCommandAsync(cancellationToken);
            return ToItem(row);
        }

        var unique = await UniqueKeyAsync(MakeKey(string.IsNullOrWhiteSpace(key) ? name : key.Trim()), cancellationToken);
        var hasAny = await _db.Client.Queryable<FieldDefinitionRow>().AnyAsync(cancellationToken);
        var maxOrder = hasAny
            ? await _db.Client.Queryable<FieldDefinitionRow>().MaxAsync(x => x.SortOrder, cancellationToken)
            : 0;
        row = new FieldDefinitionRow
        {
            Key = unique,
            DisplayName = name,
            DataType = type,
            Required = false,
            SortOrder = maxOrder + 1
        };
        row.Id = await _db.Client.Insertable(row).ExecuteReturnIdentityAsync(cancellationToken);
        return ToItem(row);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await _db.Client.Deleteable<FieldDefinitionRow>().Where(x => x.Id == id).ExecuteCommandAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _db.Client.Deleteable<FieldDefinitionRow>().ExecuteCommandAsync(cancellationToken);
    }

    private async Task<FieldDefinitionRow?> FindRowAsync(string keyOrName, CancellationToken cancellationToken) =>
        await _db.Client.Queryable<FieldDefinitionRow>()
            .Where(x => x.Key == keyOrName || x.DisplayName == keyOrName)
            .FirstAsync(cancellationToken);

    private async Task<string> UniqueKeyAsync(string key, CancellationToken cancellationToken)
    {
        var current = key;
        var n = 2;
        while (await _db.Client.Queryable<FieldDefinitionRow>().AnyAsync(x => x.Key == current, cancellationToken))
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
