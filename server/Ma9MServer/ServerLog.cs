using System;

namespace Ma9MServer
{
	public static class ServerLog
	{
		private static readonly object s_lock = new object();

		public static void Info(string message)
		{
			Write("INFO", message);
		}

		public static void Warn(string message)
		{
			Write("WARN", message);
		}

		private static void Write(string level, string message)
		{
			lock (s_lock)
			{
				Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level} {message}");
			}
		}
	}
}
