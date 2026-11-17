using System;
using System.Collections.Generic;
using Ma9MPackets;
using NetworkLibrary.PacketType;
using TableReader;
using UnityEngine;

// Ma9M port (engine session, 2026-11-09): "모두 펼치기" for card packs. User 11-09: "1장씩 까게 할 수도 있고, 한 번에
// 펼쳐서 다같이 보여주는 기능도 추가" with a reference picture (cards dealt face down in a row, the rare ones glowing before
// they turn: "사용자가 기대하게 만드는게 좀 괜찮을 것 같음"). Twenty cards (two rows of ten, PageSize) still read on a
// phone; more go page by page ("다음 20장"). Packs open and sell up to MaxOpen (100) at a time, starting at the most that
// can be had (user 11-09 21:1x: "상점이랑 인벤토리 모두 최대 100개이고, 100개 이하일 시 가능한 최대 수량으로 깔 수 있도록";
// 20 at a time before that, for one evening).
//
// The cards are dealt face down with the pack's picture as the back. 8볼 and up (coach 레어/엘리트) glow by grade before
// they turn, in the card colours: 8·9볼 레어 red, 10볼·엘리트 purple, 레전드 changing colours with the legend sparkle. A tap turns one card;
// "모두 뒤집기" turns the rest, plain ones quickly and the glowing ones last, best at the end, with the high-card sound.
// A turned card opens its info on a tap. The back key turns everything at once, or closes when all are turned.
// "한 장씩" goes back to the original one-card opening for the cards not yet seen; the choice is kept per device.
// With 카드연출 off (Ma9MPortSettings.CardEffects) there is no glow and no pause.
public class Ma9MCardSpread : MonoBehaviour
{
	public const int MaxOpen = 100;

	public const int PageSize = 20;

	private const int PerRow = 10;

	private const string PrefKey = "Ma9M_CardSpread";

	private const float CardW = 79f;

	private const float CardH = 127f;

	private const float Glow = 16f;

	private static readonly string[] SingleModeParts = new string[6] { "Background", "Btn_CardOpen", "Info", "Sprite_CardPack", "Text", "ItemCardSlot" };

	public static bool Use
	{
		get
		{
			try
			{
				return PlayerPrefs.GetInt(PrefKey, 1) != 0;
			}
			catch
			{
				return true;
			}
		}
		set
		{
			try
			{
				PlayerPrefs.SetInt(PrefKey, value ? 1 : 0);
				PlayerPrefs.Save();
			}
			catch
			{
			}
		}
	}

	private class Card
	{
		public PUICardInfo info;

		public int rank;

		public GameObject holder;

		public GameObject back;

		public GameObject face;

		public UITexture glow;

		public BoxCollider tap;

		public Vector3 from;

		public Vector3 to;

		public float dealAt;

		public bool dealt;

		public bool turned;

		public float turnStart = -1f;

		public float turnLength;

		public float scale;

		public bool quiet;
	}

	private ShopInventory_CardOpen owner;

	private eShopType shopType;

	private Item packItem;

	private readonly List<PUICardInfo> pending = new List<PUICardInfo>();

	private readonly List<Card> cards = new List<Card>();

	private readonly List<Card> turnQueue = new List<Card>();

	private float nextTurnAt;

	private int seen;

	private int total;

	private GameObject root;

	private UITexture backdrop;

	private UILabel countLabel;

	private UIImageButton btnMain;

	private UIImageButton btnMode;

	private UIImageButton btnMore;

	private int moreCount;

	private readonly Dictionary<GameObject, bool> hiddenParts = new Dictionary<GameObject, bool>();

	private bool escapePushed;

	private static Texture2D glowTexture;

	private Vector2 screenMin;

	private Vector2 screenMax;

	public bool Active
	{
		get
		{
			return root != null;
		}
	}

	// 업적 boxes that came while cards were face down (PopupManager.EnqueueMissionComplete), shown when all are turned.
	private static Ma9MCardSpread s_current;

	private static readonly List<KeyValuePair<int, Action>> s_heldMissions = new List<KeyValuePair<int, Action>>();

	// (11-15, AI tester LG 13회차: "첫 레전드" came after the first 20 of 100 and covered the next row) They wait for the last
	// page: a page all turned with more to come still holds them.
	private static bool Holding()
	{
		return s_current != null && s_current.root != null && (!s_current.AllTurned() || s_current.pending.Count > 0);
	}

