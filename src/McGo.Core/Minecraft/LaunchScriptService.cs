using System.Text;
using System.Text.RegularExpressions;

namespace McGo.Core.Minecraft;

public static class LaunchScriptService
{
    public static string SafeScriptName(string versionId)
    {
        var safe = Regex.Replace(versionId, """[<>:"/\\|?*]+""", "_").Trim();
        return safe.Length == 0 ? "minecraft" : safe;
    }

    public static string BuildWindowsBatchScript(IReadOnlyList<string> command, string? preLaunchCommand = null)
    {
        if (command.Count == 0)
        {
            throw new ArgumentException("启动命令不能为空。", nameof(command));
        }

        var builder = new StringBuilder();
        builder.AppendLine("@echo off");
        builder.AppendLine("cd /d %~dp0\\..");
        if (!string.IsNullOrWhiteSpace(preLaunchCommand))
        {
            builder.AppendLine(preLaunchCommand.Trim());
        }

        builder.AppendLine(QuoteWindowsCommand(command));
        builder.AppendLine("pause");
        return builder.ToString().Replace("\n", "\r\n");
    }

    public static string QuoteWindowsCommand(IEnumerable<string> arguments)
        => string.Join(' ', arguments.Select(QuoteWindowsArgument));

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        var needsQuotes = argument.Any(char.IsWhiteSpace) || argument.Contains('"');
        if (!needsQuotes)
        {
            return argument;
        }

        var builder = new StringBuilder();
        builder.Append('"');
        var backslashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(ch);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }
}
