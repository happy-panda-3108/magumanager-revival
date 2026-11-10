using TableReader;

// Ma9M port (user, 2026-11-02: "앨범에 10볼, 9볼, 8볼 엘리트 레어 볼 수 있는데, 거기서 조합처럼 필터링이나 정렬을 할 수 있을까? …
// 외야수 팩만 까고 있을 때 남은 외야수가 몇인지 계산하는게 정말 힘들었음"): the 등급별 album pages get 선수조합's filter bar
// (볼·구단·연도·포지션; a short press sorts, a long press picks, with multi picks and 제외 as there). This list holds no cards of
// its own: it keeps the picks (SortPopup's popups find it through LazyLoading_CardList.Ma9MListFor for their marks and button
// labels) and asks TeamInfo_Album to draw the page again. TeamInfo_Album adds it to its own GameObject, the bar's target, and
// forwards the bar's OnBtn_SubSort to it.
public class Ma9MAlbumFilter : LazyLoading_CardList
{
	public TeamInfo_Album Album;

	// The base sizes its lazy grid here; this list has none, only the saved picks (as LazyLoading_CardList.LoadFilters).
	protected override void Awake()
	{
		Ma9MSortMemory.CardList memory = Ma9MSortMemory.Cards(GetType().Name);
		if (memory.Saved)
		{
			selectedYear = memory.Year;
			selectedBall = memory.Ball;
			selectedPos = (ePos)memory.Pos;
			selectedTeam = memory.Team;
		}
	}

	protected override void Update()
	{
	}

	public override void FilteringReposition()
	{
		if (Album != null)
		{
			Album.Ma9MRefilter();
		}
	}

	public bool Ma9MMatches(TRRow_PlayerCard card)
	{
		return card != null && IsMatchSelectedValue(card, null);
	}
}
