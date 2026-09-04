namespace WeaponPaints;

internal sealed class FailureLogLimiter(TimeSpan interval)
{
	private readonly long _intervalMilliseconds = Math.Max(1, (long)interval.TotalMilliseconds);
	private long _nextLogAt;
	private int _suppressed;

	internal bool ShouldLog(out int suppressed)
	{
		long now = Environment.TickCount64;
		while (true)
		{
			long next = Volatile.Read(ref _nextLogAt);
			if (now < next)
			{
				Interlocked.Increment(ref _suppressed);
				suppressed = 0;
				return false;
			}

			if (Interlocked.CompareExchange(ref _nextLogAt, now + _intervalMilliseconds, next) != next)
				continue;

			suppressed = Interlocked.Exchange(ref _suppressed, 0);
			return true;
		}
	}
}
