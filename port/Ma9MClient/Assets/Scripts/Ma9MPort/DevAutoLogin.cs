using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

// Dev-only: "-ma9mautologin <email> <password>" types the credentials, presses Login, and on the
// team screen picks the first team or creates one, so the boot → lobby path can be tested unattended.
// "-ma9mtour Scene_A,Scene_B" then visits those scenes from the lobby, saving a screenshot of each
// into the "-ma9mcapture" directory, and quits — a quick way to see what each screen asks the server for.
// "-ma9mexhibition sns|video" plays one exhibition game from the lobby (try, start, watch the text or
// 3D broadcast), screenshotting the opponent and the broadcast every few seconds, then quits.
public class DevAutoLogin : MonoBehaviour
{
	private string m_email;

	private string m_password;

	private string[] m_tour;

	private bool m_touring;

	private string m_exhibition;

	// "-ma9mhighlight <game>": from the lobby, the highlights of that league game (engine session).
	private int m_highlight;

	// "-ma9mboard <game>": from the lobby, the user's league game's board (as 일정 및 결과 shows it), a
	// screenshot once its score has run up, then its 하이라이트 button (engine session).
	private int m_board;

	private bool m_boardShown;

	// "-ma9mhustle [n]": from the lobby, 일정 및 결과's 허슬 플레이 page (우리팀, then 리그 전체), 연속 재생 of n clips
	// (default 3) from the league list, and the page again after them (engine session).
	private int m_hustle = -1;

	private static bool s_stalled;

	private int m_hustleVisits;

	private bool m_exhibitionStarted;

	// "-ma9mintro": on a new account's team creation, pick the first east club and the first
	// batter and pitcher candidates, screenshot each step, and quit before creating the team.
	// "-ma9mintro NEXEN_HEROES,SK_WYVERNS,HYUNDAI_UNICORNS" (eTeamName names): show each of those clubs
	// in turn (a screenshot each), and go on with the last.
	private bool m_intro;

	private string[] m_introClubs;

	private string m_captureDir;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	private static void Init()
	{
		string[] args = Environment.GetCommandLineArgs();
		int i = Array.IndexOf(args, "-ma9mautologin");
		if (i < 0 || i + 2 >= args.Length)
		{
			return;
		}
		GameObject go = new GameObject("DevAutoLogin");
		DontDestroyOnLoad(go);
		DevAutoLogin auto = go.AddComponent<DevAutoLogin>();
		auto.m_email = args[i + 1];
		auto.m_password = args[i + 2];
		int t = Array.IndexOf(args, "-ma9mtour");
		if (t >= 0 && t + 1 < args.Length)
		{
			auto.m_tour = args[t + 1].Split(',');
		}
		int e = Array.IndexOf(args, "-ma9mexhibition");
		if (e >= 0)
		{
			auto.m_exhibition = e + 1 < args.Length && !args[e + 1].StartsWith("-") ? args[e + 1] : "sns";
		}
		if (Array.IndexOf(args, "-ma9mleaguelive") >= 0)
		{
			auto.m_highlight = -1;   // the lobby's 중계 button (the league game on air, or the last one)
			auto.m_exhibition = "video";
		}
		int hl = Array.IndexOf(args, "-ma9mhighlight");
		if (hl >= 0 && hl + 1 < args.Length && int.TryParse(args[hl + 1].Split(':')[0], out int hg))
		{
			auto.m_highlight = hg;
			auto.m_exhibition = "video";   // shoots the broadcast like an exhibition
			// "<game>:<scene>" plays that one scene of the game, whatever its grade (Common_TopMenu).
			string[] gs = args[hl + 1].Split(':');
			if (gs.Length > 1 && int.TryParse(gs[1], out int hs)) Ma9MHustle.DevScene = hs;
		}
		int bd = Array.IndexOf(args, "-ma9mboard");
		if (bd >= 0 && bd + 1 < args.Length && int.TryParse(args[bd + 1], out int bg))
		{
			auto.m_board = bg;
			auto.m_exhibition = "video";
		}
		int hu = Array.IndexOf(args, "-ma9mhustle");
		if (hu >= 0)
		{
			auto.m_hustle = hu + 1 < args.Length && int.TryParse(args[hu + 1], out int hn) ? hn : 3;
			auto.m_exhibition = "video";
		}
		int c = Array.IndexOf(args, "-ma9mcapture");
		auto.m_captureDir = (c >= 0 && c + 1 < args.Length) ? args[c + 1] : Application.persistentDataPath;
		int ic = Array.IndexOf(args, "-ma9mintro");
		auto.m_intro = ic >= 0;
		auto.m_introClubs = (ic >= 0 && ic + 1 < args.Length && !args[ic + 1].StartsWith("-")) ? args[ic + 1].Split(',') : null;
	}