	public static bool HoldMission(int missionIndex, Action endCallBack)
	{
		if (!Holding())
		{
			return false;
		}
		s_heldMissions.Add(new KeyValuePair<int, Action>(missionIndex, endCallBack));
		return true;
	}

	// The server's 도전과제 묶음 창 ("도전과제 n개 달성", a type 1 POPUP since 11-12, Achievements.Deliver) held the same way
	// (2026-11-12, tester_o: it came over the face-down cards; a "레어 10장" in it would name them before they turn).
	private static readonly List<KeyValuePair<string[], Action>> s_heldPopups = new List<KeyValuePair<string[], Action>>();

	public static bool HoldPopup(string title, string message, Action confirm)
	{
		if (!Holding() || title == null || !title.StartsWith("도전과제"))
		{
			return false;
		}
		s_heldPopups.Add(new KeyValuePair<string[], Action>(new string[2] { title, message }, confirm));
		return true;
	}

	// (11-15, AI tester NC 10회차) A 도전과제 window that came just before the cards (the pack's 업적 are paid with the purchase)
	// goes back to wait with the others; it covered the spread and took the spread's back-key entry when it was closed.
	private static void TakeBackOpenPopups()
	{
		foreach (Popup1 popup in UnityEngine.Object.FindObjectsOfType<Popup1>())
		{
			string title = popup.Ma9MTitle;
			if (title != null && title.StartsWith("도전과제"))
			{
				string content;
				Action confirm;
				popup.Ma9MTakeBack(out title, out content, out confirm);
				s_heldPopups.Add(new KeyValuePair<string[], Action>(new string[2] { title, content }, confirm));
				Debug.Log("[Ma9MMission] spread: took back the open window \"" + title + "\" until the cards are turned");
			}
		}
		// (11-17, AI tester autotest7 6회차: 업적 211 "레어 10장" came with 20 packs and ran its 5 s under the face-down cards,
		// so the tester saw only the MP) The 업적 box on screen and those queued behind it wait the same way.
		foreach (KeyValuePair<int, Action> box in PopupManager.Ma9MTakeBackMissionBoxes())
		{
			s_heldMissions.Add(box);
			Debug.Log("[Ma9MMission] spread: took back 업적 box " + box.Key + " until the cards are turned");
		}
	}

	// sceneGoing: the spread went with its screen (OnDisable), so the windows wait in PopupManager for the next screen.
	private static void ReleaseMissions(bool sceneGoing = false)
	{
		if (s_heldPopups.Count > 0)
		{
			List<KeyValuePair<string[], Action>> popups = new List<KeyValuePair<string[], Action>>(s_heldPopups);
			s_heldPopups.Clear();
			foreach (KeyValuePair<string[], Action> p in popups)
			{
				if (sceneGoing)
				{
					PopupManager.Ma9MHoldForNextScreen(p.Key[0], p.Key[1], p.Value);
				}
				else
				{
					PopupManager.ViewPopup1(p.Key[0], p.Key[1], Localization.instance.Get("OK"), p.Value);
				}
			}
		}
		if (s_heldMissions.Count == 0)
		{
			return;
		}
		List<KeyValuePair<int, Action>> held = new List<KeyValuePair<int, Action>>(s_heldMissions);
		s_heldMissions.Clear();
		foreach (KeyValuePair<int, Action> item in held)
		{
			PopupManager.EnqueueMissionComplete(item.Key, item.Value);
		}
	}

	public void Begin(ShopInventory_CardOpen owner, List<PUICardInfo> list, int seenSoFar, int totalCount)
	{
		this.owner = owner;
		shopType = owner.Ma9MItemType;
		packItem = owner.Ma9MBuyItem;
		pending.Clear();
		pending.AddRange(list);
		seen = seenSoFar;
		total = totalCount;
		s_current = this;
		BuildUI();
		NextPage();
	}

