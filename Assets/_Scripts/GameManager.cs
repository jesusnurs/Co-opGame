using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GameManager : NetworkBehaviour
{
    private const string RelayConnectionType = "udp";

    [SerializeField]
    private MultiplayerUI m_multiplayerUI;
    [SerializeField]
    private GameObject m_playerPrefab;

    [SerializeField]
    private List<ResourcePallet> m_pallets;

    [SerializeField, Min(1)]
    private int m_maxRelayConnections = 7;

    private bool m_isConnectionTransitionInProgress;

    private void Start()
    {
        if (m_multiplayerUI != null)
        {
            m_multiplayerUI.OnStartHostRequested += StartHost;
            m_multiplayerUI.OnStartClientRequested += StartClient;
            m_multiplayerUI.OnDisconnectRequested += DisconnectClient;
        }
    }

    public override void OnDestroy()
    {
        if (m_multiplayerUI != null)
        {
            m_multiplayerUI.OnStartHostRequested -= StartHost;
            m_multiplayerUI.OnStartClientRequested -= StartClient;
            m_multiplayerUI.OnDisconnectRequested -= DisconnectClient;
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsServer == false)
            return;
        NetworkManager.OnClientConnectedCallback += SpawnPlayer;
        NetworkManager.SceneManager.OnLoadEventCompleted += HandleSceneLoadCompleted;
        foreach (ResourcePallet pallet in m_pallets)
        {
            pallet.OnPalletFilled += CheckWinCondition;
        }
    }

    private void CheckWinCondition()
    {
        int points = 0;
        foreach (ResourcePallet pallet in m_pallets)
        {
            points += pallet.StackedResoruces;
        }

        if (points >= m_pallets.Count * 3)
        {
            NetworkManager.SceneManager.LoadScene(
                SceneManager.GetActiveScene().name, LoadSceneMode.Single);
        }
    }

    private void HandleSceneLoadCompleted(string sceneName, 
        LoadSceneMode loadSceneMode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        foreach (ulong clientId in clientsCompleted)
        {
            SpawnPlayer(clientId);
        }
    }

    private void SpawnPlayer(ulong clientID)
    {
        if (NetworkManager.ConnectedClients[clientID].PlayerObject != null)
            return;
        GameObject player = Instantiate(m_playerPrefab);
        player.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientID, true);
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            NetworkManager.OnClientConnectedCallback -= SpawnPlayer;
            NetworkManager.SceneManager.OnLoadEventCompleted -= HandleSceneLoadCompleted;
            foreach (ResourcePallet pallet in m_pallets)
            {
                pallet.OnPalletFilled -= CheckWinCondition;
            }
        }
       
        base.OnNetworkDespawn();
    }

    private void DisconnectClient()
    {
        if (m_multiplayerUI == null)
        {
            return;
        }

        m_multiplayerUI.EnableButtons();
        m_multiplayerUI.SetMenuVisibility(true);
        m_multiplayerUI.HideHostConnectionInfo();
        m_isConnectionTransitionInProgress = false;

        if (Unity.Netcode.NetworkManager.Singleton != null && Unity.Netcode.NetworkManager.Singleton.IsListening)
        {
            Unity.Netcode.NetworkManager.Singleton.Shutdown();
        }
    }

    private async void StartClient()
    {
        if (m_multiplayerUI == null || m_isConnectionTransitionInProgress)
        {
            return;
        }

        if (TryGetUnityTransport(out UnityTransport unityTransport) == false)
        {
            return;
        }

        if (MultiplayerSessionSettings.TryParseRelayJoinCode(m_multiplayerUI.ConnectionCode, out string relayJoinCode) == false)
        {
            Debug.LogError($"Invalid relay join code. Enter {MultiplayerSessionSettings.RelayJoinCodeLength} letters/digits.");
            return;
        }

        m_isConnectionTransitionInProgress = true;
        m_multiplayerUI.DisableButtons();
        m_multiplayerUI.HideHostConnectionInfo();
        m_multiplayerUI.SetMenuVisibility(false);

        MultiplayerSessionSettings.SavePlayerName(m_multiplayerUI.PlayerName);
        MultiplayerSessionSettings.SaveConnectionCode(relayJoinCode);
        m_multiplayerUI.SetConnectionCode(relayJoinCode);

        try
        {
            await EnsureUnityServicesSignedInAsync();

            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(relayJoinCode);
            RelayServerData relayServerData = joinAllocation.ToRelayServerData(RelayConnectionType);
            unityTransport.SetRelayServerData(relayServerData);

            Debug.Log($"Joining relay session by code: {relayJoinCode}.");
            if (Unity.Netcode.NetworkManager.Singleton.StartClient() == false)
            {
                throw new InvalidOperationException("Unity Netcode failed to start client.");
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"Failed to join relay session with code {relayJoinCode}. {exception.Message}");
            m_multiplayerUI.SetMenuVisibility(true);
            m_multiplayerUI.EnableButtons();
        }
        finally
        {
            m_isConnectionTransitionInProgress = false;
        }
    }

    private async void StartHost()
    {
        if (m_multiplayerUI == null || m_isConnectionTransitionInProgress)
        {
            return;
        }

        if (TryGetUnityTransport(out UnityTransport unityTransport) == false)
        {
            return;
        }

        m_isConnectionTransitionInProgress = true;
        m_multiplayerUI.SetMenuVisibility(false);
        m_multiplayerUI.DisableButtons();
        m_multiplayerUI.HideHostConnectionInfo();

        try
        {
            MultiplayerSessionSettings.SavePlayerName(m_multiplayerUI.PlayerName);
            await EnsureUnityServicesSignedInAsync();

            int maxConnections = Mathf.Clamp(m_maxRelayConnections, 1, 100);
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
            string relayJoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

            RelayServerData relayServerData = allocation.ToRelayServerData(RelayConnectionType);
            unityTransport.SetRelayServerData(relayServerData);

            if (Unity.Netcode.NetworkManager.Singleton.StartHost() == false)
            {
                throw new InvalidOperationException("Unity Netcode failed to start host.");
            }

            m_multiplayerUI.SetConnectionCode(relayJoinCode);
            MultiplayerSessionSettings.SaveConnectionCode(relayJoinCode);
            m_multiplayerUI.ShowHostConnectionInfo(relayJoinCode);
            Debug.Log($"Relay host started. Join code: {relayJoinCode}");
        }
        catch (Exception exception)
        {
            Debug.LogError($"Failed to start relay host. {exception.Message}");
            m_multiplayerUI.SetMenuVisibility(true);
            m_multiplayerUI.EnableButtons();
            m_multiplayerUI.HideHostConnectionInfo();
        }
        finally
        {
            m_isConnectionTransitionInProgress = false;
        }
    }

    private static async Task EnsureUnityServicesSignedInAsync()
    {
        if (string.IsNullOrWhiteSpace(Application.cloudProjectId))
        {
            throw new InvalidOperationException("Unity Cloud Project ID is not configured. Link this project to Unity Gaming Services first.");
        }

        if (UnityServices.State == ServicesInitializationState.Initializing)
        {
            while (UnityServices.State == ServicesInitializationState.Initializing)
            {
                await Task.Yield();
            }
        }

        if (UnityServices.State == ServicesInitializationState.Uninitialized)
        {
            await UnityServices.InitializeAsync();
        }

        if (AuthenticationService.Instance.IsSignedIn == false)
        {
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
            Debug.Log($"Signed in to Unity Services as player {AuthenticationService.Instance.PlayerId}.");
        }
    }

    private static bool TryGetUnityTransport(out UnityTransport unityTransport)
    {
        unityTransport = null;

        if (Unity.Netcode.NetworkManager.Singleton == null)
        {
            Debug.LogError("NetworkManager.Singleton is not available.");
            return false;
        }

        unityTransport = Unity.Netcode.NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;
        if (unityTransport == null)
        {
            Debug.LogError("Network transport is not UnityTransport.");
            return false;
        }

        return true;
    }
}
