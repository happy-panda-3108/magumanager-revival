using System;
using UnityEngine;

// Ma9M port (systems session, 2026-11-11; user 18:4x: "보유 카드가 많을 때 화면이 멈추는 이유는 무엇인지? 이걸 최적화 해서 버퍼링이 덜
// 걸리게 할 수는 없을까?", 19:1x test windows allowed): the card lists made every card's picture when the screen opened (244 cards:
// 계약관리 2.8 s, 선수 조합 2.8 s, AI tester NC 5회차), though the list shows 20 at a time and keeps the rest switched off. A list
// item holder now carries this, and the card picture is made the first time the holder is switched on (LazyLoading_CardList turns
// holders on as they scroll into view), or when the list needs it (EnsureMade).
public class Ma9MLazyCard : MonoBehaviour
{
	public Action<GameObject> Make;

	private bool m_made;

	public bool Made
	{
		get
		{
			return m_made;
		}
	}

	private void OnEnable()
	{
		EnsureMade();
	}

	public void EnsureMade()
	{
		if (m_made || Make == null)
		{
			return;
		}
		m_made = true;
		Action<GameObject> make = Make;
		Make = null;
		make(base.gameObject);
	}

	// A holder made switched off, with the picture to come.
	public static GameObject Holder(GameObject prefab, Action<GameObject> make)
	{
		GameObject holder = UnityEngine.Object.Instantiate(prefab) as GameObject;
		holder.SetActive(false);
		holder.transform.localPosition = new Vector3(3000f, 3000f, 0f);
		holder.transform.localScale = new Vector3(1f, 1f, 1f);
		holder.AddComponent<Ma9MLazyCard>().Make = make;
		return holder;
	}

	public static bool Pending(GameObject holder)
	{
		Ma9MLazyCard lazy = (holder != null) ? holder.GetComponent<Ma9MLazyCard>() : null;
		return lazy != null && !lazy.m_made;
	}

	public static void EnsureMade(GameObject holder)
	{
		Ma9MLazyCard lazy = (holder != null) ? holder.GetComponent<Ma9MLazyCard>() : null;
		if (lazy != null)
		{
			lazy.EnsureMade();
		}
	}
}
