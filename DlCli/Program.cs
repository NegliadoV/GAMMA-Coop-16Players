using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GammaZoneLauncher;

internal static class DlCliProgram
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.WriteLine("[ERROR] Missing ModDB start URL.");
            return 1;
        }

        string startUrl = args[0].Trim();
        string profileDir = @"C:\Users\stanis\Desktop\GAMMA-Coop\chrome-profile";
        if (!Directory.Exists(profileDir))
        {
            profileDir = Path.Combine(Path.GetTempPath(), "gamma-moddb-cdp-profile");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var resolver = new ModDbCdpResolver(profileDir, null, 9333);
        string cdnUrl = await resolver.ResolveDirectUrlAsync(startUrl, 0, cts.Token);
        if (!string.IsNullOrEmpty(cdnUrl) && cdnUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(cdnUrl);
            return 0;
        }

        Console.WriteLine("[ERROR] Failed to generate download link.");
        return 1;
    }
}
