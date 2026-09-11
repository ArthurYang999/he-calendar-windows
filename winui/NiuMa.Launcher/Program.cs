using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace NiuMaCalendar;

internal static class Program
{
    // Bump when the embedded WinUI payload changes so users re-extract.
    private const string PayloadVersion = "1.7.0-20260911c";

    [STAThread]
    private static void Main()
    {
        try
        {
            var appRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HeCalendar",
                "NiuMaApp");
            var marker = Path.Combine(appRoot, ".payload-version");
            var exePath = Path.Combine(appRoot, "HeCalendar.Shell.exe");

            var needExtract = !File.Exists(exePath)
                || !File.Exists(marker)
                || !string.Equals(File.ReadAllText(marker).Trim(), PayloadVersion, StringComparison.Ordinal);

            if (needExtract)
            {
                foreach (var name in new[] { "HeCalendar.Shell", "牛马日历", "合社日历" })
                {
                    try
                    {
                        foreach (var p in Process.GetProcessesByName(name))
                        {
                            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                            try { p.WaitForExit(3000); } catch { /* ignore */ }
                        }
                    }
                    catch { /* ignore */ }
                }

                if (Directory.Exists(appRoot))
                {
                    try { Directory.Delete(appRoot, recursive: true); }
                    catch
                    {
                        // Fallback: clear files best-effort
                        try
                        {
                            foreach (var f in Directory.GetFiles(appRoot, "*", SearchOption.AllDirectories))
                                File.SetAttributes(f, FileAttributes.Normal);
                            Directory.Delete(appRoot, recursive: true);
                        }
                        catch { /* ignore */ }
                    }
                }

                Directory.CreateDirectory(appRoot);
                ExtractPayload(appRoot);
                File.WriteAllText(marker, PayloadVersion);
            }

            if (!File.Exists(exePath))
            {
                Message("未找到应用主体 HeCalendar.Shell.exe，安装包可能损坏。");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = appRoot,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Message("启动失败：\n" + ex.Message);
        }
    }

    private static void ExtractPayload(string targetDir)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
        if (name == null)
            throw new InvalidOperationException("启动器未包含 payload.zip 资源。");

        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("无法读取 payload.zip。");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        zip.ExtractToDirectory(targetDir, overwriteFiles: true);
    }

    private static void Message(string text)
    {
        try
        {
            // Avoid WinForms dependency — use MessageBox via user32.
            MessageBoxW(IntPtr.Zero, text, "牛马日历", 0x00000010);
        }
        catch
        {
            Console.Error.WriteLine(text);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
