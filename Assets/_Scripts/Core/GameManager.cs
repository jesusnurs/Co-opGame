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

public enum ShiftState : byte
{
    Lobby = 0,
    InProgress = 1,
    Victory = 2,
    Defeat = 3
}

public class GameManager : NetworkBehaviour
{
    private const string RelayConnectionType = "udp";

    public static GameManager Instance { get; private set; }

    [SerializeField]
    private MultiplayerUI m_multiplayerUI;
    [SerializeField]
    private GameObject m_playerPrefab;

    [SerializeField]
    private List<ResourcePallet> m_pallets;

    [Header("Shift Loop")]
    [SerializeField, Min(15f)]
    private float m_shiftDurationSeconds = 180f;

    [SerializeField, Min(1)]
    private int m_maxRelayConnections = 7;

    private bool m_isConnectionTransitionInProgress;
    private bool m_isShiftRestartInProgress;
    private readonly NetworkVariable<ShiftState> m_shiftState = new(ShiftState.Lobby);
    private readonly NetworkVariable<int> m_currentQuota = new(0);
    private readonly NetworkVariable<int> m_requiredQuota = new(0);
    private readonly NetworkVariable<double> m_shiftEndTime = new(0d);

    public event Action OnShiftDataChanged;

    public ShiftState CurrentShiftState => IsSpawned ? m_shiftState.Value : ShiftState.Lobby;
    public int CurrentQuota => IsSpawned ? m_currentQuota.Value : 0;
    public int RequiredQuota => IsSpawned ? m_requiredQuota.Value : 0;
    public bool IsShiftActive => CurrentShiftState == ShiftState.InProgress;
    public bool IsGameplayInputBlocked => CurrentShiftState != ShiftState.InProgress;
    public bool IsLocalHost => NetworkManager != null && NetworkManager.IsHost;
    public bool IsShiftRestartInProgress => m_isShiftRestartInProgress;
    public bool CanLocalRestartShift =>
        CurrentShiftState is ShiftState.Victory or ShiftState.Defeat &&
        IsLocalHost &&
        m_isShiftRestartInProgress == false;

    public float RemainingShiftTimeSeconds
    {
        get
        {
            if (CurrentShiftState != ShiftState.InProgress || NetworkManager == null)
            {
                return 0f;
            }

            double remainingTime = m_shiftEndTime.Value - NetworkManager.ServerTime.Time;
            return Mathf.Max(0f, (float)remainingTime);
        }
    }

    private void Awake()
    {
        Instance = this;
    }

    private void Start()
    {
        if (m_multiplayerUI != null)
        {
            m_multiplayerUI.OnStartHostRequested += StartHost;
            m_multiplayerUI.OnStartClientRequested += StartClient;
            m_multiplayerUI.OnDisconnectRequested += DisconnectClient;
        }

        SyncGameplayMenuForActiveSession();
    }

