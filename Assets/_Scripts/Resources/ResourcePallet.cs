using System;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

public class ResourcePallet : NetworkBehaviour, IInteractable
{
    [SerializeField] private SelectionOutline m_selectionOutline;

    [SerializeField]
    private List<ComponentController> m_componentControllers;

    [SerializeField]
    private ObjectType m_acceptedObjectType;

    private NetworkVariable<int> m_stackedResources = new(0);
    public int StackedResoruces => m_stackedResources.Value;
    public int Capacity => m_componentControllers?.Count ?? 0;
    public event Action OnPalletFilled;
    public event Action<int, int> OnStackCountChanged;

    [SerializeField] private ItemsAudio m_itemsAudio;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_stackedResources.OnValueChanged += HandleStackCountChanged;
        ApplyStackVisuals(m_stackedResources.Value);
    }

    public override void OnNetworkDespawn()
    {
        m_stackedResources.OnValueChanged -= HandleStackCountChanged;
        base.OnNetworkDespawn();
    }

    public bool Interact(ObjectType objectType)
    {
        if(IsServer == false)
            return false;
        if (GameManager.Instance != null && GameManager.Instance.IsShiftActive == false)
            return false;
        if(objectType != m_acceptedObjectType)
            return false;
        if(m_stackedResources.Value >= Capacity)
            return false;
        PlayerAudioClientRpc();
        m_stackedResources.Value++;
        return true;
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void PlayerAudioClientRpc()
    {
        if (m_itemsAudio != null)
            m_itemsAudio.PlaySound();
    }
    public void ToggleSelection(bool isSelected)
    {
        if(m_selectionOutline != null)
            m_selectionOutline.ToggleOutline(isSelected);
    }

    private void HandleStackCountChanged(int previousValue, int newValue)
    {
        ApplyStackVisuals(newValue);
        OnStackCountChanged?.Invoke(previousValue, newValue);

        if (previousValue < Capacity && newValue >= Capacity && Capacity > 0)
        {
            OnPalletFilled?.Invoke();
        }
    }

    private void ApplyStackVisuals(int stackedCount)
    {
        if (m_componentControllers == null)
        {
            return;
        }

        int clampedCount = Mathf.Clamp(stackedCount, 0, Capacity);
        for (int i = 0; i < m_componentControllers.Count; i++)
        {
            if (m_componentControllers[i] == null)
            {
                continue;
            }

            m_componentControllers[i].SetEnabled(i < clampedCount);
        }
    }
}
