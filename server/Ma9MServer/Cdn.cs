using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using Microsoft.AspNetCore.Http;

namespace Ma9MServer
{
	// Stands in for Netmarble's CDN (ma9m.img.cdn.netmarble.kr/ma9m/...). Files are matched by
	// name rather than full path, since the client builds paths from versions and platforms.
	public sealed class Cdn
	{
		// Card faces (CardImage) and detail pictures (CardDetailImage) are versioned separately, so
		// redrawing one does not make clients download the other again.
		public const string CardArtVersion = "19";

		public const string CardDetailVersion = "16";  // 16: 8 elite/legend details redrawn from their face illustration (김용수, 송진우, 이종범, 오승환, 17 박세웅, 16 켈리A, 04 홍성흔, 16 헥터; user 2026-11-10: card picture and detail picture differed), the rest byte for byte 15; 15: the 11-02 card set, drawn with the faces into the same version folder (2026-11-02); 9: borrowed looks wear the card's club uniform (2026-10-22: 04 엔젤.P was in a Dodgers uniform)

		private readonly string m_dataDir;

		private readonly int m_gamePort;

		public Cdn(string dataDir, int gamePort)
		{
			m_dataDir = dataDir;
			m_gamePort = gamePort;
		}

		public IResult Handle(HttpContext context, string path)
		{
			string name = Path.GetFileName(path ?? string.Empty);
			byte[] body = Build(context, path, name);
			if (body == null)
			{
				ServerLog.Warn($"cdn 404 {path}");
				return Results.NotFound();
			}
			ServerLog.Info($"cdn {path} ({body.Length} bytes)");
			return Results.Bytes(body, ContentType(name));
		}

		private byte[] Build(HttpContext context, string path, string name)
		{
			if (name == "ConnectionServer.xml")
			{
				return Xml(new ConnectionClientInfo
				{
					ConnectionTypes = new[]
					{
						new ConnectionClientInfo.ConnectionType { OSType = "ANDROID", ServerType = "REAL" },
						new ConnectionClientInfo.ConnectionType { OSType = "IOS", ServerType = "REAL" }
					}
				});
			}
			if (name.StartsWith("ConnectionInfo_") && name.EndsWith(".xml"))
			{
				// Hand back the address the client used to reach us, so phones on the LAN and
				// the PC itself both get a game server address they can connect to.
				string host = context.Request.Host.Host;
				return Xml(new ConnectionInfo
				{
					CDNType = "KR",
					ClientType = "REAL",
					GameServerInfos = new[] { new ConnectionInfo.GameServerInfo { DOMAIN = host, IP = host, PORT = m_gamePort } }
				});
			}
			if (name == "GameServerInfo.xml" || name == "GameServerInfo_QA.xml")
			{
				GameServerInfo.ServerStateDetail detail = new GameServerInfo.ServerStateDetail
				{
					ServerStatus = eServiceStatus.SERVICE,
					ServerCheckMessage = string.Empty,
					CheckStartTime = string.Empty,
					CheckEndTime = string.Empty,
					KakaoPushMent = string.Empty
				};
				GameServerInfo.LoadingImage[] loading = { new GameServerInfo.LoadingImage { Name = "loading01", Weight = 1 } };
				return Xml(new GameServerInfo
				{
					ServerStatus = eServiceStatus.SERVICE,
					ServerCheckMessage = string.Empty,
					CheckStartTime = string.Empty,
					CheckEndTime = string.Empty,
					ServerStatusDetail = new[] { detail, detail },
					MarketUpdateVersions = Array.Empty<GameServerInfo.MarketUpdateVersion>(),
					CheckMenus = Array.Empty<GameServerInfo.CheckMenu>(),
					LoadingImages = loading,
					LoadingImages_MLB = loading
				});
			}
			if (name == "ResourceVersionInfo.xml")
			{
				// The client re-downloads table_bundle.txt whenever TableVersion changes.
				string tableVersion = Hash(BuildTableBundle());
				ResourceInfo info = new ResourceInfo
				{
					LeagueInfo = "1",
					TableVersion = tableVersion,
					// Card art made by tools/scripts/make_card_images.py into KBO/Card(Detail)Image/<version>/;
					// a new version makes clients drop their cached card pictures.
					CardImage = CardArtVersion,
					CardDetailImage = CardDetailVersion,
					ItemImage = "2",  // 2: card packs fill their frame's width (cover over the opening screen's glow, 2026-10-19)
					SoundFile = "1",
					ClubEmblem = "1",
					CoachCardImage = "1",
					CoachCardDetailImage = "1",
					DailyCheck = "1"
				};
				return Xml(new ResourceInfoTypes { ResourceInfos = new[] { info, info } });
			}
			if (name == "table_bundle.txt")
			{
				return MyCryptoGraphy.Encrypt(BuildTableBundle());
			}
			if (name == "clubemblem.txt")
			{
				// ClubEmblemDrawer reads "<label>\t<default emblem count>".
				return Encoding.UTF8.GetBytes("ClubEmblem\t0");
			}
			if (name == "ResourceVersion.txt")
			{
				// Two header lines and no downloadable resources yet. The client reads it as UTF-16.
				return Encoding.Unicode.GetBytes("FileName\tPath\tTarget\tPrevVersion\tPrevSize\tCurVersion\tCurSize\r\n-\r\n");
			}
			string file = Path.Combine(m_dataDir, "cdn", path.Replace('/', Path.DirectorySeparatorChar));
			if (File.Exists(file))
			{
				return File.ReadAllBytes(file);
			}
			// A client reads the card art versions only when it starts, so one that just logged back in
			// after a restart still asks for the old version's folder (deleted once a new one is made):
			// it gets the current picture, not the placeholder (2026-10-20, after faces 10 → 11).
			string current = CurrentArtPath(path);
			if (current != null && File.Exists(Path.Combine(m_dataDir, "cdn", current.Replace('/', Path.DirectorySeparatorChar))))
			{
				return File.ReadAllBytes(Path.Combine(m_dataDir, "cdn", current.Replace('/', Path.DirectorySeparatorChar)));
			}
			// Card art died with the Netmarble CDN. A missing image made the client retry it five
			// times (and log an error each time) whenever the card was shown, which stalled the
			// broadcast; a placeholder portrait gets cached instead (tools/scripts/make_placeholder_art.py).
			if (path.Contains("/CardImage/") || path.Contains("/CoachCardImage/"))
			{
				string ph = Path.Combine(m_dataDir, "cdn", "_placeholder", "card.png");
				return File.Exists(ph) ? File.ReadAllBytes(ph) : null;
			}
			if (path.Contains("/CardDetailImage/") || path.Contains("/CoachCardDetailImage/"))
			{
				string ph = Path.Combine(m_dataDir, "cdn", "_placeholder", "card_detail.png");
				return File.Exists(ph) ? File.ReadAllBytes(ph) : null;
			}
			return null;
		}

