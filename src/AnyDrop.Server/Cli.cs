using Microsoft.Extensions.Configuration;

namespace AnyDrop.Server;

/// <summary>命令行子命令：目前只有 --set-password。</summary>
internal static class Cli
{
    public static async Task<int> SetPasswordAsync(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("anydrop.json", optional: true)
            .AddEnvironmentVariables("ANYDROP__")
            .Build();

        AppConfig config;
        try
        {
            config = ConfigLoader.Load(configuration);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"配置无效：{ex.Message}");
            return 2;
        }

        var useStdin = string.Equals(args[0], "--set-password-stdin", StringComparison.Ordinal);
        string? password;
        if (useStdin)
        {
            // 只去掉结尾换行，不动首尾空白：交互式分支也不 Trim，两个分支行为要一致
            password = (Console.In.ReadToEnd() ?? string.Empty).TrimEnd('\r', '\n');
        }
        else if (args.Length > 1)
        {
            password = args[1];
            Console.Error.WriteLine(
                "提示：密码作为命令行参数会出现在同机其他进程的参数列表与 shell 历史里；" +
                "建议改用不带参数的交互输入，或用 --set-password-stdin 从标准输入读。");
        }
        else
        {
            Console.Write($"设置管理员密码（用户名 {config.Admin.Username}，至少 {AdminAuth.MinPasswordLength} 位）：");
            password = ReadPassword();
        }
        if (string.IsNullOrEmpty(password) || password.Length < AdminAuth.MinPasswordLength)
        {
            Console.Error.WriteLine($"密码不能为空，且至少 {AdminAuth.MinPasswordLength} 位");
            return 2;
        }

        var index = new SqliteIndex(config);
        try
        {
            await index.InitializeAsync();
        }
        catch (InvalidOperationException ex)
        {
            // 与 Program.cs 保持同一句提示：结构版本不匹配时要给可操作的出路，而不是一坨堆栈
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        var (hash, salt, iterations) = SecretHasher.HashPassword(password);
        await index.SetAdminPasswordAsync(hash, salt, iterations);
        Console.WriteLine($"管理员密码已写入 {config.DbPath}");
        return 0;
    }

    private static string? ReadPassword()
    {
        if (Console.IsInputRedirected) return Console.ReadLine();
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0) buffer.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
    }
}
