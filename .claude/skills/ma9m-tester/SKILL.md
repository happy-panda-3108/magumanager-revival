---
name: ma9m-tester
description: 마구매니저 AI 테스터의 손과 눈 — 창 없는 게임 클라이언트를 글로 읽고(ui·text·look), 글자로 누르고(tap), 반복 조작을 매크로(결과 받기·미션 보상·시범경기)로 한 번에, 숫자는 DB 읽기(state.py)로. AI 테스터 회차를 시작하거나 게임 화면을 조작할 때 쓴다.
---

# ma9m-tester

토큰을 아끼려고 만든 도구 모음이다(게임 주인 2026-11-19: "테스터가 실행될 때 사용되는 토큰 양만 줄이면 돼"). 규칙과 기록 방법은 `C:\Ma9MProject\tools\ai_tester\TESTER_BRIEF_5.md`에 있다.

## 순서
1. `state.py <번호>`로 숫자를 본다. MP, 순위, 확인 안 한 경기, 곧 끝나는 계약, 미션.
2. 장면이 `Scene_Unconfirmed`면 `results.ps1`을 돌린다.
3. 미션 보상은 `missions.ps1`로 받고, 시범경기는 `exhibition.ps1`로 한다.
4. 직접 볼 화면은 `look.ps1`로 읽고 `tap`으로 누른다. 그림은 겉모습을 판단할 때만 `shot <png> 480`으로 찍는다.
   - 생중계는 `watch.ps1 -Port <포트> -Seconds 180 [-ShotOn '번트|도루|투수 교체' -ShotDir <폴더> -ShotNo <번호>]`로 따라간다. 중계 글이 바뀔 때만 한 줄씩 나오고, 고른 글이 보이면 작은 캡처를 남긴다. 한 번 부를 때 3분씩 본다.
     - 앞에서(포그라운드) 돌린다. 도구 시간 제한을 `-Seconds`보다 30초 넉넉히 준다(예: -Seconds 540 → timeout 600000). 11-20부터 줄이 읽히는 대로 나온다.
     - 3D 중계 화면에는 도루·번트·투수 교체 글이 없다. tester_1120d부터 watch는 원격 `sns`(LIVE-SNS 기록)로 "1루주자 김실 : 도루로 2루까지 진루" 같은 플레이 줄만 내고, 화면은 3D에 그대로 둔다. 투구 한 개씩의 줄은 `-Pitches`를 줄 때만. 줄 앞에 [도루]·[번트]·[투수 교체]·[실책]이 붙는다.
     - tester_1120f부터 `-ShotOn`의 도루·번트·병살·실책·2루타·3루타·홈런은 그 장면이 시작될 때 "다음 장면: 도루 (플레이가 시작되면 캡처)"가 나오고, 공을 던지거나 친 순간과 1.2초 뒤를 찍는다(도루 시험: 주자가 뛰는 장면 + 2루 SAFE). 그 전 빌드에서는 줄이 나온 뒤에 찍어 다음 타자가 찍힌다.
     - LIVE-SNS 화면(중계 오른쪽 위 Btn_LiveSNS)을 열 필요는 없다. 열면 3D 장면 대신 글 화면이 찍힌다.
     - 로비 위 띠 "경기중"이 `tap`으로 안 잡히면 `click 459 25`로 누른다. `click 474 20`은 ← 단추 자리라 종료 메뉴가 뜬다(NC 16회차).
5. 리그 결과를 기다려야 하면 기다리지 않고 회차를 끝낸다. state.md를 고쳐 쓰고 마친다.

## 명령
- `ma9m_remote.exe <포트> ui | text [낱말] | tap <글자>[#n] | state | shot <png> 480 | zoom x y w h <png> | sns [from]`
  - `sns`(tester_1120d부터): 경기 중 플레이 기록을 "번호 글" 줄로. 첫 줄의 total=N을 다음에 `sns N`으로 넘기면 새 줄만 온다.
  - tester_1120c부터 `ui`의 카드 줄에 카드 이름이 붙는다("'22 이정후 9볼"). 카드를 눌러 열거나 캡처하지 않아도 누가 어디 있는지 안다.
  - 목록 위 정렬 단추(포지션·연도·구단·볼 등): 짧게 누르면 정렬(누를 때마다 순서가 뒤집힘), 꾹 누르면 거르기 창. 거르기를 켜면 단추 글이 고른 보기 이름("1군만", "NC", "FA")으로 바뀐다. 목록이 비면 거르기부터 의심하고, 꾹 눌러 [모두 해제](두산 17회차 "1군만", LG "FA").
  - tester_1120c부터 회색으로 꺼진 단추도 "(꺼짐)"으로 나온다(예: 계약관리 [계약연장]·[선수방출]은 카드를 고르기 전에 꺼짐). `tap`은 꺼진 단추를 누르지 않고 "꺼짐"이라고 답한다. 먼저 카드를 고르고 다시 `ui`.
- 매크로: `powershell -NoProfile -ExecutionPolicy Bypass -File C:\Ma9MProject\tools\ai_tester\macros\<look|results|missions|exhibition|watch>.ps1 -Port <포트> [...]`. exhibition은 판 사이에 리그 결과가 끼어들면 results를 먼저 돌리고 이어 간다(11-20).
- 자기 폴더에 .ps1을 만들 때 한글이 들어가면 PowerShell 5.1에서 깨진다. 한글은 매크로 인자로 넘기거나, 파일을 UTF-8 BOM으로 저장한다: `[IO.File]::WriteAllText(<경로>, <글>, (New-Object Text.UTF8Encoding $true))`.
- 숫자: `python -I C:\Ma9MProject\tools\ai_tester\state.py <1-6>` (실서버 DB를 읽기만 함)

## 화면 이름과 단추 (알아낸 것)
- 확인하지 않은 결과 `Scene_Unconfirmed`: "모두 확인"(Button_Skip) → 창 "확인" → "메인로비로"(Button_MainLobby, 확인 전에는 꺼짐). 결과를 막 받은 뒤 몇 초는 단추가 안 잡힌다(숫자 올라가는 중). "모두 확인" 한 번이면 목록 전체(31경기 등)가 확인된다. `text`는 목록에서 화면에 보이는 6줄만 읽으므로, 다 확인됐는지는 줄 수가 아니라 `state.py`나 MP로 본다(11-20 자급자족: 6경기만 확인된 줄 앎 → 서버 기록에서 31경기 모두 확인).
- 로비 `Scene_MainLobby`: 팀 관리 · 기록실 · 전력강화 · 도전과제 · 대회 · 상점 · 인벤토리 · 커뮤니티 · 설정. 위 띠 ←는 BtnBack이다. 시즌 결산 창은 "다음"/"건너뛰기"로 넘긴다.
- 도전과제 `Scene_ChallengeMission`: "보상받기" 여섯 개(#1~#6), 나가기는 BtnBack.
- 시범경기 `Scene_ExhibitionGame` → 중계 `Scene_BroadCast`: SKIP은 Btn_Chat → "예" → 결과 창 "확인" → 스코어보드 "닫기"(바로 로비).
- 매크로가 멈추면 `look.ps1`로 화면을 보고 손으로 이어 간다. 새로 알아낸 단추는 RUN_NOTE.md를 통해 코디네이터에게 알린다(이 파일은 코디네이터가 고친다).