		// ".../CardImage/<old>/<idx>.png" with the current version instead (null when it already is current).
		private static string CurrentArtPath(string path)
		{
			string[] parts = path.Split('/');
			for (int i = 0; i + 2 < parts.Length; i++)
			{
				string version = parts[i] == "CardImage" ? CardArtVersion : parts[i] == "CardDetailImage" ? CardDetailVersion : null;
				if (version != null && parts[i + 1] != version)
				{
					parts[i + 1] = version;
					return string.Join("/", parts);
				}
			}
			return null;
		}

		// Plain layout: [int32 name length][UTF-8 name][int32 body length][UTF-8 body], repeated,
		// one entry per data/tables/*.txt. RC2 is applied by the caller.
		private byte[] BuildTableBundle()
		{
			string dir = Path.Combine(m_dataDir, "tables");
			Directory.CreateDirectory(dir);
			List<string> files = new List<string>(Directory.GetFiles(dir, "*.txt"));
			files.Sort(StringComparer.Ordinal);
			using MemoryStream stream = new MemoryStream();
			using BinaryWriter writer = new BinaryWriter(stream);
			foreach (string file in files)
			{
				byte[] fileName = Encoding.UTF8.GetBytes(Path.GetFileName(file));
				string text = File.ReadAllText(file, Encoding.UTF8);
				string name = Path.GetFileName(file);
				if (name == "P_BatterTable.txt" || name == "P_PItcherTable.txt")
				{
					text = ClientHands(text);
				}
				else if (name == "ManagementLeagueSchedule.txt")
				{
					// The schedule as of now: the 16-game rows from LeagueRules.SixteenFrom (engine session, 2026-10-31).
					text = LeagueRules.ScheduleTable(DateTime.Now);
				}
				byte[] content = Encoding.UTF8.GetBytes(text);
				writer.Write(fileName.Length);
				writer.Write(fileName);
				writer.Write(content.Length);
				writer.Write(content);
			}
			writer.Flush();
			return stream.ToArray();
		}

		// The card tables store hands as 1 right, 2 left, 3 switch (the server and the engine read
		// them so). The client reads 0 left, 1 right, 2 both (PlayerInfo "좌/우/양", the broadcast's
		// "== 1 is right", the roster analysis's "2 is switch"), so its copy is converted: without
		// this every left-hander showed as 양투/양타.
		private static string ClientHands(string table)
		{
			string[] lines = table.Split('\n');
			for (int i = 1; i < lines.Length; i++)
			{
				string[] c = lines[i].Split('\t');
				if (c.Length < 6)
				{
					continue;
				}
				c[4] = ToClientHand(c[4]);
				c[5] = ToClientHand(c[5].TrimEnd('\r')) + (c[5].EndsWith("\r") ? "\r" : string.Empty);
				lines[i] = string.Join("\t", c);
			}
			return string.Join("\n", lines);
		}

		private static string ToClientHand(string value)
		{
			return value switch
			{
				"1" => "1",
				"2" => "0",
				"3" => "2",
				_ => value
			};
		}

		private static byte[] Xml<T>(T document)
		{
			using MemoryStream stream = new MemoryStream();
			new XmlSerializer(typeof(T)).Serialize(stream, document);
			return stream.ToArray();
		}

		private static string Hash(byte[] data)
		{
			return Convert.ToHexString(SHA256.HashData(data)).Substring(0, 16);
		}

		private static string ContentType(string name)
		{
			return Path.GetExtension(name).ToLowerInvariant() switch
			{
				".xml" => "application/xml",
				".png" => "image/png",
				_ => "application/octet-stream"
			};
		}
	}
}
