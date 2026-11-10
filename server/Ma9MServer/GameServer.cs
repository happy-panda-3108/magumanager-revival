using System;
using System.Net.Sockets;
using System.Threading;
using Ma9MPackets;
using NetworkLibrary;
using NetworkLibrary.Server;

namespace Ma9MServer
{
	// TCP front end. The accept loop and the per-connection framing/AES handshake come from the
	// shared NetworkLibrary, so they behave exactly like the original server.
	public sealed class GameServer : Server
	{
		private readonly GameLoop m_loop;

		public GameServer(GameLoop loop)
			: base("Ma9MGame", new Ma9MPacket())
		{
			m_loop = loop;
		}

		public override Client InstantiateClient(Socket socket)
		{
			return new GameSession(socket, m_loop);
		}
	}

	public sealed class GameSession : Client
	{
		private static int s_nextId;

		private readonly GameLoop m_loop;

		public readonly int Id = Interlocked.Increment(ref s_nextId);

		// Set by the login handlers; only touched on the game loop thread.
		public AccountRow Account;

		public TeamRow Team;

		public Guid AccessToken;

		// The login's bundle_version: the original "2.0.81%…", and Ma9M builds add what they support (e.g. "%ma9m-pos1",
		// PositionChangeHandlers.ClientSupports).
		public string ClientVersion = string.Empty;

		public GameSession(Socket socket, GameLoop loop)
			: base(new Ma9MPacket())
		{
			m_loop = loop;
			// Built here rather than by the base constructor so the handshake callback is in place
			// before the first byte can arrive.
			m_connection = new Connection(socket, PacketUtil, OnDisconnected, false, true, true, this);
			m_connection.SetInitializedCallBack(OnCryptoReady);
			m_connection.Listen();
			ServerLog.Info($"[{Id}] connected from {GetEndPointIP()}");
		}

		private void OnCryptoReady()
		{
			// The client waits for this before it sends its login packet.
			Send(new Ma9MPacket.PKS_SC_INITIALIZED());
		}

		public override void InsertPacket(Packet.PKS_PACKET packet)
		{
			m_loop.Post(this, packet);
		}

		// False once the connection drops (set on the network thread; TopBar.PushLeague skips such sessions).
		public volatile bool Connected = true;

		// The results screen's scoreboard: its CONFIRM pays the game, and the app adds that MP to its top bar at the stamp,
		// after the score count-up. A top bar sent in between (업적 MP, the league push) already holds it, so it showed twice
		// (AI tester 11-09: +24,000). Set by LeagueHandlers.Confirm; Achievements.Deliver waits for the next packet, and
		// TopBar.PushLeague for ConfirmQuiet (loop thread only).
		public bool ConfirmedLast;

		public DateTime ConfirmedAt;

		public override void OnDisconnected()
		{
			Connected = false;
			ServerLog.Info($"[{Id}] disconnected");
		}

		public void Send(Packet.PKS_PACKET packet)
		{
			ServerLog.Info($"[{Id}] -> {packet.GetType().Name}");
			m_connection.SendPacket(packet);
		}
	}
}
