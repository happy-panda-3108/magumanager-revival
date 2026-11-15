using System;
using System.Collections.Generic;
using Ma9MPackets;
using NetworkLibrary;
using UnityEngine;

// Ma9M port (systems session, 2026-11-11; user 18:4x: "계약 관리 기능 중에 1군 일괄 연장 … 나중에 보너스 18스탯까지 찍고 나면 순수 계약
// 연장만 해야하는데 1군 일괄 연장해서 신경을 덜 쓸 수 있으면 좋을 것 같음"): renews a list of cards one after another with the
// ordinary request (PKS_CS_EXTEND_CONTRACT, no bonus choice: the server takes none once six bonus renewals are done), so the
// server needs nothing new. Lives on the 계약관리 screen (ContractManage_Main adds it); a reply that fails, or none within
// ReplySeconds, stops the run.
public class Ma9MContractBulk : MonoBehaviour
{
	private const float ReplySeconds = 10f;

	private readonly Queue<PlayerData_Contract> m_queue = new Queue<PlayerData_Contract>();

	private PlayerData_Contract m_current;

	private float m_sentAt;

	private int m_done;

	private long m_paid;

	private int m_cost;

	private bool m_listening;

	private Action<PlayerData_Contract> m_onCard;

	private Action<int, long, bool> m_onDone;

	public bool Busy
	{
		get
		{
			return m_current != null || m_queue.Count > 0;
		}
	}

	public void Run(List<PlayerData_Contract> cards, Action<PlayerData_Contract> onCard, Action<int, long, bool> onDone)
	{
		if (Busy || cards == null || cards.Count == 0)
		{
			return;
		}
		m_queue.Clear();
		foreach (PlayerData_Contract card in cards)
		{
			m_queue.Enqueue(card);
		}
		m_done = 0;
		m_paid = 0;
		m_onCard = onCard;
		m_onDone = onDone;
		if (!m_listening)
		{
			NetworkConnector.AddProcessCallBack(OnPacket);
			m_listening = true;
		}
		SceneChanger.ShowLoadingTransparent();
		Next();
	}

	private void Next()
	{
		if (m_queue.Count == 0)
		{
			Finish(true);
			return;
		}
		m_current = m_queue.Dequeue();
		m_cost = TableReader.ContractCost.Ma9MCost(m_current.ui_card.index.n, (byte)m_current.contract_count, m_current.card_info.ball, m_current.ui_card.remain_contract.n);
		Ma9MPacket.PKS_CS_EXTEND_CONTRACT packet = new Ma9MPacket.PKS_CS_EXTEND_CONTRACT();
		packet.target_card.n = m_current.ui_card.index.n;
		packet.bonus_stat.n = byte.MaxValue;
		m_sentAt = Time.realtimeSinceStartup;
		NetworkConnector.SendPacket(packet);
	}

	private void OnPacket(Packet.PKS_PACKET packet)
	{
		if (m_current == null || (Ma9MPacket.SUP)packet.GetSUP() != Ma9MPacket.SUP.SUP_LOBBY || (Ma9MPacket.SUB)packet.GetSUB() != Ma9MPacket.SUB.PK_SC_EXTEND_CONTRACT)
		{
			return;
		}
		Ma9MPacket.PKS_SC_EXTEND_CONTRACT reply = (Ma9MPacket.PKS_SC_EXTEND_CONTRACT)packet;
		if (reply.result.n != 0)
		{
			Finish(false);
			return;
		}
		Common_TopMenu.SetCurrentMP(reply.cur_point.n);
		m_done++;
		m_paid += m_cost;
		PlayerData_Contract card = m_current;
		m_current = null;
		if (m_onCard != null)
		{
			m_onCard(card);
		}
		Next();
	}

	private void Update()
	{
		if (m_current != null && Time.realtimeSinceStartup - m_sentAt > ReplySeconds)
		{
			Debug.Log("[Ma9MContractBulk] no reply for card " + m_current.ui_card.index.n + "; stopping after " + m_done);
			Finish(false);
		}
	}

	private void Finish(bool ok)
	{
		m_queue.Clear();
		m_current = null;
		SceneChanger.HideLoadingTransparent();
		Action<int, long, bool> done = m_onDone;
		m_onDone = null;
		m_onCard = null;
		if (done != null)
		{
			done(m_done, m_paid, ok);
		}
	}

	private void OnDestroy()
	{
		if (m_listening)
		{
			NetworkConnector.RemoveProcessCallBack(OnPacket);
			m_listening = false;
		}
	}
}
