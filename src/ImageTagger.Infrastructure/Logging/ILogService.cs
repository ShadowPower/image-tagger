namespace ImageTagger.Infrastructure.Logging;

/// <summary>
/// 应用日志抽象（DESIGN 18.2）。
/// 默认不记录完整路径、Prompt、元数据或图片内容；
/// 调用方先用 <see cref="SerilogFileLogger"/> 的脱敏帮助方法处理路径。
/// </summary>
public interface ILogService
{
    void Info(string message);

    void Warning(string message);

    void Error(string message, Exception? exception = null);

    /// <summary>
    /// 同一 key 在抑制窗口内只记录一次并合并计数，避免批量错误轰炸日志。
    /// </summary>
    void ErrorSuppressed(string dedupKey, string message, Exception? exception = null);

    /// <summary>结构化推理记录：只记指纹、provider、设备与耗时。</summary>
    void LogInference(string modelFingerprint, string provider, string device, double durationMs);
}
