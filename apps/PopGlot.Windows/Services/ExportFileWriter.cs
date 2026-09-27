using System.Text;

namespace PopGlot.Windows.Services;

/// <summary>
/// Creates an export file without replacing anything that already exists.
/// A failed write removes the partial file and is reported as a short error.
/// </summary>
internal static class ExportFileWriter
{
    public const int MaxAttempts = 1000;

    public readonly record struct WrittenExport(string FullPath, string FileName);

    /// <summary>Test seam. When set, it replaces the exclusive file create.</summary>
    internal static Action<string, byte[]>? WriterOverride;

    public static void ResetForTests() => WriterOverride = null;

    public static WrittenExport WriteExclusive(
        string directory,
        string fileStem,
        string extension,
        string content,
        DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new IOException("导出文件夹不存在。请另选一个可写的文件夹。");
        }

        var ext = string.IsNullOrWhiteSpace(extension)
            ? ".txt"
            : extension.StartsWith('.') ? extension : "." + extension;
        var stamp = timestamp.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content ?? string.Empty);
        IOException? lastCollision = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var suffix = attempt == 0
                ? string.Empty
                : "-" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var fileName = $"{fileStem}_{stamp}{suffix}{ext}";
            var path = Path.Combine(directory, fileName);
            var existedBefore = File.Exists(path);
            try
            {
                if (WriterOverride is { } writer)
                {
                    writer(path, bytes);
                }
                else
                {
                    WriteCreateNew(path, bytes);
                }

                return new WrittenExport(path, fileName);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A test seam that creates the destination and then fails must
                // not leave that partial behind. A lost race against another
                // exclusive create must not delete the winner.
                if (WriterOverride is not null)
                {
                    TryDeletePartial(path);
                }

                if (existedBefore || (WriterOverride is null && File.Exists(path)))
                {
                    lastCollision = exception as IOException ?? new IOException(exception.Message, exception);
                    continue;
                }

                throw new IOException(DescribeWriteFailure(exception), exception);
            }
        }

        throw new IOException("同一时刻的导出太多，请稍后再试。", lastCollision);
    }

    public static string DescribeWriteFailure(Exception exception)
    {
        if (exception is UnauthorizedAccessException)
        {
            return "无法写入导出文件。请检查文件夹是否只读，或另选一个可写位置。";
        }

        var message = exception.Message ?? string.Empty;
        if (message.Contains("只读", StringComparison.Ordinal) ||
            message.Contains("可写", StringComparison.Ordinal) ||
            message.Contains("read-only", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("拒绝", StringComparison.Ordinal))
        {
            return "无法写入导出文件。请检查文件夹是否只读，或另选一个可写位置。";
        }

        if (message.Contains("space", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("disk", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("磁盘", StringComparison.Ordinal))
        {
            return "磁盘空间不足，导出未完成。请清理空间后重试。";
        }

        if (message.Contains("不存在", StringComparison.Ordinal))
        {
            return "导出文件夹不存在。请另选一个可写的文件夹。";
        }

        return "导出未完成。请换一个文件夹后重试。";
    }

    private static void WriteCreateNew(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temp, path);
        }
        catch
        {
            TryDeletePartial(temp);
            throw;
        }
    }

    private static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
