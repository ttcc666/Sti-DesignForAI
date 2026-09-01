namespace StiLabel.Data;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StiLabel");

    public static string DbFile => Path.Combine(Root, "sti-label.db");
    public static string Templates => Path.Combine(Root, "templates");
    public static string Versions => Path.Combine(Root, "versions");
    public static string SampleData => Path.Combine(Root, "sample-data.json");
    public static string ChatImages => Path.Combine(Root, "chat-images");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Templates);
        Directory.CreateDirectory(Versions);
        Directory.CreateDirectory(ChatImages);
    }
}
