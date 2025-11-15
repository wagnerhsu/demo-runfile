#!/bin/usr/env dotnet
#:package Spectre.Console@0.54.0
#:property AllowUnsafeBlocks=true

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Spectre.Console;

// Parse command-line arguments
var useParallel = args.Contains("--parallel", StringComparer.OrdinalIgnoreCase);

// Get the path of this script file
var scriptPath = AppContext.GetData("EntryPointFilePath")?.ToString();
if (string.IsNullOrEmpty(scriptPath))
{
    AnsiConsole.MarkupLine("[red]Error: Could not determine script path[/]");
    return 1;
}

var scriptDir = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory;
AnsiConsole.MarkupLine($"[cyan]Scanning for .cs files from:[/] {scriptDir}");
AnsiConsole.MarkupLine($"[cyan]Execution mode:[/] {(useParallel ? "Parallel" : "Sequential")}");
AnsiConsole.WriteLine();

// Find all .cs files in subdirectories
var csFiles = FindExecutableCsFiles(scriptDir);

if (csFiles.Count == 0)
{
    AnsiConsole.MarkupLine("[yellow]No executable .cs files found[/]");
    return 0;
}

AnsiConsole.MarkupLine($"[green]Found {csFiles.Count} .cs file(s) to verify[/]");
AnsiConsole.WriteLine();

// Run verification
var results = useParallel 
    ? await VerifyFilesParallel(csFiles)
    : await VerifyFilesSequential(csFiles);

// Display results in a table
DisplayResults(results);

// Return exit code based on results
var failedCount = results.Count(r => !r.Success);
return failedCount > 0 ? 1 : 0;

// --- Helper Methods ---

bool HasVerifyLaunchProfile(string csFilePath)
{
    try
    {
        var directory = Path.GetDirectoryName(csFilePath);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(csFilePath);
        var runJsonPath = Path.Combine(directory ?? ".", $"{fileNameWithoutExtension}.run.json");
        
        if (!File.Exists(runJsonPath))
        {
            return false;
        }
        
        var jsonContent = File.ReadAllText(runJsonPath);
        using var doc = JsonDocument.Parse(jsonContent);
        
        if (doc.RootElement.TryGetProperty("profiles", out var profiles))
        {
            return profiles.TryGetProperty("verify", out _);
        }
    }
    catch
    {
        // If we can't read or parse the file, just proceed without the launch profile
    }
    
    return false;
}

List<string> FindExecutableCsFiles(string rootDir)
{
    var files = new List<string>();
    
    foreach (var dir in Directory.GetDirectories(rootDir))
    {
        // Skip if directory contains a .csproj file
        if (Directory.GetFiles(dir, "*.csproj").Length > 0)
        {
            continue;
        }
        
        // Add all .cs files in this directory
        files.AddRange(Directory.GetFiles(dir, "*.cs"));
        
        // Recursively search subdirectories
        files.AddRange(FindExecutableCsFiles(dir));
    }
    
    return files;
}

async Task<List<VerificationResult>> VerifyFilesSequential(List<string> files)
{
    var results = new List<VerificationResult>();
    
    await AnsiConsole.Progress()
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new SpinnerColumn())
        .StartAsync(async ctx =>
        {
            var task = ctx.AddTask("[green]Verifying files[/]", maxValue: files.Count);
            
            foreach (var file in files)
            {
                var result = await VerifyFile(file);
                results.Add(result);
                task.Increment(1);
            }
        });
    
    return results;
}

async Task<List<VerificationResult>> VerifyFilesParallel(List<string> files)
{
    var results = new List<VerificationResult>();
    var lockObj = new Lock();
    
    await AnsiConsole.Progress()
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new SpinnerColumn())
        .StartAsync(async ctx =>
        {
            var progressTask = ctx.AddTask("[green]Verifying files[/]", maxValue: files.Count);
            
            var tasks = files.Select(async file =>
            {
                var result = await VerifyFile(file);
                lock (lockObj)
                {
                    results.Add(result);
                    progressTask.Increment(1);
                }
            });
            
            await Task.WhenAll(tasks);
        });
    
    return results;
}

