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
5. 리그 결과를 기다려야 하면 기다리지 않고 회차를 끝낸다. state.md를 고쳐 쓰고 마친다.

## 명령
- `ma9m_remote.exe <포트> ui | text [낱말] | tap <글자>[#n] | state | shot <png> 480 | zoom x y w h <png>`
- 매크로: `powershell -NoProfile -ExecutionPolicy Bypass -File C:\Ma9MProject\tools\ai_tester\macros\<look|results|missions|exhibition>.ps1 -Port <포트> [...]`
- 숫자: `python -I C:\Ma9MProject\tools\ai_tester\state.py <1-6>` (실서버 DB를 읽기만 함)

## 화면 이름과 단추 (알아낸 것)
- 확인하지 않은 결과 `Scene_Unconfirmed`: "모두 확인"(Button_Skip) → 창 "확인" → "메인로비로"(Button_MainLobby, 확인 전에는 꺼짐). 결과를 막 받은 뒤 몇 초는 단추가 안 잡힌다(숫자 올라가는 중).
- 로비 `Scene_MainLobby`: 팀 관리 · 기록실 · 전력강화 · 도전과제 · 대회 · 상점 · 인벤토리 · 커뮤니티 · 설정. 위 띠 ←는 BtnBack이다. 시즌 결산 창은 "다음"/"건너뛰기"로 넘긴다.
- 도전과제 `Scene_ChallengeMission`: "보상받기" 여섯 개(#1~#6), 나가기는 BtnBack.
- 시범경기 `Scene_ExhibitionGame` → 중계 `Scene_BroadCast`: SKIP은 Btn_Chat → "예" → 결과 창 "확인" → 스코어보드 "닫기"(바로 로비).
- 매크로가 멈추면 `look.ps1`로 화면을 보고 손으로 이어 간다. 새로 알아낸 단추는 RUN_NOTE.md를 통해 코디네이터에게 알린다(이 파일은 코디네이터가 고친다).
