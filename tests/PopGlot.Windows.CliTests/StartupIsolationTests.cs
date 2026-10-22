using System.Diagnostics;
using System.IO;

namespace PopGlot.Windows.CliTests;

internal static partial class Program
{
    private static async Task AppConstructionDoesNotReadStores()
    {
        var root = Path.Combine(Path.GetTempPath(), $"popglot-construction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        const string corrupt = "{ isolated corrupt history";
        var historyPath = Path.Combine(root, "history.json");
        File.WriteAllText(historyPath, corrupt);
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--probe-app-construction");
            start.ArgumentList.Add(root);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated application constructor");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await child.WaitForExitAsync(timeout.Token); }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(); }
            }
            Assert(child.ExitCode == 0, "application construction must not read or quarantine history before OnStartup chooses the data root");
            Assert(File.ReadAllText(historyPath) == corrupt && !Directory.EnumerateFiles(root, "*.corrupt-*").Any(),
                "an unstarted application must leave existing data untouched");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
