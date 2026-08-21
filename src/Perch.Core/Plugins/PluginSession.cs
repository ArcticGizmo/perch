namespace Perch.Plugins;

using System.Diagnostics;

/// <summary>
/// Drives a single one-shot request/response conversation with a plugin process: write the request line,
/// close stdin, read the plugin's output lines until it exits, and enforce a hard timeout (kill on
/// overrun). Malformed output lines are skipped, not fatal. Kept transport-agnostic (an
/// <see cref="IPluginProcess"/>, not a raw <see cref="Process"/>) so it is unit-testable against
/// in-memory streams.
/// </summary>
internal static class PluginSession
{
    /// <summary>Runs one request against an already-launched process and returns everything it emitted.
    /// Never throws for plugin misbehaviour — a timeout, a crash, or garbage output all come back as a
    /// populated <see cref="PluginRunResult"/> with the relevant flag set.</summary>
    public static async Task<PluginRunResult> RunOnceAsync(
        IPluginProcess proc, PluginRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        var messages = new List<PluginMessage>();
        bool timedOut = false;
        int exitCode = 0;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            // Send the request, then close stdin so a one-shot plugin sees EOF and can finish.
            try
            {
                await proc.StandardInput.WriteLineAsync(PluginProtocol.Serialize(request).AsMemory(), timeoutCts.Token);
                await proc.StandardInput.FlushAsync(timeoutCts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* plugin closed stdin early — fine */ }
            finally { try { proc.StandardInput.Close(); } catch { } }

            // Drain stdout to EOF (the plugin exiting closes it).
            string? line;
            while ((line = await ReadLineAsync(proc.StandardOutput, timeoutCts.Token)) != null)
            {
                var msg = PluginProtocol.ParseLine(line);
                if (msg != null) messages.Add(msg);
            }

            exitCode = await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            timedOut = true;
            proc.Kill();
        }
        catch
        {
            // Any other failure (broken pipe, process vanished) → treat as a fault with whatever we got.
            proc.Kill();
        }

        return new PluginRunResult(messages, exitCode, timedOut);
    }

    // TextReader.ReadLineAsync doesn't take a CancellationToken on all targets; wrap it so the timeout can
    // interrupt a plugin that opens stdout but never writes or closes it.
    private static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken ct)
    {
        var readTask = reader.ReadLineAsync();
        var completed = await Task.WhenAny(readTask, Task.Delay(Timeout.Infinite, ct));
        if (completed != readTask) throw new OperationCanceledException(ct);
        return await readTask;
    }
}

/// <summary>Everything a one-shot plugin run produced: the messages it emitted, its exit code, and whether
/// it was killed for overrunning the timeout.</summary>
internal sealed record PluginRunResult(
    IReadOnlyList<PluginMessage> Messages,
    int ExitCode,
    bool TimedOut);
