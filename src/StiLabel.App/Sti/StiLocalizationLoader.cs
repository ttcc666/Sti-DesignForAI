using System.IO;
using Stimulsoft.Report;

namespace StiLabel.App.Sti;

/// <summary>
/// 官网：StiOptions.Localization.Load(xml)。
/// https://www.stimulsoft.com/en/samples/reports-net/net80/localizing-the-user-interface
/// </summary>
public static class StiLocalizationLoader
{
    private static bool _loaded;

    public static void TryLoadChinese()
    {
        if (_loaded)
        {
            return;
        }

        var file = Path.Combine(AppContext.BaseDirectory, "Localization", "zh-CHS.xml");
        if (!File.Exists(file))
        {
            return;
        }

        StiOptions.Configuration.DirectoryLocalization = Path.GetDirectoryName(file) ?? AppContext.BaseDirectory;
        StiOptions.Localization.Load(file);
        _loaded = true;
    }
}
