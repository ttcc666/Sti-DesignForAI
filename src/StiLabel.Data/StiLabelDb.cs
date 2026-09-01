using SqlSugar;
using StiLabel.Data.Entities;

namespace StiLabel.Data;

public sealed class StiLabelDb
{
    public ISqlSugarClient Client { get; }

    public StiLabelDb()
    {
        AppPaths.EnsureCreated();
        Client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={AppPaths.DbFile}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            MoreSettings = new ConnMoreSettings
            {
                SqliteCodeFirstEnableDefaultValue = true
            }
        });
    }

    public void Initialize()
    {
        Client.DbMaintenance.CreateDatabase();
        Client.CodeFirst.InitTables(
            typeof(AppSettingRow),
            typeof(FieldDefinitionRow),
            typeof(RecentFileRow),
            typeof(TemplateVersionRow));
        SeedSampleData();
    }

    private static void SeedSampleData()
    {
        if (File.Exists(AppPaths.SampleData))
        {
            return;
        }

        const string json =
            """
            [
              {
                "MaterialCode": "M-10086",
                "MaterialName": "轴承座",
                "Spec": "Φ80",
                "BatchNo": "B20260831",
                "Qty": "12",
                "ProductionDate": "2026-08-31",
                "Warehouse": "东仓"
              }
            ]
            """;
        File.WriteAllText(AppPaths.SampleData, json);
    }
}
