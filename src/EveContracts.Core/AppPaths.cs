namespace EveContracts.Core;

public static class AppPaths
{
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveContracts");
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
