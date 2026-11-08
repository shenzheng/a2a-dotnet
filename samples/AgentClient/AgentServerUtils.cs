using System.Diagnostics;

namespace AgentClient;

internal static class AgentServerUtils
{
    private static readonly HttpClient s_httpClient = new();
    private static readonly List<Process> s_runningProcesses = new();

    public static async Task StartLocalAgentServerAsync(string agentName, uint port)
    {
        // Check if server is already running on the specified port
        if (await WaitForServerReadyAsync(port, timeout: TimeSpan.FromSeconds(5), checkOnly: true))
        {
            return;
        }

        // Start the new server process in a new window
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --agent {agentName} --urls http://localhost:{port}",
            WorkingDirectory = GetAgentServerDirectoryPath(),
            UseShellExecute = true,
            CreateNoWindow = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start AgentServer process");
        }

        s_runningProcesses.Add(process);

        // Wait for the server to be ready
        await WaitForServerReadyAsync(port, timeout: TimeSpan.FromSeconds(30));
    }

    private static string GetAgentServerDirectoryPath()
    {
        const string projectFileName = "AgentServer.csproj";

        var fromEnv = Environment.GetEnvironmentVariable("A2A_AGENT_SERVER_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var envPath = Path.GetFullPath(fromEnv);
            if (IsAgentServerProjectDirectory(envPath, projectFileName))
            {
                return envPath;
            }
        }

        foreach (var candidate in GetAgentServerDirectoryCandidates())
        {
            if (IsAgentServerProjectDirectory(candidate, projectFileName))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not find AgentServer directory. cwd={Directory.GetCurrentDirectory()}, " +
            $"BaseDirectory={AppContext.BaseDirectory}. Set A2A_AGENT_SERVER_DIR or run from samples/AgentClient.");
    }

    private static IEnumerable<string> GetAgentServerDirectoryCandidates()
    {
        var cwd = Directory.GetCurrentDirectory();
        yield return Path.GetFullPath(Path.Combine(cwd, "..", "AgentServer"));
        yield return Path.GetFullPath(Path.Combine(cwd, ".worktrees", "a2a-dotnet", "samples", "AgentServer"));

        var probe = AppContext.BaseDirectory;
        for (var depth = 0; depth < 12; depth++)
        {
            yield return Path.GetFullPath(Path.Combine(probe, "AgentServer"));
            var parent = Directory.GetParent(probe);
            if (parent is null)
            {
                break;
            }

            probe = parent.FullName;
        }

        // Legacy relative paths (IDE / dotnet run from AgentClient project directory)
        yield return Path.GetFullPath(Path.Combine(cwd, "..", "..", "..", "..", "AgentServer"));
    }

    private static bool IsAgentServerProjectDirectory(string directoryPath, string projectFileName) =>
        Directory.Exists(directoryPath) && File.Exists(Path.Combine(directoryPath, projectFileName));

    public static async Task<bool> WaitForServerReadyAsync(uint port, TimeSpan timeout, bool checkOnly = false)
    {
        var healthUrl = $"http://localhost:{port}/health";
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < timeout)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                var response = await s_httpClient.GetAsync(healthUrl, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // Ignore connection errors while waiting for server to start
            }

            if (checkOnly)
            {
                return false;
            }

            await Task.Delay(500);
        }

        if (checkOnly)
        {
            return false;
        }

        throw new TimeoutException($"AgentServer did not start successfully within {timeout.TotalSeconds} seconds");
    }

    public static async Task StopLocalAgentServersAsync()
    {
        // Stop our tracked processes
        foreach (var process in s_runningProcesses.ToList())
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    await process.WaitForExitAsync();
                    Console.WriteLine($"Stopped AgentServer process with PID {process.Id}");
                }
                process.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping process {process.Id}: {ex.Message}");
            }
        }
        s_runningProcesses.Clear();
    }
}
