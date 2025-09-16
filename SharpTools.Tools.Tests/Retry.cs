using System.Diagnostics;

namespace SharpTools.Tools.Tests;

[DebuggerStepThrough]
public static class Retry
{
	public static async Task Until(int timeLimit, Func<Task<bool>> assertion)
	{
		var cancellationToken = TestContext.Current.CancellationToken;

		using var timeoutWatcher = CancellationTokenUtils.ApplyTimeout(
			timeLimit,
			ref cancellationToken
		);

		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var result = await assertion();

				if (result)
					return;

				await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
			}
		}
		catch (TaskCanceledException)
		{
			if (!timeoutWatcher.TimedOut)
				throw;
		}

		await assertion();
	}

	public static async Task UntilPasses(Action condition)
	{
		await UntilPasses(5, condition);
	}

	public static async Task UntilPasses(Func<Task> condition)
	{
		await UntilPasses(5, condition);
	}

	public static async Task UntilPasses(int timeLimit, Func<Task> condition)
	{
		await Until(
			timeLimit,
			async () =>
			{
				try
				{
					await condition();
					return true;
				}
				catch
				{
					return false;
				}
			}
		);

		await condition();
	}

	public static async Task UntilPasses(int timeLimit, Action condition)
	{
		await UntilPasses(
			timeLimit,
			() =>
			{
				condition();
				return Task.CompletedTask;
			}
		);
	}
}