	private void BuildUI()
	{
		if (root != null)
		{
			return;
		}
		HideSingleMode(true);
		root = NGUITools.AddChild(owner.gameObject);
		root.name = "Ma9MCardSpread";
		ComputeScreen();
		GameObject go = NGUITools.AddChild(root);
		go.name = "Backdrop";
		backdrop = go.AddComponent<UITexture>();
		backdrop.shader = Shader.Find("Unlit/Transparent Colored");
		backdrop.mainTexture = Texture2D.whiteTexture;
		backdrop.color = new Color(0.02f, 0.03f, 0.08f, 0.94f);
		go.transform.localPosition = new Vector3((screenMin.x + screenMax.x) * 0.5f, (screenMin.y + screenMax.y) * 0.5f, -2f);
		go.transform.localScale = new Vector3(screenMax.x - screenMin.x + 4f, screenMax.y - screenMin.y + 4f, 1f);
		BoxCollider boxCollider = go.AddComponent<BoxCollider>();
		boxCollider.size = new Vector3(1f, 1f, 0f);
		if (owner.Text_OpenCardCount != null)
		{
			GameObject label = UnityEngine.Object.Instantiate(owner.Text_OpenCardCount.gameObject) as GameObject;
			label.name = "Count";
			label.transform.parent = root.transform;
			label.transform.localScale = new Vector3(18f, 18f, 1f);
			label.transform.localPosition = new Vector3(screenMin.x + 70f, screenMax.y - 78f, -30f);
			label.SetActive(true);
			countLabel = label.GetComponent<UILabel>();
			countLabel.pivot = UIWidget.Pivot.Left;
			countLabel.color = Color.white;
		}
		float y = screenMin.y + 30f;
		float cx = (screenMin.x + screenMax.x) * 0.5f;
		btnMode = MakeButton("Mode", new Vector3(cx - 150f, y, -30f), "OnBtn_Ma9MOneByOne", "한 장씩");
		btnMain = MakeButton("Main", new Vector3(cx, y, -30f), "OnBtn_Ma9MMain", "모두 뒤집기");
		// "더 열기 / 더 사기" is made when it is first needed: a widget switched off in the frame it was made keeps
		// its picture on this NGUI's panel (an empty grey button stayed on screen).
		btnMore = null;
		TakeBackOpenPopups();
		AndroidManager.Instance.PushEscapeAction(OnEscape);
		escapePushed = true;
	}

	private UIImageButton MakeButton(string name, Vector3 pos, string function, string text)
	{
		GameObject go = UnityEngine.Object.Instantiate(owner.Btn_CardOpen.gameObject) as GameObject;
		go.name = "Btn_" + name;
		go.transform.parent = root.transform;
		go.transform.localScale = Vector3.one;
		go.transform.localPosition = pos;
		DropLocalize(go);
		go.SetActive(true);
		UIButtonMessage message = go.GetComponent<UIButtonMessage>();
		if (message != null)
		{
			message.target = base.gameObject;
			message.functionName = function;
		}
		UILabel label = go.GetComponentInChildren<UILabel>();
		if (label != null)
		{
			label.text = text;
		}
		UIImageButton button = go.GetComponent<UIImageButton>();
		Ma9MClientUtil.ButtonSetActive(button, true);
		return button;
	}

	// The original button's label carries UILocalize, which puts "바로열기" back on enable and on start.
	public static void DropLocalize(GameObject go)
	{
		foreach (UILocalize localize in go.GetComponentsInChildren<UILocalize>(true))
		{
			UnityEngine.Object.DestroyImmediate(localize);
		}
	}

	private void SetButtonText(UIImageButton button, string text)
	{
		UILabel label = button.GetComponentInChildren<UILabel>();
		if (label != null)
		{
			label.text = text;
		}
	}

