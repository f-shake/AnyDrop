namespace AnyDrop.Tests;

/// <summary>
/// 测试用的临时目录放在仓库内的 .local-dev/ 下（已 gitignore）。
/// 不用 %TEMP%：本机沙箱不允许在系统临时目录下创建子目录。
/// </summary>
internal static class TestPaths
{
    private static readonly string Base = Path.Combine(RepositoryRoot(), ".local-dev", "test-runs");

    public static string NewRoot(string kind)
    {
        var path = Path.Combine(Base, $"{kind}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string RepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "src", "AnyDrop.Server"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return AppContext.BaseDirectory;
    }
}
