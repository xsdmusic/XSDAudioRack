using System;
using System.IO;
using System.Text;

namespace XSDAudioRack;

/// <summary>
/// 诊断日志：写到 %APPDATA%\XSDAudioRack\engine.log，用于排查音频通路问题。
/// 线程安全（简单锁），异常全部吞掉，绝不影响音频线程。
/// </summary>
internal static class DiagLog
{
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XSDAudioRack", "engine.log");

    public static void Write(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    public static void WriteBlock(string title, string body)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {title}\n");
            sb.Append(body);
            sb.Append('\n');
            File.AppendAllText(LogPath, sb.ToString());
        }
        catch { }
    }
}
