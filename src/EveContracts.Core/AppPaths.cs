namespace EveContracts.Core;

public static class AppPaths
{
    public static string DataDir
    {
        get
        {
            // EVECONTRACTS_DATA_DIR lets a dev build run against a copy of the data while the
            // installed app keeps using the real one.
            var dir = Environment.GetEnvironmentVariable("EVECONTRACTS_DATA_DIR") is { Length: > 0 } overrideDir
                ? overrideDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveContracts");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DbPath => Path.Combine(DataDir, "evecontracts.db");
    public static string SdeDir
    {
        get
        {
            var dir = Path.Combine(DataDir, "sde");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