async Task<VerificationResult> VerifyFile(string filePath)
{
    var result = new VerificationResult
    {
        FilePath = filePath,
        FileName = Path.GetFileName(filePath)
    };
    
    var startInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory
    };
    
    // Add arguments using ArgumentList to avoid escaping issues
    startInfo.ArgumentList.Add(filePath);
    
    // Check for .run.json with verify launch profile
    if (HasVerifyLaunchProfile(filePath))
    {
        startInfo.ArgumentList.Add("--launch-profile");
        startInfo.ArgumentList.Add("verify");
    }
    
    if (OperatingSystem.IsWindows())
    {
        startInfo.CreateNewProcessGroup = true;
    }
    
    var process = new Process { StartInfo = startInfo };
    var output = new List<string>();
    var error = new List<string>();
    var shutdownMessageDetected = false;
    var shutdownTcs = new TaskCompletionSource<bool>();
    
    process.OutputDataReceived += (s, e) =>
    {
        if (e.Data != null)
        {
            lock (output)
            {
                output.Add(e.Data);
                if (e.Data.Contains("Press Ctrl+C to shut down.") && !shutdownMessageDetected)
                {
                    shutdownMessageDetected = true;
                    shutdownTcs.TrySetResult(true);
                }
            }
        }
    };
    
    process.ErrorDataReceived += (s, e) =>
    {
        if (e.Data != null)
        {
            lock (error)
            {
                error.Add(e.Data);
            }
        }
    };
    
    var stopwatch = Stopwatch.StartNew();
    
    try
    {
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        // Wait for either: process exit, shutdown message detected, or timeout
        var timeout = TimeSpan.FromSeconds(10);
        var timeoutTask = Task.Delay(timeout);
        var processTask = Task.Run(process.WaitForExit);

        var completedTask = await Task.WhenAny(processTask, shutdownTcs.Task, timeoutTask);
        
        if (completedTask == shutdownTcs.Task)
        {
            // Shutdown message detected - send shutdown signal
            try
            {
                process.Stop();
                
                if (!process.HasExited)
                {
                    process.Kill();
                }
                
                stopwatch.Stop();
                result.Success = true;
                result.ExitCode = 0;
                result.Duration = stopwatch.Elapsed;
                result.Message = "App started & stopped successfully";
                
                result.FullOutput = string.Join("\n", output);
                result.FullError = string.Join("\n", error);
            }
            catch
            {
                process.Kill();
                stopwatch.Stop();
                result.Success = false;
                result.Duration = stopwatch.Elapsed;
                result.Message = "Failed to gracefully stop app";
                
                result.FullOutput = string.Join("\n", output);
                result.FullError = string.Join("\n", error);
            }
        }
        else if (completedTask == timeoutTask)
        {
            // Process timed out without shutdown message
            var allOutput = string.Join("\n", output);
            var allError = string.Join("\n", error);
            
            result.FullOutput = allOutput;
            result.FullError = allError;
            
            process.Kill();
            stopwatch.Stop();
            result.Success = false;
            result.Duration = stopwatch.Elapsed;
            result.Message = $"Timeout ({timeout.TotalSeconds}s)";
        }
        else
        {
            // Process completed normally
            stopwatch.Stop();
            result.ExitCode = process.ExitCode;
            result.Duration = stopwatch.Elapsed;
            result.Success = process.ExitCode == 0;
            result.Message = result.Success ? "Completed successfully" : $"Exit code: {process.ExitCode}";
            
            result.FullOutput = string.Join("\n", output);
            result.FullError = string.Join("\n", error);
            
            if (!result.Success && error.Count > 0)
            {
                result.Message = string.Join("; ", error.Take(2));
            }
        }
    }
    catch (Exception ex)
    {
        stopwatch.Stop();
        result.Success = false;
        result.Duration = stopwatch.Elapsed;
        result.Message = $"Error: {ex.Message}";
    }
    finally
    {
        if (!process.HasExited)
        {
            try { process.Kill(); } catch { }
        }
        process.Dispose();
    }
    
    return result;
}

