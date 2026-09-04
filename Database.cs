using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace WeaponPaints;

public sealed class Database(
	string connectionString,
	ILogger logger,
	CancellationToken lifetimeToken,
	int maxOpenAttempts)
{
	private readonly FailureLogLimiter _openFailureLogs = new(TimeSpan.FromSeconds(10));
	internal bool IsStopping => lifetimeToken.IsCancellationRequested;
	internal CancellationToken StoppingToken => lifetimeToken;

	public async Task<MySqlConnection> GetConnectionAsync()
	{
		Exception? lastException = null;
		for (int attempt = 1; attempt <= maxOpenAttempts; attempt++)
		{
			lifetimeToken.ThrowIfCancellationRequested();
			var connection = new MySqlConnection(connectionString);
			try
			{
				await connection.OpenAsync(lifetimeToken).ConfigureAwait(false);
				return connection;
			}
			catch (Exception exception) when (attempt < maxOpenAttempts && IsTransient(exception))
			{
				lastException = exception;
				await connection.DisposeAsync().ConfigureAwait(false);
				TimeSpan delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1));
				if (_openFailureLogs.ShouldLog(out int suppressed))
					logger.LogWarning(exception,
						"[WeaponPaints] Transient MySQL open failure on attempt {Attempt}; retrying in {DelayMs:F0} ms. Suppressed {SuppressedCount} similar warnings.",
						attempt, delay.TotalMilliseconds, suppressed);
				await Task.Delay(delay, lifetimeToken).ConfigureAwait(false);
			}
			catch
			{
				await connection.DisposeAsync().ConfigureAwait(false);
				throw;
			}
		}

		throw new InvalidOperationException("Unable to open a WeaponPaints database connection.", lastException);
	}

	private static bool IsTransient(Exception exception) =>
		exception is TimeoutException or MySqlException { IsTransient: true };
}
