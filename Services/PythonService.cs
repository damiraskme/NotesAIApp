using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace MyApp.Services;

public sealed record PythonResult(string? Text, string? Message, string? Error);

public static class PythonService
{
    private static string PythonExecutable =>
        Environment.GetEnvironmentVariable("PYTHON_PATH") is { Length: > 0 } p ? p : "python";

    private static string ScriptPath
    {
        get
        {
            string projectScript = Path.Combine(ProjectPaths.ProjectDirectory, "Python", "backend.py");
            if (File.Exists(projectScript)) return projectScript;
            return Path.Combine(AppContext.BaseDirectory, "Python", "backend.py");
        }
    }

    public static async Task<PythonResult> RunAsync(string action, string text, CancellationToken ct = default)
    {
        if (!File.Exists(ScriptPath))
            return new PythonResult(null, null, $"No backend.py at {ScriptPath}");

        var psi = new ProcessStartInfo
        {
            FileName = PythonExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(ScriptPath);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        string request = new JsonObject { ["action"] = action, ["text"] = text }.ToJsonString();

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start Python.");

            await process.StandardInput.WriteAsync(request);
            process.StandardInput.Close();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(stderr) ? $"exit code {process.ExitCode}" : stderr.Trim();
                return new PythonResult(null, null, $"Python failed: {detail}");
            }

            JsonNode? json = JsonNode.Parse(stdout);
            return new PythonResult(
                json?["text"]?.GetValue<string>(),
                json?["message"]?.GetValue<string>(),
                json?["error"]?.GetValue<string>());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new PythonResult(null, null,
                $"No'{PythonExecutable}'");
        }
        catch (Exception ex)
        {
            return new PythonResult(null, null, ex.Message);
        }
    }
}