	// The visible area in the root's units, from the camera that draws this panel.
	private void ComputeScreen()
	{
		Camera cam = NGUITools.FindCameraForLayer(owner.gameObject.layer);
		if (cam == null)
		{
			cam = UICamera.mainCamera;
		}
		Vector3 a = root.transform.InverseTransformPoint(cam.ViewportToWorldPoint(new Vector3(0f, 0f, 0f)));
		Vector3 b = root.transform.InverseTransformPoint(cam.ViewportToWorldPoint(new Vector3(1f, 1f, 0f)));
		screenMin = new Vector2(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
		screenMax = new Vector2(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
	}

	private void HideSingleMode(bool hide)
	{
		if (hide)
		{
			hiddenParts.Clear();
			foreach (string part in SingleModeParts)
			{
				Transform t = owner.transform.Find(part);
				if (t != null)
				{
					hiddenParts[t.gameObject] = t.gameObject.activeSelf;
					t.gameObject.SetActive(false);
				}
			}
			return;
		}
		foreach (KeyValuePair<GameObject, bool> hiddenPart in hiddenParts)
		{
			if (hiddenPart.Key != null)
			{
				hiddenPart.Key.SetActive(hiddenPart.Value);
			}
		}
		hiddenParts.Clear();
	}

	private void NextPage()
	{
		ClearCards();
		int n = Mathf.Min(PageSize, pending.Count);
		if (n <= 0)
		{
			Finish();
			return;
		}
		List<PUICardInfo> page = pending.GetRange(0, n);
		pending.RemoveRange(0, n);
		int cols = Mathf.Min(n, PerRow);
		int rows = (n + PerRow - 1) / PerRow;
		float top = screenMax.y - 100f;
		float bottom = screenMin.y + 62f;
		float gap = 10f;
		float availW = screenMax.x - screenMin.x - 48f;
		float availH = top - bottom;
		float w = Mathf.Min((availW - gap * (cols - 1)) / cols, (availH - gap * (rows - 1)) / rows * CardW / CardH);
		w = Mathf.Min(w, CardW * 1.6f);
		float s = w / CardW;
		float h = CardH * s;
		float cy = (top + bottom) * 0.5f;
		float cx = (screenMin.x + screenMax.x) * 0.5f;
		Vector3 from = new Vector3(cx, screenMin.y - h, -10f);
		for (int i = 0; i < n; i++)
		{
			int row = i / PerRow;
			int col = i % PerRow;
			int inRow = (row == rows - 1) ? (n - row * PerRow) : PerRow;
			float x = cx + (col - (inRow - 1) * 0.5f) * (w + gap);
			float y = cy + ((rows - 1) * 0.5f - row) * (h + gap);
			cards.Add(MakeCard(page[i], new Vector3(x, y, -10f), from, s, i * 0.05f));
		}
		turnQueue.Clear();
		SetButtonText(btnMain, "모두 뒤집기");
		Ma9MClientUtil.ButtonSetActive(btnMain, true);
		btnMode.gameObject.SetActive(true);
		if (btnMore != null)
		{
			btnMore.gameObject.SetActive(false);
		}
		SoundManager.PlayEffectSound(eEffectSound.Card_Drag);
		UpdateCount();
	}

	private Card MakeCard(PUICardInfo info, Vector3 to, Vector3 from, float s, float delay)
	{
		Card card = new Card();
		card.info = info;
		card.rank = RankOf(info.index.n);
		card.to = to;
		card.from = from;
		card.scale = s;
		card.dealAt = Time.time + delay;
		card.holder = NGUITools.AddChild(root);
		card.holder.name = "Card" + cards.Count;
		card.holder.transform.localPosition = from;
		card.holder.transform.localScale = new Vector3(s, s, 1f);
		if (card.rank > 0 && Ma9MPortSettings.CardEffects)
		{
			GameObject g = NGUITools.AddChild(card.holder);
			g.name = "Glow";
			card.glow = g.AddComponent<UITexture>();
			card.glow.shader = Shader.Find("Unlit/Transparent Colored");
			card.glow.mainTexture = GlowTexture();
			card.glow.color = GlowColour(card.rank, 0f);
			g.transform.localPosition = new Vector3(0f, 0f, 2f);
			g.transform.localScale = new Vector3(CardW + Glow * 2f, CardH + Glow * 2f, 1f);
		}
		card.back = MakeBack(card.holder);
		if (card.rank == 4 && Ma9MPortSettings.CardEffects && PlayerCardDrawer.instance != null && PlayerCardDrawer.instance.LegendCard_Glow_Sprite_EF != null && card.back != null)
		{
			GameObject sparkle = UnityEngine.Object.Instantiate(PlayerCardDrawer.instance.LegendCard_Glow_Sprite_EF) as GameObject;
			sparkle.transform.parent = card.back.transform;
			sparkle.transform.localPosition = new Vector3(1f, 0f, -1f);
			sparkle.transform.localScale = Vector3.one;
		}
		card.face = MakeFace(info, card.holder);
		if (card.face != null)
		{
			card.face.SetActive(false);
		}
		card.tap = card.holder.AddComponent<BoxCollider>();
		card.tap.size = new Vector3(CardW, CardH, 0f);
		card.tap.enabled = false;
		Ma9MSpreadCardTap tap = card.holder.AddComponent<Ma9MSpreadCardTap>();
		tap.spread = this;
		return card;
	}

	private GameObject MakeBack(GameObject parent)
	{
		if (packItem == null || ItemCardDrawer.instance == null)
		{
			return null;
		}
		GameObject go = NGUITools.AddChild(parent);
		go.name = "Back";
		go.transform.localPosition = new Vector3(0f, 0f, 0f);
		ItemCardDrawer.eItemSlotType type = (packItem is ShopItem) ? ItemCardDrawer.eItemSlotType.LARGE_FRAME : ItemCardDrawer.eItemSlotType.LARGE_PACK;
		GameObject picture = ItemCardDrawer.instance.MakeCardObject(packItem, go, type);
		if (picture != null)
		{
			// The pack picture drawn at the card's 79x127 (a pack is 90x149, a shop product 79x127).
			picture.transform.localScale = (type == ItemCardDrawer.eItemSlotType.LARGE_FRAME) ? Vector3.one : new Vector3(CardW / 90f, CardH / 149f, 1f);
		}
		return go;
	}

	private GameObject MakeFace(PUICardInfo info, GameObject parent)
	{
		switch (shopType)
		{
		case eShopType.PLAYER:
			return PlayerCardDrawer.instance.MakeCardObject(info, parent);
		case eShopType.COACH:
			return CoachCardDrawer.instance.MakeCardObject(info.index.n, parent);
		default:
			return ItemCardDrawer.instance.MakeCardObject(ItemListTable.GetItem(info.index.n), parent, ItemCardDrawer.eItemSlotType.LARGE_FRAME);
		}
	}

	// 0 plain, 1 8볼 / coach 레어, 2 9볼, 3 10볼·엘리트 / coach 엘리트, 4 레전드.
	private int RankOf(int index)
	{
		if (shopType == eShopType.PLAYER)
		{
			TRRow_PlayerCard row = ResourceManager.g_playerTable.FindBatter(index);
			if (row == null)
			{
				row = ResourceManager.g_playerTable.FindPitcher(index);
			}
			if (row == null)
			{
				return 0;
			}
			if (row.cardgrade == ePlayerCardGrade.CardGrade_LEGEND)
			{
				return 4;
			}
			if (row.ball >= 10 || row.cardgrade == ePlayerCardGrade.CardGrade_ELITE)
			{
				return 3;
			}
			if (row.ball == 9)
			{
				return 2;
			}
			return (row.ball == 8) ? 1 : 0;
		}
		if (shopType == eShopType.COACH)
		{
			CoachCardInfo coach = TR_Coach_Index.FindCoach(index);
			if (coach == null)
			{
				return 0;
			}
			if (coach.cardgrade == eCoachCardGrade.Elite)
			{
				return 3;
			}
			return (coach.cardgrade == eCoachCardGrade.Rare) ? 1 : 0;
		}
		return 0;
	}

	private static Color GlowColour(int rank, float t)
	{
		// The card colours (user 11-09: "엘리트는 그냥 보라색으로 하고, 9볼, 8볼 레어는 그냥 붉은색으로 하는게 좋지 않아?
		// 카드색에도 맞고"): 레어 red, 엘리트 purple; 9볼 turns after 8볼 but glows the same.
		switch (rank)
		{
		case 1:
		case 2:
			return new Color(1f, 0.3f, 0.26f, 1f);
		case 3:
			return new Color(0.74f, 0.38f, 1f, 1f);
		case 4:
		{
			Color c = HsvToRgb(Mathf.Repeat(t * 0.35f, 1f), 0.65f, 1f);
			c.a = 1f;
			return c;
		}
		default:
			return Color.white;
		}
	}

	private static Color HsvToRgb(float h, float s, float v)
	{
		float r = Mathf.Abs(h * 6f - 3f) - 1f;
		float g = 2f - Mathf.Abs(h * 6f - 2f);
		float b = 2f - Mathf.Abs(h * 6f - 4f);
		return new Color(Mathf.Lerp(1f, Mathf.Clamp01(r), s) * v, Mathf.Lerp(1f, Mathf.Clamp01(g), s) * v, Mathf.Lerp(1f, Mathf.Clamp01(b), s) * v, 1f);
	}

	// A soft rounded frame: opaque inside, fading out over the outer Glow units.
	private static Texture2D GlowTexture()
	{
		if (glowTexture != null)
		{
			return glowTexture;
		}
		int w = 56;
		int h = 80;
		float edge = 10f;
		glowTexture = new Texture2D(w, h, TextureFormat.ARGB32, false);
		glowTexture.wrapMode = TextureWrapMode.Clamp;
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				float dx = Mathf.Max(0f, Mathf.Max(edge - (x + 0.5f), x + 0.5f - (w - edge)));
				float dy = Mathf.Max(0f, Mathf.Max(edge - (y + 0.5f), y + 0.5f - (h - edge)));
				float d = Mathf.Sqrt(dx * dx + dy * dy) / edge;
				float a = Mathf.Clamp01(1f - d);
				glowTexture.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
			}
		}
		glowTexture.Apply();
		return glowTexture;
	}

