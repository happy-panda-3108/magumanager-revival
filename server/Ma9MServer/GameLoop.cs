using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using NetworkLibrary;

namespace Ma9MServer
{
	// Every packet is handled on this one thread, so handlers and the database never race.
	public sealed class GameLoop
	{
		private readonly BlockingCollection<(GameSession session, Packet.PKS_PACKET packet)> m_queue = new BlockingCollection<(GameSession, Packet.PKS_PACKET)>();

		// Jobs for this thread that are not packets (league game slots); run before the next packet.
		private readonly ConcurrentQueue<Action> m_jobs = new ConcurrentQueue<Action>();

		private readonly Dictionary<(int sup, int sub), Action<GameSession, Packet.PKS_PACKET>> m_handlers = new Dictionary<(int, int), Action<GameSession, Packet.PKS_PACKET>>();

		// Runs after every handled packet of a session (systems session: 업적 boxes, Achievements.Deliver).
		public static event Action<GameSession> AfterPacket;

		public void Register<T>(Action<GameSession, T> handler) where T : Packet.PKS_PACKET, new()
		{
			T sample = new T();
			m_handlers[(sample.GetSUP(), sample.GetSUB())] = (session, packet) => handler(session, (T)packet);
		}

		public void Post(GameSession session, Packet.PKS_PACKET packet)
		{
			m_queue.Add((session, packet));
		}

		// A job on the loop thread (a null packet wakes the loop up).
		public void Post(Action job)
		{
			m_jobs.Enqueue(job);
			m_queue.Add((null, null));
		}

		public void Start()
		{
			Thread thread = new Thread(Run)
			{
				IsBackground = true,
				Name = "GameLoop"
			};
			thread.Start();
		}

		private void Run()
		{
			foreach ((GameSession session, Packet.PKS_PACKET packet) in m_queue.GetConsumingEnumerable())
			{
				while (m_jobs.TryDequeue(out Action job))
				{
					try
					{
						job();
					}
					catch (Exception ex)
					{
						ServerLog.Warn($"job failed: {ex}");
					}
				}
				if (packet == null)
				{
					continue;
				}
				string name = packet.GetType().Name;
				if (!m_handlers.TryGetValue((packet.GetSUP(), packet.GetSUB()), out Action<GameSession, Packet.PKS_PACKET> handler))
				{
					// The client may be waiting for a reply to this; it is the next thing to implement.
					ServerLog.Warn($"[{session.Id}] <- {name} (sup {packet.GetSUP()}, sub {packet.GetSUB()}) NOT HANDLED");
					continue;
				}
				ServerLog.Info($"[{session.Id}] <- {name}");
				try
				{
					handler(session, packet);
					AfterPacket?.Invoke(session);
				}
				catch (Exception ex)
				{
					ServerLog.Warn($"[{session.Id}] {name} failed: {ex}");
				}
			}
		}
	}
}
