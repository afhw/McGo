using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace McGo.SetupBootstrap;

internal static class Program
{
    private static readonly byte[] FooterMagic = Encoding.ASCII.GetBytes("MCGOSETUPv1!");

    [STAThread]
    private static int Main(string[] args)
    {
        var extractionRoot = Path.Combine(Path.GetTempPath(), $"McGo-Setup-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(extractionRoot);
            var payloadPath = Path.Combine(extractionRoot, "payload.zip");
            ExtractAttachedPayload(payloadPath);

            var appDirectory = Path.Combine(extractionRoot, "app");
            ZipFile.ExtractToDirectory(payloadPath, appDirectory);
            File.Delete(payloadPath);

            var installerPath = Path.Combine(appDirectory, "McGo.WinUI.exe");
            if (!File.Exists(installerPath))
            {
                throw new InvalidDataException("安装包中缺少 McGo.WinUI.exe。");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                WorkingDirectory = appDirectory,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--installer");
            startInfo.ArgumentList.Add("--source=" + appDirectory);
            foreach (var argument in args)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var installer = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 McGo 安装界面。");
            installer.WaitForExit();
            return installer.ExitCode;
        }
        catch (Exception exc)
        {
            MessageBox(IntPtr.Zero, "无法启动 McGo 安装程序。\n\n" + exc.Message, "McGo 安装程序", 0x10);
            return 1;
        }
        finally
        {
            DeleteDirectoryWithRetries(extractionRoot);
        }
    }

    private static void ExtractAttachedPayload(string payloadPath)
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定安装程序路径。");
        using var executable = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footerLength = sizeof(long) + FooterMagic.Length;
        if (executable.Length <= footerLength)
        {
            throw new InvalidDataException("安装程序没有有效负载。");
        }

        executable.Position = executable.Length - footerLength;
        Span<byte> footer = stackalloc byte[footerLength];
        executable.ReadExactly(footer);
        var payloadLength = BinaryPrimitives.ReadInt64LittleEndian(footer[..sizeof(long)]);
        if (!footer[sizeof(long)..].SequenceEqual(FooterMagic) || payloadLength <= 0 || payloadLength > executable.Length - footerLength)
        {
            throw new InvalidDataException("安装程序有效负载校验失败。");
        }

        executable.Position = executable.Length - footerLength - payloadLength;
        using var payload = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        CopyExactly(executable, payload, payloadLength);
    }

    private static void CopyExactly(Stream source, Stream destination, long bytesToCopy)
    {
        var buffer = new byte[1024 * 1024];
        while (bytesToCopy > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, bytesToCopy));
            if (read == 0)
            {
                throw new EndOfStreamException("安装程序有效负载不完整。");
            }

            destination.Write(buffer, 0, read);
            bytesToCopy -= read;
        }
    }

    private static void DeleteDirectoryWithRetries(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch when (attempt < 4)
            {
                Thread.Sleep(300 * (attempt + 1));
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