	private void Awake()
	{
		SceneManager.sceneLoaded += OnSceneLoaded;
	}

	private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		if (scene.name == "Scene_Login")
		{
			StartCoroutine(Login());
		}
		else if (scene.name == "Scene_ServerSelect")
		{
			StartCoroutine(PickTeam());
		}
		else if (scene.name == "Scene_MainLobby" && m_tour != null && !m_touring)
		{
			m_touring = true;
			StartCoroutine(Tour());
		}
		else if (scene.name == "Scene_MainLobby" && m_highlight != 0 && !m_exhibitionStarted)
		{
			m_exhibitionStarted = true;
			StartCoroutine(GoHighlight());
		}
		else if (scene.name == "Scene_MainLobby" && m_board != 0 && !m_exhibitionStarted)
		{
			m_exhibitionStarted = true;
			StartCoroutine(GoBoard());
		}
		else if (scene.name == "Scene_ScheduleAndResult" && m_board != 0 && !m_boardShown)
		{
			StartCoroutine(BoardFromResults());
		}
		else if (scene.name == "Scene_MainLobby" && m_hustle >= 0 && !m_exhibitionStarted)
		{
			m_exhibitionStarted = true;
			StartCoroutine(GoHustle());
		}
		else if (scene.name == "Scene_ScheduleAndResult" && m_hustle >= 0)
		{
			StartCoroutine(HustlePage(m_hustleVisits++));
		}
		else if (scene.name == "Scene_MainLobby" && m_exhibition != null && !m_exhibitionStarted)
		{
			m_exhibitionStarted = true;
			StartCoroutine(GoExhibition());
		}
		else if (scene.name == "Scene_ExhibitionGame" && m_exhibition != null)
		{
			StartCoroutine(PlayExhibition());
		}
		else if (scene.name == "Scene_IntroTutorial" && m_intro)
		{
			StartCoroutine(Intro());
		}
		else if (scene.name == "Scene_Unconfirmed")
		{
			StartCoroutine(ConfirmResults());
		}
		else if ((scene.name == "Scene_SNSBroadcast" || scene.name == "Scene_BroadCast") && m_exhibition != null)
		{
			StartCoroutine(WatchBroadcast(scene.name));
		}
	}

	private IEnumerator Tour()
	{
		string[] args = Environment.GetCommandLineArgs();
		int c = Array.IndexOf(args, "-ma9mcapture");
		string dir = (c >= 0 && c + 1 < args.Length) ? args[c + 1] : Application.persistentDataPath;
		System.IO.Directory.CreateDirectory(dir);
		yield return new WaitForSeconds(6f);
		ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "tour_00_Scene_MainLobby.png"));
		for (int n = 0; n < m_tour.Length; n++)
		{
			yield return new WaitForSeconds(1f);
			string step = m_tour[n];
			Debug.Log("[DevAutoLogin] tour -> " + step);
			string shot = $"tour_{n + 1:00}_{step.Replace('@', 'a').Replace('=', '_').Replace(':', '_')}";
			if (step.StartsWith("@"))
			{
				// Actions on the current screen: "@card" opens the first card's player window,
				// "@coupon=CODE" uses a coupon, "@pack=ITEM:N" opens N packs, "@wait=S" waits.
				string[] kv = step.Substring(1).Split('=');
				string arg = kv.Length > 1 ? kv[1] : string.Empty;
				switch (kv[0])
				{
				case "card":
				{
					// "@card=IDX" opens that card of the team; "@card" clicks the first card on screen
					// (the same path as a real click: PlayerCardSlot.doClick → PlayerInfoManager).
					if (int.TryParse(arg, out int own))
					{
						PlayerInfoManager.popupPlayerInfo(null, own, eRosterType.ROSTER_COMMON);
						yield return new WaitForSeconds(4f);
						break;
					}
					PlayerCardSlot slot = Array.Find(FindObjectsByType<PlayerCardSlot>(FindObjectsSortMode.None), s => s.GetCardIndex() > 0);
					Debug.Log("[DevAutoLogin] card slot " + (slot != null ? slot.GetCardIndex().ToString() : "none"));
					if (slot != null)
					{
						slot.SendMessage("doClick");
					}
					yield return new WaitForSeconds(4f);
					break;
				}
				case "coupon":
				{
					Ma9MPackets.Ma9MPacket.PKS_CS_USE_COUPON coupon = new Ma9MPackets.Ma9MPacket.PKS_CS_USE_COUPON();
					coupon.coupon_code.str = arg;
					NetworkConnector.SendPacket(coupon);
					yield return new WaitForSeconds(3f);
					break;
				}
				case "pack":
				{
					string[] p = arg.Split(':');
					Inventory_Main inventory = FindAnyObjectByType<Inventory_Main>();
					if (inventory != null)
					{
						inventory.OnBtn_InvenItem(0);
					}
					Ma9MPackets.Ma9MPacket.PKS_CS_USE_CARDPACK_ITEM use = new Ma9MPackets.Ma9MPacket.PKS_CS_USE_CARDPACK_ITEM();
					use.index.n = int.Parse(p[0]);
					use.amount.n = byte.Parse(p.Length > 1 ? p[1] : "1");
					NetworkConnector.SendPacket(use);
					for (int k = 0; k < 4; k++)
					{
						yield return new WaitForSeconds(1.5f);
						ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, $"{shot}_{k}.png"));
					}
					break;
				}
				case "wait":
					yield return new WaitForSeconds(float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
					break;
				case "consult":
				{
					// "@consult=N" (engine, 2026-11-04): the open 면담 dialogue's choice N, as a tap on it and on the text.
					CoachingStaff_ConsultRoom room = FindAnyObjectByType<CoachingStaff_ConsultRoom>();
					Debug.Log("[DevAutoLogin] consult " + arg + (room != null ? string.Empty : ": no 면담실"));
					if (room != null)
					{
						typeof(CoachingStaff_ConsultRoom).GetField("SelectedTextIdx", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(room, int.Parse(arg));
						room.OnBtn_TextChoose();
					}
					yield return new WaitForSeconds(3f);
					break;
				}
				case "train":
				{
					// "@train=BOOK:SLOT" (engine, 2026-11-04): the open player window's training tab trains with that book
					// as if it had been dragged into top slot SLOT and 트레이닝 confirmed (coach training checks).
					string[] tb = arg.Split(':');
					PlayerInfo_SubWindow_Training training = Array.Find(FindObjectsByType<PlayerInfo_SubWindow_Training>(FindObjectsInactive.Include, FindObjectsSortMode.None), t => t != null);
					Debug.Log("[DevAutoLogin] train " + arg + " on " + (training != null ? PlayerInfo_DataCenter.playerIndex.ToString() : "no training tab"));
					if (training != null)
					{
						typeof(PlayerInfo_SubWindow_Training).GetField("curTrainingCardIndex", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(training, int.Parse(tb[0]));
						typeof(PlayerInfo_SubWindow_Training).GetField("curSlotIndex", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(training, tb.Length > 1 ? int.Parse(tb[1]) : 0);
						typeof(PlayerInfo_SubWindow_Training).GetMethod("sendPK_CS_TRY_CARD_TRAINING", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(training, null);
					}
					yield return new WaitForSeconds(3f);
					break;
				}
				case "detail":
				{
					// The open player window's 상세보기 (detailed season record).
					PlayerInfo_SubWindow_Record record = Array.Find(FindObjectsByType<PlayerInfo_SubWindow_Record>(FindObjectsInactive.Include, FindObjectsSortMode.None), r => r != null);
					if (record != null)
					{
						record.SendMessage("OnBtn_Detail");
					}
					yield return new WaitForSeconds(2f);
					break;
				}
				case "call":
					// "@call=Type.Method" or "@call=Type.Method:arg": a screen's handler by name, as its button would
					// call it (2026-10-24).
					Ma9MCall(arg);
					yield return new WaitForSeconds(2.5f);
					break;
				case "sort":
				case "sortclear":
				{
					// "@sort=Ball" (an eSortType name): long-presses that button of the screen's filter bar;
					// "@sort=Position/RosterChange" picks the bar whose popup type has that in its name.
					// "@sortclear=…" short-presses it instead, which clears that filter.
					string[] sa = arg.Split('/');
					FieldInfo kindField = typeof(SortPopup).GetField("popupType", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
					SortPopup sort = Array.Find(FindObjectsByType<SortPopup>(FindObjectsSortMode.None), s => sa.Length < 2 || kindField.GetValue(s).ToString().Contains(sa[1]));
					string kind = (sort != null) ? kindField.GetValue(sort).ToString() : string.Empty;
					// The bar's types as OnLongPress_Btn_Sort reads them (선수교체 uses the Roster list too).
					string list = kind.StartsWith("Normal") ? "SortType_Player_Normal" : kind.StartsWith("Roster") ? "SortType_Player_Roster" : kind.StartsWith("Training") ? "SortType_TrainingCardMix" : "SortType_CoachCardMix";
					FieldInfo types = typeof(SortPopup).GetField(list, BindingFlags.Instance | BindingFlags.NonPublic);
					int at = (sort != null) ? Array.IndexOf((SortPopup.eSortType[])types.GetValue(sort), (SortPopup.eSortType)Enum.Parse(typeof(SortPopup.eSortType), sa[0])) : -1;
					Debug.Log("[DevAutoLogin] " + kv[0] + " " + arg + " at " + at + " on " + kind);
					if (at >= 0 && kv[0] == "sort")
					{
						typeof(SortPopup).GetMethod("OnLongPress_Btn_Sort", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[1] { typeof(int) }, null).Invoke(sort, new object[1] { at });
					}
					else if (at >= 0)
					{
						((bool[])typeof(SortPopup).GetField("isLongPressBtn", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sort))[at] = false;
						typeof(SortPopup).GetMethod("OnBtn_MainSort", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[1] { typeof(int) }, null).Invoke(sort, new object[1] { at });
					}
					yield return new WaitForSeconds(1.5f);
					break;
				}
				case "pick":
				{
					// "@pick=label": the open filter popup's entry with that label; "{Key}" is a localized word
					// (e.g. "{Position_DH} 제외").
					string label = System.Text.RegularExpressions.Regex.Replace(arg, "\\{(\\w+)\\}", m => Localization.instance.Get(m.Groups[1].Value));
					SortPopupPanel panel = FindAnyObjectByType<SortPopupPanel>();
					string[] names = (panel != null) ? ((string[])typeof(SortPopupPanel).GetField("cur_Sub_Names", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel)) : new string[0];
					int at = Array.IndexOf(names, label);
					Debug.Log("[DevAutoLogin] pick " + label + " at " + at + " of " + string.Join("|", names));
					if (at >= 0)
					{
						panel.OnBtn_SubSort(at);
					}
					yield return new WaitForSeconds(1f);
					break;
				}
				case "rosterchange":
				{
					// "@rosterchange=batter" or "=pitcher": 선수교체 on, then the first such card of the map clicked (its
					// change list, e.g. cards in a 특별수강 drawn as IN_LESSON).
					Roster_Main main = FindAnyObjectByType<Roster_Main>();
					if (main != null)
					{
						main.btn_ToggleChange.isChecked = true;
					}
					yield return new WaitForSeconds(1.5f);
					bool batter = arg != "pitcher";
					Roster_Slot slot = Array.Find(FindObjectsByType<Roster_Slot>(FindObjectsSortMode.None), s => s.SlotType == Roster_Slot.eSlotType.RosterMapSlot && s.m_CardData != null && s.m_CardData.card_info is TableReader.TRRow_BatterCard == batter);
					Debug.Log("[DevAutoLogin] rosterchange " + arg + " mode " + Roster_DataCenter.RosterMapMode + " slot " + (slot != null ? slot.name : "none"));
					if (slot != null)
					{
						slot.SendMessage("DoClick");
					}
					yield return new WaitForSeconds(2.5f);
					break;
				}
				case "dblcard":
				{
					// "@dblcard" or "@dblcard=IDX": double-clicks the first card on screen (or that card) that has a
					// double-click use (e.g. 포지션 변경's list → PositionChange_Main.OnDoubleClick_PlayerCard).
					FieldInfo dblTarget = typeof(PlayerCardSlot).GetField("doubleClickTarget", BindingFlags.Instance | BindingFlags.NonPublic);
					int dblIdx = int.TryParse(arg, out int di) ? di : 0;
					PlayerCardSlot[] dblAll = Array.FindAll(FindObjectsByType<PlayerCardSlot>(FindObjectsSortMode.None), s => s.GetCardIndex() > 0 && dblTarget.GetValue(s) != null);
					PlayerCardSlot dbl = Array.Find(dblAll, s => dblIdx == 0 || s.GetCardIndex() == dblIdx) ?? (dblAll.Length > 0 ? dblAll[0] : null);
					Debug.Log("[DevAutoLogin] dblcard " + (dbl != null ? dbl.GetCardIndex().ToString() : "none"));
					if (dbl != null)
					{
						dbl.SendMessage("doDoubleClick");
					}
					yield return new WaitForSeconds(1.5f);
					break;
				}
				case "poscheck":
				{
					// "@poscheck": clicks the first role box the selected card may change to (포지션 변경).
					PositionChange_Main pm = FindAnyObjectByType<PositionChange_Main>();
					int box = (pm != null) ? Array.FindIndex(pm.checkbox, b => b != null && b.GetComponent<BoxCollider>().enabled) : -1;
					Debug.Log("[DevAutoLogin] poscheck " + box);
					if (box >= 0)
					{
						pm.checkbox[box].SendMessage("OnClick");
					}
					yield return new WaitForSeconds(1f);
					break;
				}
				case "recipes":
				{
					// "@recipes=N": N made-up 선수조합 recipes saved for this team; "@recipes=0" clears them.
					int count = int.Parse(arg);
					if (count == 0)
					{
						Ma9MCombineRecipes.Clear();
					}
					string[] supports = new string[4] { "7,4,2,2,1,1,1", "7,7,4,2,2", "5,4", "" };
					for (int k = 0; k < count; k++)
					{
						Ma9MCombineRecipes.Save(Ma9MCombineRecipes.Recipe.Load(k + "," + (7 - k % 3) + "," + (1 + k % 3) + "," + (k % 2) + ";" + supports[k % 4]));
					}
					Debug.Log("[DevAutoLogin] recipes saved " + Ma9MCombineRecipes.Saved().Count);
					yield return new WaitForSeconds(0.5f);
					break;
				}
				}
				ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, shot + ".png"));
				continue;
			}
			SceneChanger.LoadLevel(step);
			yield return new WaitForSeconds(8f);
			ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, shot + ".png"));
		}
		yield return new WaitForSeconds(2f);
		Application.Quit();
	}

	// "@call": Type.Method[:arg] by reflection, on the first live object of that type (or static); the argument is
	// parsed to the method's one parameter.
	private static void Ma9MCall(string spec)
	{
		int colon = spec.IndexOf(':');
		string path = (colon >= 0) ? spec.Substring(0, colon) : spec;
		string value = (colon >= 0) ? spec.Substring(colon + 1) : null;
		int dot = path.LastIndexOf('.');
		Type type = typeof(DevAutoLogin).Assembly.GetType(path.Substring(0, dot));
		string name = path.Substring(dot + 1);
		MethodInfo method = (type == null) ? null : Array.Find(type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic), m => m.Name == name && m.GetParameters().Length == ((value == null) ? 0 : 1));
		if (method == null)
		{
			Debug.Log("[DevAutoLogin] call " + spec + ": no such method");
			return;
		}
		UnityEngine.Object target = null;
		if (!method.IsStatic)
		{
			UnityEngine.Object[] found = FindObjectsByType(type, FindObjectsSortMode.None);
			if (found.Length == 0)
			{
				Debug.Log("[DevAutoLogin] call " + spec + ": no " + type.Name + " on screen");
				return;
			}
			target = found[0];
		}
		object[] args = null;
		if (value != null)
		{
			Type p = method.GetParameters()[0].ParameterType;
			args = new object[1] { p.IsEnum ? Enum.Parse(p, value) : Convert.ChangeType(value, p, System.Globalization.CultureInfo.InvariantCulture) };
		}
		try
		{
			method.Invoke(target, args);
			Debug.Log("[DevAutoLogin] call " + spec);
		}
		catch (Exception e)
		{
			Debug.Log("[DevAutoLogin] call " + spec + " failed: " + (e.InnerException ?? e));
		}
	}

	private void Capture(string name)
	{
		System.IO.Directory.CreateDirectory(m_captureDir);
		ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(m_captureDir, name + ".png"));
		Debug.Log("[DevAutoLogin] capture " + name);
	}

	private IEnumerator GoHustle()
	{
		yield return new WaitForSeconds(5f);
		Ma9MHustle.BackToPage = true;   // opens on the 허슬 플레이 page
		SceneChanger.LoadLevel("Scene_ScheduleAndResult");
	}

	private IEnumerator HustlePage(int visit)
	{
		Ma9MHustlePage page = null;
		for (int i = 0; i < 40 && page == null; i++)
		{
			yield return new WaitForSeconds(0.25f);
			page = FindAnyObjectByType<Ma9MHustlePage>();
		}
		yield return new WaitForSeconds(3f);
		if (page == null)
		{
			Debug.Log("[DevAutoLogin] no 허슬 플레이 page");
			yield break;
		}
		if (visit > 0)
		{
			Capture("hustle_9_back" + visit);
			if (visit == 1)
			{
				// Then the page's own 재생 button on the second row.
				page.OnRow(1);
				yield return new WaitForSeconds(0.5f);
				UIImageButton play = (UIImageButton)typeof(Ma9MHustlePage).GetField("m_play", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(page);
				Debug.Log("[DevAutoLogin] 허슬 플레이: 재생 " + (play == null ? "missing" : "pressed"));
				if (play != null) play.gameObject.SendMessage("OnClick");
				yield break;
			}
			yield return new WaitForSeconds(2f);
			Application.Quit();
			yield break;
		}
		Capture("hustle_0_mine");
		yield return null;
		page.OnButton(1);
		yield return new WaitForSeconds(3f);
		Capture("hustle_1_league");
		page.OnRow(1);
		yield return new WaitForSeconds(0.5f);
		Capture("hustle_2_selected");
		page.OnRow(0);
		System.Collections.Generic.List<Ma9MHustle.Clip> clips = (System.Collections.Generic.List<Ma9MHustle.Clip>)typeof(Ma9MHustlePage).GetField("m_clips", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(page);
		if (clips == null || clips.Count == 0)
		{
			Debug.Log("[DevAutoLogin] 허슬 플레이: no clips");
			yield break;
		}
		Debug.Log("[DevAutoLogin] 허슬 플레이: " + clips.Count + " clips, playing " + Mathf.Min(m_hustle, clips.Count));
		Ma9MHustle.Play(clips.GetRange(0, Mathf.Min(m_hustle, clips.Count)), 0, true);
	}

	private IEnumerator GoBoard()
	{
		yield return new WaitForSeconds(5f);
		SceneChanger.LoadLevel("Scene_ScheduleAndResult");
	}

	// On 일정 및 결과: pick the game, open its board (결과판), then press the board's 하이라이트.
	private IEnumerator BoardFromResults()
	{
		m_boardShown = true;
		ScheduleAndResult_MyTeamResult results = null;
		for (int i = 0; i < 40 && results == null; i++)
		{
			yield return new WaitForSeconds(0.25f);
			results = FindAnyObjectByType<ScheduleAndResult_MyTeamResult>();
		}
		yield return new WaitForSeconds(4f);
		if (results == null)
		{
			Debug.Log("[DevAutoLogin] no 일정 및 결과 list");
			yield break;
		}
		results.OnBtn_ListItem(m_board);
		Capture("board_0_list");
		results.OnBtn_Scoreboard();
		yield return new WaitForSeconds(1.5f);
		Capture("board_1_race");
		yield return new WaitForSeconds(6f);
		Capture("board_2_" + m_board);
		GameObject button = GameObject.Find("Button_Highlight");
		Debug.Log("[DevAutoLogin] board of game " + m_board + ": 하이라이트 " + (button == null ? "missing" : "pressed"));
		if (button != null)
		{
			button.SendMessage("OnClick");
		}
	}

	private IEnumerator GoHighlight()
	{
		yield return new WaitForSeconds(5f);
		if (m_highlight < 0)
		{
			DataManager.PutData_String("Setting_SnsBroadcast", "False");
			Debug.Log("[DevAutoLogin] league broadcast (the lobby's button)");
			FindAnyObjectByType<Common_TopMenu>().OnBtn_Broadcast();
			yield break;
		}
		Debug.Log("[DevAutoLogin] highlights of game " + m_highlight);
		DataCenter.instance.SetTargetGame(m_highlight, ScheduleAndResult_MyTeamResult.MyGame, eBroadcastType.HIGHLIGHT, eMatchType.REGULAR_MATCH);
		Ma9MPackets.Ma9MPacket.PKS_CS_ENTER_BROADCAST p = new Ma9MPackets.Ma9MPacket.PKS_CS_ENTER_BROADCAST();
		p.game_num.n = (byte)m_highlight;
		p.result_num.n = ScheduleAndResult_MyTeamResult.MyGame;
		NetworkConnector.SendPacket(p);
	}

	private IEnumerator GoExhibition()
	{
		DataManager.PutData_String("Setting_SnsBroadcast", m_exhibition == "video" ? "False" : "True");
		yield return new WaitForSeconds(5f);
		Capture("ex_00_lobby");
		Debug.Log("[DevAutoLogin] exhibition -> Scene_ExhibitionGame");
		SceneChanger.LoadLevel("Scene_ExhibitionGame");
	}

	private IEnumerator PlayExhibition()
	{
		ExhibitionGame_Main main = null;
		FieldInfo tries = typeof(ExhibitionGame_Main).GetField("nRemainTryCount", BindingFlags.Instance | BindingFlags.NonPublic);
		while (main == null || (int)tries.GetValue(main) <= 0)
		{
			main = FindAnyObjectByType<ExhibitionGame_Main>();
			yield return new WaitForSeconds(0.5f);
		}
		yield return new WaitForSeconds(2f);
		Capture("ex_01_exhibition");
		Debug.Log("[DevAutoLogin] exhibition try");
		typeof(ExhibitionGame_Main).GetMethod("SendPKS_CS_TRY_EXHIBITION_GAME", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, null);
		// The machine spins until PUSH pops a ball; the opponent shows up once it lands.
		yield return new WaitForSeconds(4f);
		ExhibitionGame_SlotMachine machine = FindAnyObjectByType<ExhibitionGame_SlotMachine>();
		if (machine != null)
		{
			Debug.Log("[DevAutoLogin] exhibition push");
			machine.SendMessage("OnBtn_SlotMachinePush");
			// Burst: the result ball's drop into the tray.
			for (int k = 0; k < 16; k++)
			{
				yield return new WaitForSeconds(0.1f);
				foreach (GameObject b in machine.ResultSmallBalls)
				{
					if (b != null && b.activeSelf)
					{
						Rigidbody rb = b.GetComponent<Rigidbody>();
						Debug.Log($"[DevAutoLogin] push t={0.1f * (k + 1):F1} ball local {b.transform.localPosition} world {b.transform.position} v {(rb != null ? rb.linearVelocity.ToString("F4") : "-")} sleeping {(rb != null && rb.IsSleeping())}");
					}
				}
				if (k % 3 == 0)
				{
					Capture($"ex_01_push_{k:00}");
				}
			}
		}
		yield return new WaitForSeconds(3.4f);
		Capture("ex_02_opponent");
		Debug.Log("[DevAutoLogin] exhibition start");
		main.SendPK_CS_START_EXHIBITION_GAME();
	}

	private IEnumerator WatchBroadcast(string scene)
	{
		// "-ma9minterval <s>" shoots the broadcast more often (to follow a home run or a walk-off).
		string[] args = Environment.GetCommandLineArgs();
		int iv = Array.IndexOf(args, "-ma9minterval");
		float every = iv >= 0 && iv + 1 < args.Length && float.TryParse(args[iv + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float e) ? Mathf.Max(0.2f, e) : 5f;
		int shots = Mathf.CeilToInt(1200f / every);
		// "-ma9mstall <s>": once, that far into the broadcast, the game stands still for 6.5 s like an app gone to the
		// home screen (Ma9MLiveResume).
		int st = Array.IndexOf(args, "-ma9mstall");
		float stallAt = !s_stalled && st >= 0 && st + 1 < args.Length && float.TryParse(args[st + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sa) ? Time.realtimeSinceStartup + sa : float.MaxValue;
		for (int n = 0; n < shots; n++)
		{
			yield return new WaitForSeconds(n == 0 ? 6f : every);
			if (!s_stalled && Time.realtimeSinceStartup >= stallAt)
			{
				s_stalled = true;
				Capture($"stall_before_{scene}");
				Debug.Log("[DevAutoLogin] stalling 6.5 s");
				System.Threading.Thread.Sleep(6500);
			}
			Capture($"ex_{10 + n:000}_{scene}");
			if (SceneManager.GetActiveScene().name != scene)
			{
				break;
			}
		}
		// 허슬 플레이 clips come back to their page and go on from there (HustlePage quits).
		if (m_hustle >= 0) yield break;
		yield return new WaitForSeconds(3f);
		Application.Quit();
	}

	// League games keep being played every 20 minutes, so a login usually lands on the unconfirmed
	// results screen first: confirm them all (the screen's skip button, "자동 결과 확인") and go on
	// to the lobby, where the tour or exhibition continues.
	private IEnumerator ConfirmResults()
	{
		Unconfirmed_Main main = null;
		for (int i = 0; i < 20 && main == null; i++)
		{
			main = FindAnyObjectByType<Unconfirmed_Main>();
			yield return new WaitForSeconds(0.5f);
		}
		yield return new WaitForSeconds(3f);
		if (main == null)
		{
			yield break;
		}
		Capture("unconfirmed");
		Debug.Log("[DevAutoLogin] confirming unplayed-through league results");
		main.SendMessage("SendPKS_CS_GAMESCOREBOARD_ALL");
		yield return new WaitForSeconds(3f);
		main.SendMessage("OnButton_MainLobby");
	}

	private IEnumerator Intro()
	{
		IntroTutorial_Main main = null;
		while (main == null)
		{
			main = FindAnyObjectByType<IntroTutorial_Main>();
			yield return new WaitForSeconds(0.5f);
		}
		yield return new WaitForSeconds(4f);
		Capture("intro_01_start");
		string[] clubs = m_introClubs ?? new string[1] { Emblem.EastTeam[0].ToString() };
		for (int k = 0; k < clubs.Length; k++)
		{
			eTeamName club = (eTeamName)Enum.Parse(typeof(eTeamName), clubs[k]);
			if (Emblem.WestTeam.Contains(club))
			{
				main.OnCheckWestTeam();
			}
			else
			{
				main.OnCheckEastTeam();
			}
			yield return new WaitForSeconds(2f);
			Capture("intro_02_clubs_" + k);
			main.CheckSelectItem((int)club);
			yield return new WaitForSeconds(2f);
			Capture("intro_03_club_" + clubs[k]);
		}
		main.SendMessage("OnBtnNext");
		yield return new WaitForSeconds(5f);
		Capture("intro_04_keyplayers");
		IntroTutorial_Step3 step3 = FindAnyObjectByType<IntroTutorial_Step3>();
		if (step3 != null)
		{
			PlayerCardSlot[] bats = step3.m_BatterGrid.GetComponentsInChildren<PlayerCardSlot>();
			if (bats.Length > 0)
			{
				step3.OnDoubleClick_PlayerCard(bats[0].gameObject);
			}
			yield return new WaitForSeconds(1.5f);
			PlayerCardSlot[] arms = step3.m_PitcherGrid.GetComponentsInChildren<PlayerCardSlot>();
			if (arms.Length > 0)
			{
				step3.OnDoubleClick_PlayerCard(arms[0].gameObject);
			}
		}
		yield return new WaitForSeconds(2f);
		Capture("intro_05_picked");
		main.SendMessage("OnBtnNext");
		yield return new WaitForSeconds(3f);
		Capture("intro_06_create");
		yield return new WaitForSeconds(1f);
		Application.Quit();
	}

	private IEnumerator Login()
	{
		Login_Main login = null;
		FieldInfo state = typeof(Login_Main).GetField("_loginState", BindingFlags.Instance | BindingFlags.NonPublic);
		while (login == null || (eLoginMainState)state.GetValue(login) != eLoginMainState.LOGIN_WAIT)
		{
			login = FindAnyObjectByType<Login_Main>();
			yield return null;
		}
		login.email.text = m_email;
		login.password.text = m_password;
		Debug.Log("[DevAutoLogin] pressing Login as " + m_email);
		login.SendMessage("OnButton_Login");
	}

	private IEnumerator PickTeam()
	{
		ServerSelect select = null;
		// Wait for the team list reply: it fills either the team list or the empty "create" slot.
		while (select == null || (!HasTeam(select) && GetField(select, "dummyInfo") == null))
		{
			select = FindAnyObjectByType<ServerSelect>();
			yield return new WaitForSeconds(0.5f);
		}
		yield return new WaitForSeconds(1f);
		if (HasTeam(select))
		{
			Debug.Log("[DevAutoLogin] choosing first team");
			select.Team[0].SendMessage("OnBtnConnect");
		}
		else
		{
			Debug.Log("[DevAutoLogin] creating a team");
			select.SendMessage("OnBtnCreateTeam");
		}
	}

	private static bool HasTeam(ServerSelect select)
	{
		ICollection list = GetField(select, "UserTeamInfoList") as ICollection;
		return list != null && list.Count > 0;
	}

	private static object GetField(object target, string name)
	{
		return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
	}
}
