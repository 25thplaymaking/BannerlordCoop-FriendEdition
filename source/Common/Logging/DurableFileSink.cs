using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Common.Logging;

/// <summary>
/// Writes every log event straight to a file on disk, flushing as it goes.
/// </summary>
/// <remarks>
/// The dedicated host had no durable log at all: <see cref="LogManager"/> wrote only to
/// <see cref="OutputSinkManager"/> (an in-memory fan-out for the in-game console) and to a Seq
/// endpoint that does not run on the server. Every server-side fault therefore vanished, which is
/// exactly what made an 87-second campaign-load crash impossible to diagnose.
/// <para>
/// Durability is the whole point, so this deliberately does not buffer. Per <c>CoopMod</c>, the
/// TaleWorlds watchdog behaves as an attached debugger and kills the process before
/// <c>UnhandledException</c> or a Serilog flush can run; an autoflushing writer means the last
/// line before the kill has already reached the OS and survives. That costs throughput and is
/// worth it — a log that loses the final entry loses the only entry that mattered.
/// </para>
/// <para>
/// Nothing here may throw. A logging sink that fails takes down whatever was being logged, and
/// this one is constructed from a static initializer where an exception surfaces as an opaque
/// TypeInitializationException.
/// </para>
/// </remarks>
public sealed class DurableFileSink : ILogEventSink, IDisposable
{
    private const int RetainedFileCount = 10;

    private readonly object gate = new object();
    private readonly StreamWriter writer;

    private DurableFileSink(StreamWriter writer) => this.writer = writer;

    /// <summary>
    /// Opens a new log file for this process, or returns null when no location is writable.
    /// </summary>
    public static DurableFileSink TryCreate(string role)
    {
        try
        {
            string directory = ResolveDirectory();
            if (directory == null) return null;

            Directory.CreateDirectory(directory);
            Prune(directory);

            string name = string.Format(
                CultureInfo.InvariantCulture,
                "coop-{0}-{1:yyyyMMdd-HHmmss}-{2}.log",
                string.IsNullOrWhiteSpace(role) ? "peer" : role,
                DateTime.Now,
                System.Diagnostics.Process.GetCurrentProcess().Id);

            // FileShare.ReadWrite so the file can be tailed while the host is running.
            var stream = new FileStream(
                Path.Combine(directory, name),
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite);
            return new DurableFileSink(new StreamWriter(stream) { AutoFlush = true });
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent == null) return;

        try
        {
            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff} [{1}] {2}",
                logEvent.Timestamp,
                logEvent.Level,
                logEvent.RenderMessage(CultureInfo.InvariantCulture));

            lock (gate)
            {
                writer.WriteLine(line);
                // The exception is the part worth having when the process is about to be killed.
                if (logEvent.Exception != null) writer.WriteLine(logEvent.Exception.ToString());
            }
        }
        catch (Exception)
        {
            // A broken log must never break the caller that was logging.
        }
    }

    public void Dispose()
    {
        try
        {
            lock (gate)
            {
                writer.Flush();
                writer.Dispose();
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Logs live beside the save data the host already owns, which is a writable location on both
    /// roles and, under Wine, resolves inside the server's own prefix.
    /// </summary>
    private static string ResolveDirectory()
    {
        try
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents)) return null;
            return Path.Combine(documents, "Mount and Blade II Bannerlord", "CoopData", "logs");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Prune(string directory)
    {
        try
        {
            List<FileInfo> existing = new DirectoryInfo(directory)
                .GetFiles("coop-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            foreach (FileInfo stale in existing.Skip(RetainedFileCount - 1))
            {
                try { stale.Delete(); } catch (Exception) { }
            }
        }
        catch (Exception)
        {
        }
    }
}
