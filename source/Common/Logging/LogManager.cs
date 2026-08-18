using Common.Logging.Enrichers;
using Serilog;
using System;

namespace Common.Logging;

public static class LogManager
{
	public static LoggerConfiguration Configuration { get; set; } = new LoggerConfiguration();
	
	// If this is called before the Configuration is setup, logging does not work
	private static Lazy<ILogger> _logger = new Lazy<ILogger>(CreateLogger);

	/// <summary>
	/// Builds the logger. Seq on its own was not enough: it does not run on the dedicated host, so
	/// every server-side fault was written to a socket nobody was listening on and lost. The
	/// durable file sink is added first so the host always keeps a readable record.
	/// </summary>
	private static ILogger CreateLogger()
	{
		LoggerConfiguration configuration = Configuration
			.Enrich.With(new NetworkEnricher())
			.Enrich.With(new StackTraceEnricher())
			.WriteTo.Sink(new OutputSinkManager());

		// ModInformation.IsServer is set by the co-op start flow, which runs long after the first
		// GetLogger call, so it still reads false on a dedicated host here. Fall back to the launch
		// arguments, which already say what this process is.
		DurableFileSink file = DurableFileSink.TryCreate(IsDedicatedHost() ? "server" : "client");
		if (file != null) configuration = configuration.WriteTo.Sink(file);

		// Opt out where no Seq server exists. On the dedicated host this sink retried
		// localhost:5341 continuously, and those connection failures buried real faults in the
		// first-chance diagnostics.
		if (!string.Equals(
				Environment.GetEnvironmentVariable("COOP_DISABLE_SEQ"), "1", StringComparison.Ordinal))
			configuration = configuration.WriteTo.Seq("http://localhost:5341");

		return configuration.CreateLogger();
	}

	private static bool IsDedicatedHost()
	{
		if (ModInformation.IsServer) return true;
		try
		{
			return Environment.CommandLine.IndexOf("/dedicated", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static ILogger GetLogger<T>() => _logger.Value
		.ForContext<T>();

	// For static classes, which cannot be used as the generic type argument above.
	public static ILogger GetLogger(Type type) => _logger.Value
		.ForContext(type);

}