	private void Update()
	{
		if (root == null)
		{
			return;
		}
		float now = Time.time;
		for (int i = 0; i < cards.Count; i++)
		{
			Card card = cards[i];
			if (!card.dealt)
			{
				float t = Mathf.Clamp01((now - card.dealAt) / 0.28f);
				if (t <= 0f)
				{
					continue;
				}
				float e = 1f - (1f - t) * (1f - t);
				card.holder.transform.localPosition = Vector3.Lerp(card.from, card.to, e);
				if (t >= 1f)
				{
					card.dealt = true;
					if (!card.turned && card.turnStart < 0f)
					{
						card.tap.enabled = true;
					}
				}
			}
			if (card.turnStart >= 0f)
			{
				Turning(card, now);
			}
			else if (card.glow != null && !card.turned)
			{
				// Waiting face down: the glow breathes.
				Color c = GlowColour(card.rank, now);
				c.a = 0.55f + 0.35f * Mathf.Sin(now * 4f + i);
				card.glow.color = c;
			}
		}
		if (turnQueue.Count > 0 && now >= nextTurnAt)
		{
			Card next = turnQueue[0];
			turnQueue.RemoveAt(0);
			if (!next.turned && next.turnStart < 0f)
			{
				StartTurn(next);
				nextTurnAt = now + ((next.rank > 0 && Ma9MPortSettings.CardEffects) ? 0.75f : 0.06f);
			}
		}
	}

