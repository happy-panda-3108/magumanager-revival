using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

// Where the private server lives. Netmarble's CDN is gone, so every URL the client builds
// starts from CdnRoot. The game server's TCP address comes from the ConnectionInfo XML that
// the HTTP server generates, so only the HTTP host has to be known here.
//
// Host lookup order:
//   1. <persistentDataPath>/ma9m_server.txt  (lets a phone point at another PC without rebuilding)
//   2. Resources/Ma9MServerHost.txt         (set at build time, e.g. the PC's LAN IP)
//   3. 127.0.0.1                             (PC test builds)
// Either file may list several hosts (separated by ';', ',' or new lines), e.g. the PC's Tailscale
// address (reachable on mobile data while Tailscale is on) and then its LAN address (home Wi-Fi with
// Tailscale off); the first one whose HTTP port answers is used.
public static class Ma9MServerConfig
{
	// Dev (engine session, 2026-10-30): "-ma9mserver host:port" points a PC test build at a second
	// server (the fielding engine's 3D tests run one on a copy of the data, next to the live server).
	public static readonly int HttpPort = ArgPort(18080);

	private static string ArgServer()
	{
		string[] args = Environment.GetCommandLineArgs();
		int i = Array.IndexOf(args, "-ma9mserver");
		return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
	}

	private static int ArgPort(int fallback)
	{
		string s = ArgServer();
		int colon = s != null ? s.LastIndexOf(':') : -1;
		return colon > 0 && int.TryParse(s.Substring(colon + 1), out int p) ? p : fallback;
	}

	private const string DefaultHost = "127.0.0.1";

	private const int ProbeTimeoutMs = 1500;

	private static string s_host;

	public static string Host
	{
		get
		{
			if (s_host == null)
			{
				s_host = LoadHost();
			}
			return s_host;
		}
	}

	public static string CdnRoot => "http://" + Host + ":" + HttpPort + "/ma9m/";

	private static string LoadHost()
	{
		string arg = ArgServer();
		if (arg != null)
		{
			int colon = arg.LastIndexOf(':');
			return colon > 0 ? arg.Substring(0, colon) : arg;
		}
		try
		{
			string path = Path.Combine(Application.persistentDataPath, "ma9m_server.txt");
			if (File.Exists(path))
			{
				string text = File.ReadAllText(path).Trim();
				if (text.Length > 0)
				{
					return Pick(text);
				}
			}
		}
		catch (Exception ex)
		{
			Debug.LogWarning("[Ma9MServerConfig] " + ex.Message);
		}
		TextAsset textAsset = Resources.Load<TextAsset>("Ma9MServerHost");
		if (textAsset != null && textAsset.text.Trim().Length > 0)
		{
			return Pick(textAsset.text);
		}
		return DefaultHost;
	}

	// One host as is; of several, the first that accepts a connection on the HTTP port (or the first
	// one when none does, so the usual "cannot connect" message follows).
	private static string Pick(string list)
	{
		string[] hosts = list.Split(new char[] { ';', ',', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
		if (hosts.Length <= 1)
		{
			return hosts.Length == 1 ? hosts[0] : DefaultHost;
		}
		foreach (string host in hosts)
		{
			if (Answers(host))
			{
				Debug.Log("[Ma9MServerConfig] server " + host);
				return host;
			}
			Debug.Log("[Ma9MServerConfig] no answer from " + host);
		}
		return hosts[0];
	}

	private static bool Answers(string host)
	{
		try
		{
			IPAddress address = Resolve(host);
			if (address == null)
			{
				return false;
			}
			using (TcpClient client = new TcpClient(address.AddressFamily))
			{
				return client.ConnectAsync(address, HttpPort).Wait(ProbeTimeoutMs) && client.Connected;
			}
		}
		catch (Exception ex)
		{
			Debug.Log("[Ma9MServerConfig] " + host + ": " + ex.Message);
			return false;
		}
	}

	// The original code reverse-resolved the server address with Dns.GetHostEntry, which can
	// return an IPv6 address first for a plain IPv4 string and then fail to connect.
	public static IPAddress Resolve(string host)
	{
		if (IPAddress.TryParse(host, out IPAddress address))
		{
			return address;
		}
		IPAddress[] addresses = Dns.GetHostAddresses(host);
		foreach (IPAddress candidate in addresses)
		{
			if (candidate.AddressFamily == AddressFamily.InterNetwork)
			{
				return candidate;
			}
		}
		return addresses.Length > 0 ? addresses[0] : null;
	}
}
