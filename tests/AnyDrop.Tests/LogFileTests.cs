using AnyDrop.Server;
using AnyDrop.Server.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AnyDrop.Tests;

/// <summary>
/// 文件日志（Serilog）的配置绑定、级别护栏、降级、滚动与保留上限。
/// <para>
/// 最要紧的是 <see cref="配置压低级时_带凭证的路径不得出现在文件里"/>(T2)：它走**真实的 <c>Program</c> 接线**
/// 发一个带假 id 的下载请求，断言那条请求路径没有落进文件日志。把接线换成会绕过 MS 过滤器的写法
/// （例如 <c>Logging.AddSerilog(...)</c> 或换成 <c>SerilogLoggerFactory</c>），这条就会红。
/// </para>
/// <para>
/// 注意这条断言的**边界**：它守的是"逐请求那条 Information 日志被关掉了"。Warning/Error 级仍可能带 id
/// （见 <c>deploy/RUNBOOK-WINDOWS.md</c> 第 2 节的清单），那不是本用例能覆盖的。
/// </para>
/// </summary>
public sealed class LogFileTests : IDisposable
{
    private readonly List<string> _created = [];

    private AppConfig LoadConfig(params (string Key, string? Value)[] settings)
    {
        var pairs = settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
        return ConfigLoader.Load(configuration);
    }

    private string NewDir()
    {
        var dir = TestPaths.NewRoot("logfile");
        _created.Add(dir);
        return dir;
    }

    /// <summary>建一个测试宿主，并记下它的根目录（<see cref="TestApp"/> 自己删不掉，见 Dispose）。</summary>
    private TestApp NewApp(params (string Key, string? Value)[] settings)
    {
        var app = new TestApp(settings);
        _created.Add(app.Root);
        return app;
    }

    private static string[] LogFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.log").OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : [];

    /// <summary>
    /// 读正在写的日志文件。**必须显式给 FileShare.ReadWrite**：sink 持的是写句柄且只共享读，
    /// 而 Windows 要求双向兼容 —— 用 <c>File.ReadAllText</c>（FileShare.Read）会撞
    /// "being used by another process"。这也解释了运行时想 tail 日志要注意共享模式。
    /// </summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ReadAll(string directory) =>
        string.Join("\n", LogFiles(directory).Select(ReadShared));

