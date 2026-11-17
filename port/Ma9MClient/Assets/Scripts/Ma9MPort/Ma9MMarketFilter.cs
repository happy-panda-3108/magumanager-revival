// Ma9M port (systems session, 2026-11-17; AI testers 두산 17, 쌍방울 60: 전력강화 > FA 시장 opened empty, the album's year filter
// ('18) applied to the season's list): the FA 시장 keeps its own filter picks (Ma9MSortMemory key "Ma9MMarketFilter", by the class
// name) apart from the album's 등급별 pages (Ma9MAlbumFilter); TeamInfo_Album adds this one in market mode, and the bar's button
// labels are scoped the same way (SortPopup.Ma9MLabelScope).
public class Ma9MMarketFilter : Ma9MAlbumFilter
{
}