	private void Turning(Card card, float now)
	{
		float t = Mathf.Clamp01((now - card.turnStart) / card.turnLength);
		float half = 0.5f;
		float s = card.scale;
		if (t < half)
		{
			float k = 1f - t / half;
			card.holder.transform.localScale = new Vector3(s * Mathf.Max(0.02f, k), s, 1f);
		}
		else
		{
			if (card.back != null && card.back.activeSelf)
			{
				card.back.SetActive(false);
				if (card.face != null)
				{
					card.face.SetActive(true);
				}
				PlayTurnSound(card);
			}
			float k2 = (t - half) / half;
			float pop = (card.rank > 0 && Ma9MPortSettings.CardEffects) ? (1f + 0.18f * Mathf.Sin(k2 * Mathf.PI)) : 1f;
			card.holder.transform.localScale = new Vector3(s * Mathf.Max(0.02f, k2) * pop, s * pop, 1f);
			if (card.glow != null)
			{
				Color c = GlowColour(card.rank, now);
				c.a = Mathf.Lerp(1f, 0.6f, k2);
				card.glow.color = c;
			}
		}
		if (t >= 1f)
		{
			card.turnStart = -1f;
			card.turned = true;
			card.holder.transform.localScale = new Vector3(s, s, 1f);
			if (card.back != null && card.back.activeSelf)
			{
				card.back.SetActive(false);
				if (card.face != null)
				{
					card.face.SetActive(true);
				}
				PlayTurnSound(card);
			}
			// The card's own collider now takes the tap: its info.
			card.tap.enabled = false;
			PlayerCardSlot slot = (card.face != null) ? card.face.GetComponent<PlayerCardSlot>() : null;
			if (slot != null)
			{
				slot.Ma9MSetTapInfo();
			}
			seen++;
			UpdateCount();
			if (AllTurned())
			{
				PageDone();
			}
		}
	}