    public override void OnDestroy()
    {
        if (m_multiplayerUI != null)
        {
            m_multiplayerUI.OnStartHostRequested -= StartHost;
            m_multiplayerUI.OnStartClientRequested -= StartClient;
            m_multiplayerUI.OnDisconnectRequested -= DisconnectClient;
        }

        if (Instance == this)
        {
            Instance = null;
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        m_shiftState.OnValueChanged += HandleShiftStateChanged;
        m_currentQuota.OnValueChanged += HandleShiftQuotaChanged;
        m_requiredQuota.OnValueChanged += HandleShiftQuotaChanged;
        m_shiftEndTime.OnValueChanged += HandleShiftEndTimeChanged;

        if (IsServer)
        {
            NetworkManager.OnClientConnectedCallback += SpawnPlayer;
            NetworkManager.SceneManager.OnLoadEventCompleted += HandleSceneLoadCompleted;
            RegisterPalletCallbacks();
            StartShift();
        }

        NotifyShiftDataChanged();
    }

    private void HandleSceneLoadCompleted(string sceneName, 
        LoadSceneMode loadSceneMode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        foreach (ulong clientId in clientsCompleted)
        {
            SpawnPlayer(clientId);
        }

        PrepareGameplayUiClientRpc();
    }

    private void Update()
    {
        if (IsServer == false || CurrentShiftState != ShiftState.InProgress)
        {
            return;
        }

        if (RemainingShiftTimeSeconds <= 0f)
        {
            ResolveShift(ShiftState.Defeat);
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
        m_shiftState.OnValueChanged -= HandleShiftStateChanged;
        m_currentQuota.OnValueChanged -= HandleShiftQuotaChanged;
        m_requiredQuota.OnValueChanged -= HandleShiftQuotaChanged;
        m_shiftEndTime.OnValueChanged -= HandleShiftEndTimeChanged;

        if (IsServer)
        {
            NetworkManager.OnClientConnectedCallback -= SpawnPlayer;
            NetworkManager.SceneManager.OnLoadEventCompleted -= HandleSceneLoadCompleted;
            UnregisterPalletCallbacks();
        }
       
        base.OnNetworkDespawn();
        NotifyShiftDataChanged();
    }

    private void DisconnectClient()
    {
        if (m_multiplayerUI == null)
        {
            return;
        }

        m_isShiftRestartInProgress = false;
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

    public bool TryRestartShift()
    {
        if (CanLocalRestartShift == false)
        {
            return false;
        }

        if (NetworkManager.SceneManager == null)
        {
            Debug.LogError("Network scene manager is not available.");
            return false;
        }

        m_isShiftRestartInProgress = true;
        NotifyShiftDataChanged();
        NetworkManager.SceneManager.LoadScene(SceneManager.GetActiveScene().name, LoadSceneMode.Single);
        return true;
    }

    private void StartShift()
    {
        if (IsServer == false || NetworkManager == null)
        {
            return;
        }

        m_isShiftRestartInProgress = false;
        m_requiredQuota.Value = CalculateRequiredQuota();
        m_currentQuota.Value = CalculateCurrentQuota();
        m_shiftEndTime.Value = NetworkManager.ServerTime.Time + Mathf.Max(1f, m_shiftDurationSeconds);
        m_shiftState.Value = ShiftState.InProgress;

        if (m_requiredQuota.Value > 0 && m_currentQuota.Value >= m_requiredQuota.Value)
        {
            ResolveShift(ShiftState.Victory);
        }
    }

    private void RegisterPalletCallbacks()
    {
        if (m_pallets == null)
        {
            return;
        }

        foreach (ResourcePallet pallet in m_pallets)
        {
            if (pallet == null)
            {
                continue;
            }

            pallet.OnStackCountChanged += HandlePalletStackCountChanged;
        }
    }

    private void UnregisterPalletCallbacks()
    {
        if (m_pallets == null)
        {
            return;
        }

        foreach (ResourcePallet pallet in m_pallets)
        {
            if (pallet == null)
            {
                continue;
            }

            pallet.OnStackCountChanged -= HandlePalletStackCountChanged;
        }
    }

    private void HandlePalletStackCountChanged(int previousValue, int newValue)
    {
        if (IsServer == false || CurrentShiftState != ShiftState.InProgress)
        {
            return;
        }

        RefreshQuotaProgress();
    }

    private void RefreshQuotaProgress()
    {
        int currentQuota = CalculateCurrentQuota();
        if (m_currentQuota.Value != currentQuota)
        {
            m_currentQuota.Value = currentQuota;
        }

        if (m_requiredQuota.Value > 0 && currentQuota >= m_requiredQuota.Value)
        {
            ResolveShift(ShiftState.Victory);
        }
    }

    private int CalculateCurrentQuota()
    {
        if (m_pallets == null)
        {
            return 0;
        }

        int total = 0;
        foreach (ResourcePallet pallet in m_pallets)
        {
            if (pallet == null)
            {
                continue;
            }

            total += Mathf.Max(0, pallet.StackedResoruces);
        }

        return total;
    }

    private int CalculateRequiredQuota()
    {
        if (m_pallets == null)
        {
            return 0;
        }

        int total = 0;
        foreach (ResourcePallet pallet in m_pallets)
        {
            if (pallet == null)
            {
                continue;
            }

            total += Mathf.Max(0, pallet.Capacity);
        }

        return total;
    }

    private void ResolveShift(ShiftState finalState)
    {
        if (IsServer == false || CurrentShiftState != ShiftState.InProgress)
        {
            return;
        }

        if (finalState is not ShiftState.Victory and not ShiftState.Defeat)
        {
            return;
        }

        m_shiftState.Value = finalState;
    }

    private void HandleShiftStateChanged(ShiftState previousValue, ShiftState newValue)
    {
        NotifyShiftDataChanged();
    }

    private void HandleShiftQuotaChanged(int previousValue, int newValue)
    {
        NotifyShiftDataChanged();
    }

    private void HandleShiftEndTimeChanged(double previousValue, double newValue)
    {
        NotifyShiftDataChanged();
    }

    private void NotifyShiftDataChanged()
    {
        OnShiftDataChanged?.Invoke();
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void PrepareGameplayUiClientRpc()
    {
        SyncGameplayMenuForActiveSession();
    }

    private void SyncGameplayMenuForActiveSession()
    {
        if (m_multiplayerUI == null || NetworkManager == null || NetworkManager.IsListening == false)
        {
            return;
        }

        m_multiplayerUI.DisableButtons();
        m_multiplayerUI.SetMenuVisibility(false);
    }
}
