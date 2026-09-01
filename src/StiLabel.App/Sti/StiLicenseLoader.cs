using System.IO;
using StiLabel.Data;
using Stimulsoft.Base;

namespace StiLabel.App.Sti;

public static class StiLicenseLoader
{
    public static bool IsLoaded { get; private set; }

    public static void TryLoad()
    {
        var candidates = new[]
        {
            Path.Combine(AppPaths.Root, "license.key"),
            Path.Combine(AppContext.BaseDirectory, "license.key")
        };

        foreach (var file in candidates)
        {
            if (!File.Exists(file))
            {
                continue;
            }

            StiLicense.LoadFromFile(file);
            IsLoaded = true;
            return;
        }
    }
}