	private void PlayTurnSound(Card card)
	{
		if (card.quiet)
		{
			return;
		}
		SoundManager.PlayEffectSound((card.rank > 0 && Ma9MPortSettings.CardEffects) ? eEffectSound.CardOpen_Over7 : eEffectSound.CardOpen);
	}

	private void StartTurn(Card card)
	{
		if (card.turned || card.turnStart >= 0f)
		{
			return;
		}
		if (!card.dealt)
		{
			card.dealt = true;
			card.holder.transform.localPosition = card.to;
		}
		card.tap.enabled = false;
		card.turnStart = Time.time;
		card.turnLength = (card.rank > 0 && Ma9MPortSettings.CardEffects) ? 0.55f : 0.2f;
	}

	public void OnCardTapped(GameObject holder)
	{
		foreach (Card card in cards)
		{
			if (card.holder == holder)
			{
				turnQueue.Remove(card);
				StartTurn(card);
				break;
			}
		}
	}

	private bool AllTurned()
	{
		foreach (Card card in cards)
		{
			if (!card.turned)
			{
				return false;
			}
		}
		return true;
	}

	private void PageDone()
	{
		turnQueue.Clear();
		if (pending.Count == 0)
		{
			ReleaseMissions();
		}
		btnMode.gameObject.SetActive(false);
		if (pending.Count > 0)
		{
			SetButtonText(btnMain, "다음 " + Mathf.Min(PageSize, pending.Count) + "장");
		}
		else
		{
			SetButtonText(btnMain, Localization.instance.Get("OK"));
			moreCount = MoreCount();
			if (moreCount > 0)
			{
				string text = moreCount + ((ShopForMore() != null) ? "장 더 사기" : "장 더 열기");
				if (btnMore == null)
				{
					btnMore = MakeButton("More", btnMain.transform.localPosition + new Vector3(150f, 0f, 0f), "OnBtn_Ma9MMore", text);
				}
				btnMore.gameObject.SetActive(true);
				SetButtonText(btnMore, text);
				Ma9MClientUtil.ButtonSetActive(btnMore, true);
			}
		}
		Ma9MClientUtil.ButtonSetActive(btnMain, true);
	}

	// From the inventory: open more of the same pack. From the shop (user 11-09: "20장 추가 오픈 버튼"): buy the same
	// product and count again, through the shop's own checks and its confirm popup. Not from the 홈런볼 shop.
	private int MoreCount()
	{
		GameObject receiver = owner.Ma9MMessageReceiver;
		Inventory_Main inventory = (receiver != null) ? receiver.GetComponent<Inventory_Main>() : null;
		if (inventory != null)
		{
			return inventory.Ma9MMoreCount();
		}
		Shop_Main shop = ShopForMore();
		return (shop != null) ? shop.Ma9MBuyAgainCount() : 0;
	}

	private Shop_Main ShopForMore()
	{
		GameObject receiver = owner.Ma9MMessageReceiver;
		Shop_Main shop = (receiver != null) ? receiver.GetComponent<Shop_Main>() : null;
		return (shop != null && shop.GetType() == typeof(Shop_Main)) ? shop : null;
	}

	// Plain cards first in their places, then the glowing ones from the lowest to the best.
	private void TurnAll(bool instant)
	{
		List<Card> order = new List<Card>();
		foreach (Card card in cards)
		{
			if (!card.turned && card.turnStart < 0f)
			{
				order.Add(card);
			}
		}
		if (!Ma9MPortSettings.CardEffects)
		{
			order.Sort((Card a, Card b) => cards.IndexOf(a).CompareTo(cards.IndexOf(b)));
		}
		else
		{
			order.Sort((Card a, Card b) => (a.rank != b.rank) ? a.rank.CompareTo(b.rank) : cards.IndexOf(a).CompareTo(cards.IndexOf(b)));
		}
		if (instant)
		{
			turnQueue.Clear();
			bool high = false;
			foreach (Card card2 in order)
			{
				StartTurn(card2);
				card2.turnStart = Time.time - card2.turnLength;
				card2.quiet = true;
				high |= card2.rank > 0;
			}
			if (order.Count > 0)
			{
				SoundManager.PlayEffectSound((high && Ma9MPortSettings.CardEffects) ? eEffectSound.CardOpen_Over7 : eEffectSound.CardOpen);
			}
			return;
		}
		turnQueue.Clear();
		turnQueue.AddRange(order);
		nextTurnAt = Time.time;
	}

