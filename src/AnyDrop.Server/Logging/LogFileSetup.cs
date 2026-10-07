using Serilog;
using Serilog.Debugging;
using Serilog.Events;

namespace AnyDrop.Server.Logging;

/// <summary>
/// 只负责把 <see cref="LogFileOptions"/> 映射成 Serilog 的文件 sink。
/// <para>
/// <b>它不决定日志级别。</b>级别仍然只由 MS 的 <c>Logging:LogLevel</c> 过滤器（含
/// <c>ANYDROP__Logging__LogLevel__*</c>）决定，所以这里从 <see cref="LogEventLevel.Verbose"/>
/// 起步、不在 Serilog 侧做二次过滤 —— 否则就会出现"两套级别配置谁赢"的问题。
/// </para>
/// <para>
/// 它也**不负责**：业务日志内容、目录之外的文件生命周期管理（旧文件由 sink 的保留上限处理）、
/// 日志的轮转策略本身（交给 sink）。
/// </para>
/// </summary>
public static class LogFileSetup
{
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// 构造文件 logger；返回 <c>null</c> 表示"未启用"或"不可用"。
    /// 调用方在拿到非 null 的 <paramref name="problem"/> 时应把它打到 stderr 并继续启动
    /// （sink 的运行时失败是**静默**的，只进 SelfLog，所以必须显式报告）。
    /// </summary>
    public static Serilog.Core.Logger? TryCreate(AppConfig config, out string? problem)
    {
        problem = null;
        var options = config.LogFile;
        if (!options.Enabled) return null;

        // 目录创建 + 写探测。探测的是"**目录**能不能写"，且用带 PID+GUID 的唯一名字：
        // 固定名字会在两个实例同时启动时互撞，把"目录可写"误判成"不可用"（实测 8 路并发里有 1 路被误判，
        // 而误判的后果是该进程整个生命周期都没有文件日志）。
        //
        // 探测**不**覆盖"当天那个目标文件本身不可用"（目标位置被同名目录占住、或目标文件是只读的）：
        // 那两种情况下 sink 会在 emit 阶段抛异常，被 SafeAggregateSink 吞掉、只进 SelfLog ——
        // 也就是说信号**在 stderr 里**，而部署形态下 stderr 被重定向进 logs\bootstrap.log。
        // 排查入口写在 RUNBOOK 的排障表里，这里不假装能覆盖它。
        try
        {
            Directory.CreateDirectory(config.LogDirectory);
            // 顺手清掉上次"探测窗口内崩溃"留下的 0 字节探测文件：唯一名意味着它们不会被复用，
            // 而保留策略只认日志文件、不管这类 .tmp。
            // ⚠ 整段必须包在**它自己的** try 里：清扫是 best-effort，不能掉进下面那条
            // "目录不可用就降级"的宽 catch —— 否则一个"能创建文件但不能列目录"的权限
            // （Windows 只给 CreateFiles/WriteData 不给 ListDirectory）就会把整条文件日志弄没，
            // 那比留下几个 .tmp 严重得多。
            try
            {
                foreach (var stale in Directory.GetFiles(config.LogDirectory, $"{options.FileNamePrefix}write-probe-*.tmp"))
                {
                    try { File.Delete(stale); }
                    catch (IOException) { /* 正被另一个实例探测占用，留着 */ }
                    catch (UnauthorizedAccessException) { /* 同上 */ }
                }
            }
            catch (IOException) { /* 枚举失败 → 跳过清扫，文件日志照常建 */ }
            catch (UnauthorizedAccessException) { /* 同上 */ }
            var probe = Path.Combine(
                config.LogDirectory,
                $"{options.FileNamePrefix}write-probe-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(probe, "");
            }
            finally
            {
                try { File.Delete(probe); }
                catch (IOException) { /* 探测文件删不掉不影响主流程 */ }
                catch (UnauthorizedAccessException) { /* 同上 */ }
            }
        }
        // 这是一个刻意的"降级而不是死掉"边界：任何原因导致日志目录不可用，
        // 服务都必须照常起来（R6），只把原因报给 stderr。
        catch (Exception ex)
        {
            problem = ex.Message;
            return null;
        }

        // SelfLog 是**进程级全局状态**，且是赋值语义（后一次 Enable 顶掉前一次，没有配对的 Disable）。
        // 这里用它把 sink 的运行时失败引到 stderr；部署形态下 stderr 被 service-run.cmd 重定向进
        // logs\bootstrap.log —— 所以故障信号在**那个**文件里，而它由包装脚本按 32 MiB 改名，不会无限增长。
        SelfLog.Enable(message => Console.Error.WriteLine($"[Serilog SelfLog] {message}"));

        var path = Path.Combine(config.LogDirectory, options.FileNamePrefix + ".log");
        return new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(
                path: path,
                rollingInterval: ToRollingInterval(options.RollingInterval),
                retainedFileCountLimit: options.RetainedFileCountLimit == 0 ? null : options.RetainedFileCountLimit,
                fileSizeLimitBytes: options.FileSizeLimitBytes == 0 ? null : options.FileSizeLimitBytes,
                rollOnFileSizeLimit: options.RollOnFileSizeLimit,
                buffered: options.Buffered,
                flushToDiskInterval: options.FlushToDiskIntervalSeconds > 0
                    ? TimeSpan.FromSeconds(options.FlushToDiskIntervalSeconds)
                    : null,
                outputTemplate: OutputTemplate)
            .CreateLogger();
    }

    /// <summary>取值已在 <c>ConfigLoader</c> 校验过（大小写也归一化了），这里只做映射。</summary>
    private static RollingInterval ToRollingInterval(string name) => name switch
    {
        "Infinite" => RollingInterval.Infinite,
        "Year" => RollingInterval.Year,
        "Month" => RollingInterval.Month,
        "Day" => RollingInterval.Day,
        "Hour" => RollingInterval.Hour,
        "Minute" => RollingInterval.Minute,
        _ => throw new InvalidOperationException($"未知的 rollingInterval：{name}"),
    };
}