    private static string[] Lines(string directory) =>
        ReadAll(directory).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// 临时目录清理。TestApp 的 Dispose 删自己的根目录时会因 SQLite 连接池仍持句柄而抛 IOException
    /// 并被它静默吞掉，所以这里先清池、再统一删 —— 否则 .local-dev/test-runs 只增不减。
    /// </summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var dir in _created)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* 仍被占用就留给下一次，不影响结论 */ }
            catch (UnauthorizedAccessException) { /* 同上 */ }
        }
    }

    // ---------------- T1 配置绑定与默认值 ----------------

    [Fact]
    public void 未配置时_默认启用_按天滚动_保留14份_单文件32MiB()
    {
        var config = LoadConfig();

        Assert.True(config.LogFile.Enabled);
        Assert.Equal("logs", config.LogFile.Directory);
        Assert.Equal("server-", config.LogFile.FileNamePrefix);
        Assert.Equal("Day", config.LogFile.RollingInterval);
        Assert.Equal(14, config.LogFile.RetainedFileCountLimit);
        Assert.Equal(32L * 1024 * 1024, config.LogFile.FileSizeLimitBytes);
        Assert.True(config.LogFile.RollOnFileSizeLimit);
        Assert.False(config.LogFile.Buffered);
        Assert.Equal(0, config.LogFile.FlushToDiskIntervalSeconds);
        Assert.Equal(new[] { "Infinite", "Year", "Month", "Day", "Hour", "Minute" }, LogFileOptions.RollingIntervalNames);
    }

    [Fact]
    public void 相对目录按程序所在目录解析_而不是按当前工作目录()
    {
        var original = Directory.GetCurrentDirectory();
        var elsewhere = NewDir();
        try
        {
            // 必须真的换掉工作目录，否则"按 BaseDirectory 解析"与"按 CWD 解析"在本运行器下
            // 返回同一个路径（测试进程的 CWD 就是 BaseDirectory），断言会退化成恒真。
            Directory.SetCurrentDirectory(elsewhere);
            var config = LoadConfig(("logging:file:directory", "logs"));

            Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "logs")), config.LogDirectory);
            Assert.NotEqual(Path.GetFullPath(Path.Combine(elsewhere, "logs")), config.LogDirectory);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    [Fact]
    public void 显式配置时_逐项生效()
    {
        var dir = NewDir();
        var config = LoadConfig(
            ("logging:file:enabled", "true"),
            ("logging:file:directory", dir),
            ("logging:file:fileNamePrefix", "app-"),
            ("logging:file:rollingInterval", "hour"),      // 大小写不敏感，且会被归一化
            ("logging:file:retainedFileCountLimit", "3"),
            ("logging:file:fileSizeLimitBytes", "4096"),
            ("logging:file:rollOnFileSizeLimit", "false"),
            ("logging:file:buffered", "true"),
            ("logging:file:flushToDiskIntervalSeconds", "2"));

        Assert.Equal("Hour", config.LogFile.RollingInterval);
        Assert.Equal(dir, config.LogDirectory);
        Assert.Equal("app-", config.LogFile.FileNamePrefix);
        Assert.Equal(3, config.LogFile.RetainedFileCountLimit);
        Assert.Equal(4096, config.LogFile.FileSizeLimitBytes);
        Assert.False(config.LogFile.RollOnFileSizeLimit);
        Assert.True(config.LogFile.Buffered);
        Assert.Equal(2, config.LogFile.FlushToDiskIntervalSeconds);
    }

    [Theory]
    [InlineData("logging:file:rollingInterval", "EveryDay")]
    [InlineData("logging:file:retainedFileCountLimit", "-1")]
    [InlineData("logging:file:fileSizeLimitBytes", "-1")]
    [InlineData("logging:file:flushToDiskIntervalSeconds", "-1")]
    [InlineData("logging:file:fileNamePrefix", "..\\escape")]
    [InlineData("logging:file:fileNamePrefix", "sub/name")]
    public void 非法配置_启动即报错_而不是静默降级(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadConfig((key, value)));
        Assert.Contains(key.Split(':')[^1], ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("logging:file:enabled", "0")]                 // 常见的"关掉"写法
    [InlineData("logging:file:enabled", "no")]
    [InlineData("logging:file:buffered", "1")]
    [InlineData("logging:file:rollOnFileSizeLimit", "off")]
    [InlineData("logging:file:fileSizeLimitBytes", "32MB")]   // 想写单位但解析不了
    [InlineData("logging:file:retainedFileCountLimit", "14 天")]
    public void 新增键解析不了就报错_不静默回落默认值(string key, string value)
    {
        // Bool/Int/Long 这些**既有**辅助方法对解析失败是静默回落的（改它们会影响既有配置的兼容性），
        // 所以 enabled: 0 本来会静默变成 true。本批新增的键一律走严格校验。
        var ex = Assert.Throws<InvalidOperationException>(() => LoadConfig((key, value)));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 空字符串按未配置处理_回落到默认值()
    {
        // ConfigLoader 的 Str() 对空白值一律取 fallback，所以空串不会触发"不能为空"的校验，
        // 而是安静地回到默认值 —— 这个行为要与其它 section 保持一致。
        var config = LoadConfig(
            ("logging:file:directory", "   "),
            ("logging:file:fileNamePrefix", ""));
        Assert.Equal("logs", config.LogFile.Directory);
        Assert.Equal("server-", config.LogFile.FileNamePrefix);
    }

    // ---------------- 滚动周期 → 实际文件名的映射（六个取值逐个钉死） ----------------

    [Theory]
    [InlineData("Infinite", "")]
    [InlineData("Year", "yyyy")]
    [InlineData("Month", "yyyyMM")]
    [InlineData("Day", "yyyyMMdd")]
    [InlineData("Hour", "yyyyMMddHH")]
    [InlineData("Minute", "yyyyMMddHHmm")]
    public void 滚动周期决定文件名的日期后缀(string interval, string format)
    {
        var dir = NewDir();
        var config = LoadConfig(("logging:file:directory", dir), ("logging:file:rollingInterval", interval));
        using var logger = LogFileSetup.TryCreate(config, out var problem);
        Assert.Null(problem);

        var before = DateTime.Now;
        logger!.Information("一行");
        var after = DateTime.Now;

        // 期望值取"写入**前后**两个时间戳"之一：Serilog 用的是首条事件的时间，只取写入之后的
        // DateTime.Now 会在跨分钟/小时/日/年的那一瞬间与它不一致（极小概率但不必留）。
        var expected = new[] { before, after }
            .Select(t => format.Length == 0
                ? "server-.log"
                : $"server-{t.ToString(format, System.Globalization.CultureInfo.InvariantCulture)}.log")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains(Path.GetFileName(LogFiles(dir).Single()), expected);
    }

    // ---------------- T2 级别护栏（本次改动最要紧的一条） ----------------

    [Fact]
    public async Task 配置压低级时_带凭证的路径不得出现在文件里()
    {
        using var app = NewApp();
        var logs = Path.Combine(app.Root, "logs");
        app.With("logging:file:directory", logs)
           .With("logging:logLevel:Microsoft.AspNetCore", "Warning");

        var factory = app.GetService<ILoggerFactory>();
        var framework = factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
        var own = factory.CreateLogger("AnyDrop.Server.BlobService");

        // 过滤器语义：框架类别被压到 Warning，自家类别仍按 default=Information
        Assert.False(framework.IsEnabled(LogLevel.Information));
        Assert.False(framework.IsEnabled(LogLevel.Debug));
        Assert.True(framework.IsEnabled(LogLevel.Warning));
        Assert.True(own.IsEnabled(LogLevel.Information));

        // 发一个**带凭证**（假 blob id 就在路径里）的下载请求：这条路径如果落进日志，
        // 日志文件就成了可用的链接清单 —— 这正是要守的东西。
        var fakeId = "AAAAAAAAAAAAAAAAAAAAAAAAAA";
        var response = await app.NewClient().GetAsync($"/v1/blobs/{fakeId}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);

        var text = ReadAll(logs);
        Assert.Contains("AnyDrop 启动", text, StringComparison.Ordinal);       // 文件日志确实在工作
        Assert.Contains("[INF] AnyDrop.Server:", text, StringComparison.Ordinal); // 且带 ILogger<T> 注入的来源类别
        Assert.DoesNotContain("Request starting", text, StringComparison.Ordinal); // 逐请求行没有落盘
        Assert.DoesNotContain(fakeId, text, StringComparison.Ordinal);          // 那条凭证也没有落盘
    }

    [Fact]
    public void 目录不可写时_返回null并给出原因_服务不因此启动失败()
    {
        var root = NewDir();
        // 在"应该是目录"的位置放一个文件：Directory.CreateDirectory 会抛 IOException
        var blocked = Path.Combine(root, "blocked");
        File.WriteAllText(blocked, "占位");
        var config = LoadConfig(("logging:file:directory", blocked));

        var logger = LogFileSetup.TryCreate(config, out var problem);

        Assert.Null(logger);
        Assert.False(string.IsNullOrWhiteSpace(problem));
        // 断言"原因确实指向被挡住的那个位置"：否则一个"无论什么原因都返回 null"的实现也能过。
        Assert.Contains(blocked, problem, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(blocked));
        Assert.False(Directory.Exists(blocked));
    }

    // ---------------- T3 命名冲突防护 ----------------

    [Fact]
    public void logging_file_段的存在不影响_logLevel_的过滤器取值()
    {
        var dir = NewDir();
        using var app = NewApp();
        app.With("logging:file:directory", dir)
           .With("logging:logLevel:default", "Warning")
           .With("logging:logLevel:AnyDrop.Server.Storage.BlobStore", "Error");

        var factory = app.GetService<ILoggerFactory>();
        Assert.False(factory.CreateLogger("AnyDrop.Server.TokenService").IsEnabled(LogLevel.Information));
        Assert.True(factory.CreateLogger("AnyDrop.Server.TokenService").IsEnabled(LogLevel.Warning));
        Assert.False(factory.CreateLogger("AnyDrop.Server.Storage.BlobStore").IsEnabled(LogLevel.Warning));

        // 光看 IsEnabled 还不够：必须证明"文件日志确实在写、且写进去的内容也归这套级别管"。
        // 在 default=Warning 下写一条 Warning 与一条 Information，看谁进了文件。
        var own = factory.CreateLogger("AnyDrop.Server.TokenService");
        own.LogWarning("警告级必须落盘");
        own.LogInformation("信息级必须被压掉");

        var text = ReadAll(dir);
        Assert.Contains("警告级必须落盘", text, StringComparison.Ordinal);
        Assert.DoesNotContain("信息级必须被压掉", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AnyDrop 启动", text, StringComparison.Ordinal);   // 启动行是 Information，同样被压掉
    }

    // ---------------- T8 未启用时不碰文件系统 ----------------

    [Fact]
    public void 未启用时不建目录也不返回logger()
    {
        var dir = Path.Combine(NewDir(), "logs-off");
        var config = LoadConfig(("logging:file:enabled", "false"), ("logging:file:directory", dir));

        var logger = LogFileSetup.TryCreate(config, out var problem);

        Assert.Null(logger);
        Assert.Null(problem);
        Assert.False(Directory.Exists(dir));
    }

    // ---------------- T9 输出格式 ----------------

    [Fact]
    public void 每行含时间戳_级别与来源类别_且是单行()
    {
        var dir = NewDir();
        var config = LoadConfig(("logging:file:directory", dir));
        using var logger = LogFileSetup.TryCreate(config, out var problem);
        Assert.Null(problem);
        Assert.NotNull(logger);

        logger!.Information("hello {Who}", "world");

        var lines = Lines(dir);
        var line = Assert.Single(lines);
        // 裸 Serilog logger 没有 SourceContext（那是 ILogger<T> 经 MS 管线注入的），
        // 所以这里只断言时间戳/级别/单行/消息；SourceContext 由 T2 走真实管线验证。
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} \[INF\] .*hello world$", line);
    }

    // ---------------- T7 缓冲模式下退出即刷盘 ----------------

    [Fact]
    public void 开启缓冲时_未释放前看不到_释放后刷进文件()
    {
        var dir = NewDir();
        var config = LoadConfig(
            ("logging:file:directory", dir),
            ("logging:file:buffered", "true"),
            ("logging:file:flushToDiskIntervalSeconds", "60"));   // 故意设很长：只能靠 Dispose 刷

        var logger = LogFileSetup.TryCreate(config, out var problem);
        Assert.Null(problem);
        logger!.Information("缓冲里的最后一行");

        // 这一半才是判别力所在：不缓冲时下面这句会立刻为真，测试就退化成恒真
        Assert.DoesNotContain("缓冲里的最后一行", ReadAll(dir), StringComparison.Ordinal);

        logger.Dispose();
        Assert.Contains("缓冲里的最后一行", ReadAll(dir), StringComparison.Ordinal);
    }

    // ---------------- T5 保留上限 ----------------

    [Fact]
    public void 超过保留上限时_最旧的文件被删除_最新的仍在()
    {
        var dir = NewDir();
        var config = LoadConfig(
            ("logging:file:directory", dir),
            ("logging:file:rollingInterval", "Infinite"),   // 只按大小滚动，便于造多个文件
            ("logging:file:retainedFileCountLimit", "3"),
            ("logging:file:fileSizeLimitBytes", "2048"),
            ("logging:file:rollOnFileSizeLimit", "true"));

        using var logger = LogFileSetup.TryCreate(config, out var problem);
        Assert.Null(problem);
        for (var i = 0; i < 200; i++)
            logger!.Information("第 {Index} 行，填充到足以触发按大小滚动：{Pad}", i, new string('x', 120));

        var files = LogFiles(dir);
        // 上界与下界都要有牙：若"按大小滚动"被拆掉（rollOnFileSizeLimit=false），只会剩 1 个文件；
        // 若"保留上限"被拆掉，文件数会远大于 3。
        Assert.Equal(3, files.Length);

        var text = ReadAll(dir);
        Assert.Contains("第 199 行", text, StringComparison.Ordinal);       // 最新那条必须在
        Assert.DoesNotContain("第 0 行", text, StringComparison.Ordinal);   // 最旧的已经被删掉
    }

    [Fact]
    public void 保留上限为0表示不限_旧文件不被删()
    {
        var dir = NewDir();
        var config = LoadConfig(
            ("logging:file:directory", dir),
            ("logging:file:rollingInterval", "Infinite"),
            ("logging:file:retainedFileCountLimit", "0"),      // 0 = 不限
            ("logging:file:fileSizeLimitBytes", "2048"),
            ("logging:file:rollOnFileSizeLimit", "true"));

        using var logger = LogFileSetup.TryCreate(config, out var problem);
        Assert.Null(problem);
        for (var i = 0; i < 200; i++)
            logger!.Information("第 {Index} 行，填充到足以触发按大小滚动：{Pad}", i, new string('x', 120));

        Assert.True(LogFiles(dir).Length > 3, $"0 应当表示不限，实际只有 {LogFiles(dir).Length} 个文件");
        Assert.Contains("第 0 行", ReadAll(dir), StringComparison.Ordinal);
    }

    [Fact]
    public void 单文件上限为0表示不限_不按大小滚动()
    {
        var dir = NewDir();
        var config = LoadConfig(
            ("logging:file:directory", dir),
            ("logging:file:rollingInterval", "Infinite"),
            ("logging:file:retainedFileCountLimit", "5"),
            ("logging:file:fileSizeLimitBytes", "0"),          // 0 = 不限
            ("logging:file:rollOnFileSizeLimit", "true"));

        using var logger = LogFileSetup.TryCreate(config, out var problem);
        Assert.Null(problem);
        for (var i = 0; i < 200; i++)
            logger!.Information("第 {Index} 行，填充到足以触发按大小滚动：{Pad}", i, new string('x', 120));

        Assert.Single(LogFiles(dir));
    }

    // ---------------- T6 单条超过单文件上限 / 上限到达后的行为 ----------------

    [Fact]
    public void 单条日志超过单文件上限时_当前文件为空则整条写入而不丢弃()
    {
        var dir = NewDir();
        var config = LoadConfig(
            ("logging:file:directory", dir),
            ("logging:file:rollingInterval", "Infinite"),
            ("logging:file:retainedFileCountLimit", "5"),
            ("logging:file:fileSizeLimitBytes", "512"),      // 比下面那一条还小
            ("logging:file:rollOnFileSizeLimit", "true"));

        using var logger = LogFileSetup.TryCreate(config, out var problem);
        Assert.Null(problem);
        logger!.Information("超长单条：{Pad}", new string('y', 4000));

        // 实测行为（不是推断）：当前文件为空时，超限的那一条**不做滚动、直接整条写入** ——
        // 即"宁可让文件超过上限，也不写半条、也不丢"。这里把它钉死。
        Assert.Single(LogFiles(dir));
        Assert.Contains("超长单条", ReadAll(dir), StringComparison.Ordinal);
    }

    [Fact]
    public void 单文件上限到达后_关闭按大小滚动就不再写入()
    {
        var dir = NewDir();
        var config = LoadConfig(
            ("logging:file:directory", dir),
            ("logging:file:rollingInterval", "Infinite"),
            ("logging:file:retainedFileCountLimit", "5"),
            ("logging:file:fileSizeLimitBytes", "512"),
            ("logging:file:rollOnFileSizeLimit", "false"));   // ← 这一条是关键

        using var logger = LogFileSetup.TryCreate(config, out var problem);
        Assert.Null(problem);
        logger!.Information("先把上限撑满：{Pad}", new string('z', 2000));
        logger.Information("上限之后写的那一行");

        // 正向前置：没有它，"一条都没写出去"的实现（例如级别被整体掐掉）也能满足下面那条负断言。
        Assert.Contains("先把上限撑满", ReadAll(dir), StringComparison.Ordinal);
        // 这是 AppConfig 里写明的契约：false 时"到达上限后到下一个滚动点前不再写入（会静默丢日志）"。
        // 正因如此，LogFileSetup 默认给的是 true。
        Assert.DoesNotContain("上限之后写的那一行", ReadAll(dir), StringComparison.Ordinal);
    }

    // ---------------- T4 随程序退出的收尾 ----------------

    [Fact]
    public void 释放后不再写_且探测文件不残留()
    {
        var dir = NewDir();
        var config = LoadConfig(("logging:file:directory", dir));
        var logger = LogFileSetup.TryCreate(config, out _);
        Assert.NotNull(logger);
        logger!.Information("写一行");
        logger.Dispose();

        Assert.Empty(Directory.GetFiles(dir, "*write-probe*"));
        Assert.Contains("写一行", ReadAll(dir), StringComparison.Ordinal);
    }

    // ---------------- 宿主释放时 flush（Program.cs 的接线回归网） ----------------
    [Fact]
    public async Task 缓冲模式下_到刷盘间隔就落盘_不必等释放()
    {
        var dir = NewDir();
        var config = LoadConfig(
            ("logging:file:directory", dir),
            ("logging:file:buffered", "true"),
            ("logging:file:flushToDiskIntervalSeconds", "1"));   // 这一项只被本用例覆盖

        using var logger = LogFileSetup.TryCreate(config, out var problem);
        Assert.Null(problem);
        logger!.Information("按间隔刷盘的那一行");

        // 故意**不** Dispose：只等到刷盘间隔生效。把 flushToDiskInterval 的映射丢掉，
        // 这一行就永远不会落盘（T7 与宿主释放那条都只靠 Dispose，覆盖不到这里）。
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !ReadAll(dir).Contains("按间隔刷盘的那一行", StringComparison.Ordinal))
            await Task.Delay(200);

        Assert.Contains("按间隔刷盘的那一行", ReadAll(dir), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 缓冲模式下_宿主释放后尾部日志仍在文件里()
    {
        // 日志目录故意放在 app.Root **之外**：TestApp 释放时会删掉自己的 Root，
        // 放在里面就没法在"宿主已释放、目录还在"的窗口里读文件。
        var logs = NewDir();
        var app = NewApp(
            ("logging:file:directory", logs),
            ("logging:file:buffered", "true"),
            ("logging:file:flushToDiskIntervalSeconds", "60"));   // 只能靠释放时刷

        app.GetService<ILoggerFactory>().CreateLogger("AnyDrop.Server.Probe").LogWarning("宿主释放前的最后一行");
        Assert.DoesNotContain("宿主释放前的最后一行", ReadAll(logs), StringComparison.Ordinal);   // 确认真的在缓冲

        // 关键：provider 必须是**容器托管的**，否则它的 Dispose 永远不会被调用、这一行就没了。
        // 把 Program.cs 的注册改回 Logging.AddProvider(实例) 会让这条测试变红。
        await app.DisposeAsync();

        Assert.Contains("宿主释放前的最后一行", ReadAll(logs), StringComparison.Ordinal);
    }

    // ---------------- 并发写 ----------------

    [Fact]
    public void 多线程并发写入_不丢行也不撕裂行()
    {
        var dir = NewDir();
        var config = LoadConfig(("logging:file:directory", dir));
        using var logger = LogFileSetup.TryCreate(config, out var problem);
        Assert.Null(problem);

        const int threads = 4, perThread = 25;
        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++) logger!.Information("并发标记 {T}-{I}", t, i);
        });

        var lines = Lines(dir);
        Assert.Equal(threads * perThread, lines.Length);
        for (var t = 0; t < threads; t++)
            for (var i = 0; i < perThread; i++)
                Assert.Contains(lines, l => l.EndsWith($"并发标记 {t}-{i}", StringComparison.Ordinal));
    }
}