void DisplayResults(List<VerificationResult> results)
{
    var table = new Table();
    table.Border(TableBorder.Rounded);
    table.AddColumn("[bold]File[/]");
    table.AddColumn("[bold]Status[/]");
    table.AddColumn("[bold]Duration[/]");
    table.AddColumn("[bold]Message[/]");
    
    foreach (var result in results.OrderBy(r => r.FileName))
    {
        var statusText = result.Success 
            ? "[green]✓ Pass[/]" 
            : $"[red]✗ Fail ({result.ExitCode})[/]";
        
        var durationText = $"{result.Duration.TotalSeconds:F2}s";
        
        // For failed apps, show full error output; for successful apps, show brief message
        string messageText;
        if (!result.Success)
        {
            // Combine error and output for failed cases
            var fullMessage = !string.IsNullOrEmpty(result.FullError) 
                ? result.FullError 
                : result.FullOutput;
            
            messageText = !string.IsNullOrEmpty(fullMessage) 
                ? fullMessage 
                : result.Message;
        }
        else
        {
            messageText = result.Message.Length > 60 
                ? result.Message.Substring(0, 57) + "..." 
                : result.Message;
        }
        
        table.AddRow(
            result.FileName,
            statusText,
            durationText,
            messageText.EscapeMarkup()
        );
    }
    
    AnsiConsole.Write(table);
    AnsiConsole.WriteLine();
    
    // Summary
    var totalCount = results.Count;
    var passCount = results.Count(r => r.Success);
    var failCount = totalCount - passCount;
    
    var summaryTable = new Table();
    summaryTable.Border(TableBorder.None);
    summaryTable.HideHeaders();
    summaryTable.AddColumn("");
    summaryTable.AddColumn("");
    
    summaryTable.AddRow("[bold]Total:[/]", totalCount.ToString());
    summaryTable.AddRow("[green]Passed:[/]", passCount.ToString());
    if (failCount > 0)
    {
        summaryTable.AddRow("[red]Failed:[/]", failCount.ToString());
    }
    
    AnsiConsole.Write(summaryTable);
    
    if (failCount == 0)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green bold]All tests passed! ✓[/]");
    }
}

class VerificationResult
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public TimeSpan Duration { get; set; }
    public string Message { get; set; } = "";
    public string FullOutput { get; set; } = "";
    public string FullError { get; set; } = "";
}

internal static partial class ProcessExtensions
{
    // Code in this class adapted from https://github.com/devlooped/dotnet-stop
    // See THIRDPARTYNOTICES for license information.
    extension(Process process)
    {
        public int Stop(TimeSpan? timeout = null, bool quiet = true)
        {
            timeout ??= TimeSpan.FromSeconds(1);
            if (OperatingSystem.IsWindows())
            {
                return process.StopWindowsProcess(timeout.Value, quiet);
            }
            else
            {
                return process.StopUnixProcess(timeout.Value, quiet);
            }
        }

        int StopUnixProcess(TimeSpan timeout, bool quiet)
        {
            if (!quiet)
            {
                AnsiConsole.MarkupLine($"[yellow]Shutting down {process.ProcessName}:{process.Id}...[/]");
            }

            var killProcess = new ProcessStartInfo("kill")
            {
                UseShellExecute = true
            };
            killProcess.ArgumentList.Add("-s");
            killProcess.ArgumentList.Add("SIGINT");
            killProcess.ArgumentList.Add(process.Id.ToString());
            Process.Start(killProcess)?.WaitForExit();

            if (timeout != TimeSpan.Zero)
            {
                if (process.WaitForExit(timeout))
                {
                    return 0;
                }

                if (!quiet)
                {
                    AnsiConsole.MarkupLine($"[red]Timed out waiting for process {process.ProcessName}:{process.Id} to exit[/]");
                }

                return -1;
            }
            else
            {
                process.WaitForExit();
                return 0;
            }
        }

        int StopWindowsProcess(TimeSpan timeout, bool quiet)
        {
            if (!quiet)
            {
                AnsiConsole.MarkupLine($"[yellow]Shutting down {process.ProcessName}:{process.Id}...[/]");
            }

            // Send Ctrl+Break to the process group
            GenerateConsoleCtrlEvent(1, (uint)process.Id);

            if (timeout != TimeSpan.Zero)
            {
                if (process.WaitForExit(timeout))
                {
                    return 0;
                }

                if (!quiet)
                {
                    AnsiConsole.MarkupLine($"[red]Timed out waiting for process {process.ProcessName}:{process.Id} to exit[/]");
                }

                return -1;
            }
            else
            {
                process.WaitForExit();
                return 0;
            }
        }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
}