	private void OnBtn_Ma9MMain()
	{
		if (!AllTurned())
		{
			Ma9MClientUtil.ButtonSetActive(btnMain, false);
			TurnAll(false);
		}
		else if (pending.Count > 0)
		{
			NextPage();
		}
		else
		{
			Finish();
		}
	}

	private void OnBtn_Ma9MMore()
	{
		GameObject receiver = owner.Ma9MMessageReceiver;
		Inventory_Main inventory = (receiver != null) ? receiver.GetComponent<Inventory_Main>() : null;
		Shop_Main shop = ShopForMore();
		int n = moreCount;
		if (inventory == null && shop != null)
		{
			Finish();
			shop.Ma9MBuyAgain(n);
			return;
		}
		// The request goes before the inventory's own end-of-opening refresh (training packs ask for the whole list
		// again), so the answer still finds the same selected pack.
		if (inventory != null && n > 0)
		{
			inventory.Ma9MOpenMore(n);
		}
		Finish();
	}

	// Back to one card at a time for the cards not seen yet (this page's face-down ones and the later pages).
	private void OnBtn_Ma9MOneByOne()
	{
		Use = false;
		List<PUICardInfo> left = new List<PUICardInfo>();
		foreach (Card card in cards)
		{
			if (!card.turned && card.turnStart < 0f)
			{
				left.Add(card.info);
			}
		}
		left.AddRange(pending);
		pending.Clear();
		int seenNow = seen;
		Teardown();
		owner.Ma9MContinueOneByOne(left, seenNow);
	}

	// AndroidManager runs the top action without popping it and turns the key off until someone turns it back on.
	// The back key turns this page's cards, then closes the spread with the later pages too (2026-11-12, AI tester LG:
	// "뒤로 가기로 남은 연출 다 건너뛰기"가 펼치기에서 "다음 20장"이 되어 100장이면 9번 눌러야 했다; the cards are in the
	// inventory already, and the "다음" button still goes page by page).
	private void OnEscape()
	{
		if (root == null)
		{
			return;
		}
		if (!AllTurned())
		{
			TurnAll(true);
		}
		else
		{
			Finish();
			return;
		}
		AndroidManager.Instance.EscapeEnable = true;
	}

	private void UpdateCount()
	{
		if (countLabel != null)
		{
			countLabel.text = seen + " / " + total;
		}
	}

	private void ClearCards()
	{
		foreach (Card card in cards)
		{
			if (card.holder != null)
			{
				if (card.face != null && (shopType == eShopType.PLAYER || shopType == eShopType.COACH))
				{
					Ma9MClientUtil.DestroyCardObj(card.face);
				}
				UnityEngine.Object.Destroy(card.holder);
			}
		}
		cards.Clear();
		turnQueue.Clear();
	}

	private void Teardown(bool sceneGoing = false)
	{
		ClearCards();
		if (escapePushed)
		{
			escapePushed = false;
			// (11-15) Its own entry only: a plain Pop took another screen's, and threw on a stack the scene change had cleared.
			AndroidManager.Instance.Ma9MRemoveEscapeAction(OnEscape);
			AndroidManager.Instance.EscapeEnable = true;
		}
		if (root != null)
		{
			UnityEngine.Object.Destroy(root);
			root = null;
		}
		HideSingleMode(false);
		if (s_current == this)
		{
			s_current = null;
		}
		ReleaseMissions(sceneGoing);
	}

	private void Finish()
	{
		pending.Clear();
		Teardown();
		owner.Ma9MSpreadDone();
	}

	// The spread goes with its screen (back key to the lobby, a scene change). (11-15) Also when the scene took the root
	// first: the held 업적 boxes and windows wait for the next screen (AI tester NC 10회차: paid, never shown).
	private void OnDisable()
	{
		if (root != null || s_current == this || escapePushed)
		{
			pending.Clear();
			Teardown(true);
		}
	}
}

// Taps on a face-down card in the spread.
public class Ma9MSpreadCardTap : MonoBehaviour
{
	public Ma9MCardSpread spread;

	private void OnClick()
	{
		if (spread != null)
		{
			spread.OnCardTapped(base.gameObject);
		}
	}
}
