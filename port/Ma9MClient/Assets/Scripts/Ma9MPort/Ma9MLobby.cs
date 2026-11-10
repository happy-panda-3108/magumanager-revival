// Ma9M port: lobby features this version leaves out. Their code and data stay in place; only the
// buttons are hidden or do nothing, so turning a feature back on is a one-line change here.
public static class Ma9MLobby
{
	// Locker room (마구도리 uniform shop).
	public const bool ShowLockerRoom = false;

	// EVENT (event list and the daily attendance popup) and INVITED (Kakao invites).
	public const bool ShowEvent = false;

	public const bool ShowInvite = false;

	// BUBBLE (chat bubble toggle): there is no chat server; the button only shows its press.
	public const bool ChatDisabled = true;

	// 기록실 > 구단정보: the server answers only its album page so far (team info, records and rank
	// points come later from the systems session). Until then 구단정보 opens on the album alone
	// (TeamInfo_Main, DragPage.minPage), and the other clubs' team info (lobby next-match box,
	// league ranking chart) does nothing.
	public const bool TeamInfoPages = false;
}
