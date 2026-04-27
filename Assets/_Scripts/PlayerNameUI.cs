using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

public class PlayerNameUI : NetworkBehaviour
{
    private static readonly Dictionary<ulong, string> s_playerNamesByClientId = new Dictionary<ulong, string>();

    [SerializeField]
    private UIDocument m_uiPlayerNameDocument;

    private Label m_playerNameLabel;

    private NetworkVariable<FixedString32Bytes> m_playerName = new NetworkVariable<FixedString32Bytes>(
        string.Empty,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public static bool TryGetRegisteredName(ulong clientId, out string playerName)
    {
        if (s_playerNamesByClientId.TryGetValue(clientId, out playerName))
        {
            return !string.IsNullOrWhiteSpace(playerName);
        }

        playerName = string.Empty;
        return false;
    }

    private void Awake()
    {
        m_playerNameLabel = m_uiPlayerNameDocument.rootVisualElement.Q<Label>("NameLabel");
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        m_playerName.OnValueChanged += HandlePlayerNameChanged;

        if (IsServer && m_playerName.Value.Length == 0)
        {
            m_playerName.Value = MultiplayerSessionSettings.ResolvePlayerName(string.Empty, OwnerClientId);
        }

        if (IsOwner)
        {
            SubmitPlayerNameServerRpc(MultiplayerSessionSettings.LoadPlayerName());
        }

        string currentName = m_playerName.Value.ToString();
        m_playerNameLabel.text = currentName;
        UpdateRegisteredPlayerName(currentName);
    }

    public override void OnNetworkDespawn()
    {
        m_playerName.OnValueChanged -= HandlePlayerNameChanged;
        s_playerNamesByClientId.Remove(OwnerClientId);
        base.OnNetworkDespawn();
    }

    private void HandlePlayerNameChanged(FixedString32Bytes previousValue, FixedString32Bytes newValue)
    {
        string updatedName = newValue.ToString();
        m_playerNameLabel.text = updatedName;
        UpdateRegisteredPlayerName(updatedName);
    }

    [ServerRpc]
    private void SubmitPlayerNameServerRpc(string requestedName, ServerRpcParams serverRpcParams = default)
    {
        ulong senderClientId = serverRpcParams.Receive.SenderClientId;
        m_playerName.Value = MultiplayerSessionSettings.ResolvePlayerName(requestedName, senderClientId);
    }

    private void UpdateRegisteredPlayerName(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            s_playerNamesByClientId.Remove(OwnerClientId);
            return;
        }

        s_playerNamesByClientId[OwnerClientId] = playerName;
    }
}
