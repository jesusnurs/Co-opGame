using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class SeatInteractable : NetworkBehaviour, IInteractable
{
    [Serializable]
    private struct SeatSlot
    {
        [SerializeField] private string m_name;
        [SerializeField] private Transform m_attachPoint;

        public Transform AttachPoint => m_attachPoint;
    }

    [SerializeField] private SelectionOutline m_outline;
    [SerializeField] private List<SeatSlot> m_slots = new();

    private NetworkList<ulong> m_slotOccupants;

    private const ulong EmptyOccupantId = ulong.MaxValue;

    public int SlotCount => m_slots.Count;

    private void Awake()
    {
        m_slotOccupants = new NetworkList<ulong>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsServer)
        {
            SyncSlotStorageWithDefinition();
        }
    }

    public bool TryOccupySeatServer(ulong playerNetworkObjectId, out int seatSlotIndex)
    {
        seatSlotIndex = -1;

        if (IsServer == false || playerNetworkObjectId == EmptyOccupantId || m_slots.Count == 0)
        {
            return false;
        }

        SyncSlotStorageWithDefinition();
        RemoveInvalidOccupantsServer();

        if (TryFindOccupiedSeatIndex(playerNetworkObjectId, out seatSlotIndex))
        {
            return true;
        }

        for (int i = 0; i < m_slotOccupants.Count; i++)
        {
            if (IsSlotAvailable(i) == false)
            {
                continue;
            }

            m_slotOccupants[i] = playerNetworkObjectId;
            seatSlotIndex = i;
            return true;
        }

        return false;
    }

    public bool ReleaseSeatServer(ulong playerNetworkObjectId, out int releasedSlotIndex)
    {
        releasedSlotIndex = -1;

        if (IsServer == false || playerNetworkObjectId == EmptyOccupantId)
        {
            return false;
        }

        SyncSlotStorageWithDefinition();
        if (TryFindOccupiedSeatIndex(playerNetworkObjectId, out int seatSlotIndex) == false)
        {
            return false;
        }

        m_slotOccupants[seatSlotIndex] = EmptyOccupantId;
        releasedSlotIndex = seatSlotIndex;
        return true;
    }

    public Transform GetSeatAttachPoint(int seatSlotIndex)
    {
        if (seatSlotIndex < 0 || seatSlotIndex >= m_slots.Count)
        {
            return null;
        }

        return m_slots[seatSlotIndex].AttachPoint;
    }

    public void ToggleSelection(bool isSelected)
    {
        if (m_outline != null)
        {
            m_outline.ToggleOutline(isSelected);
        }
    }

    private void SyncSlotStorageWithDefinition()
    {
        if (m_slotOccupants == null)
        {
            return;
        }

        while (m_slotOccupants.Count < m_slots.Count)
        {
            m_slotOccupants.Add(EmptyOccupantId);
        }

        while (m_slotOccupants.Count > m_slots.Count)
        {
            m_slotOccupants.RemoveAt(m_slotOccupants.Count - 1);
        }
    }

    private void RemoveInvalidOccupantsServer()
    {
        if (IsServer == false || NetworkManager == null || NetworkManager.SpawnManager == null)
        {
            return;
        }

        for (int i = 0; i < m_slotOccupants.Count; i++)
        {
            ulong occupantId = m_slotOccupants[i];
            if (occupantId == EmptyOccupantId)
            {
                continue;
            }

            if (NetworkManager.SpawnManager.SpawnedObjects.ContainsKey(occupantId))
            {
                continue;
            }

            m_slotOccupants[i] = EmptyOccupantId;
        }
    }

    private bool IsSlotAvailable(int seatSlotIndex)
    {
        if (seatSlotIndex < 0 || seatSlotIndex >= m_slotOccupants.Count)
        {
            return false;
        }

        if (seatSlotIndex >= m_slots.Count)
        {
            return false;
        }

        if (m_slots[seatSlotIndex].AttachPoint == null)
        {
            return false;
        }

        return m_slotOccupants[seatSlotIndex] == EmptyOccupantId;
    }

    private bool TryFindOccupiedSeatIndex(ulong playerNetworkObjectId, out int seatSlotIndex)
    {
        for (int i = 0; i < m_slotOccupants.Count; i++)
        {
            if (m_slotOccupants[i] != playerNetworkObjectId)
            {
                continue;
            }

            seatSlotIndex = i;
            return true;
        }

        seatSlotIndex = -1;
        return false;
    }
}
