using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

/// <summary>
/// Helper script that exposes UI Document button clicks as C# events that can be listened to 
/// from the GameManager script to trigger the start host / client and disconnect actions.
/// </summary>
public class MultiplayerUI : MonoBehaviour
{
    private const string HostButtonName = "ButtonHost";
    private const string ClientButtonName = "ButtonClient";
    private const string DisconnectButtonName = "ButtonDisconnect";
    private const string PlayerNameFieldName = "PlayerNameField";
    private const string ConnectionCodeFieldName = "ConnectionCodeField";
    private const string HostInfoLabelName = "HostInfoLabel";

    [SerializeField]
    private UIDocument m_uiDocument;
    
    private Button m_hostButton;
    private Button m_clientButton;
    private Button m_clientDisconnect;
    private TextField m_playerNameField;
    private TextField m_connectionCodeField;
    private Label m_hostInfoLabel;
    private VisualElement m_root;
    private bool m_isMenuVisible;

    public event Action OnStartHostRequested;
    public event Action OnStartClientRequested;
    public event Action OnDisconnectRequested;

    public string PlayerName => m_playerNameField?.value ?? string.Empty;
    public string ConnectionCode => m_connectionCodeField?.value ?? string.Empty;

    private void Awake()
    {
        VisualElement root = m_uiDocument.rootVisualElement;
        m_root = root;
        m_hostButton = root.Q<Button>(HostButtonName);
        m_clientButton = root.Q<Button>(ClientButtonName);
        m_clientDisconnect = root.Q<Button>(DisconnectButtonName);
        m_playerNameField = root.Q<TextField>(PlayerNameFieldName);
        m_connectionCodeField = root.Q<TextField>(ConnectionCodeFieldName);
        m_hostInfoLabel = root.Q<Label>(HostInfoLabelName);
    }

    private void Start()
    {
        if (m_hostButton == null || m_clientButton == null || m_clientDisconnect == null)
        {
            Debug.LogError("Multiplayer UI buttons were not found in UXML.");
            return;
        }

        InitializeInputFields();
        m_hostButton.clicked += HandleStartHostClicked;
        m_clientButton.clicked += HandleStartClientClicked;
        m_clientDisconnect.clicked += HandleDisconnectClicked;

        bool isActiveSession = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        if (isActiveSession)
        {
            DisableButtons();
            SetMenuVisibility(false);
        }
        else
        {
            EnableButtons();
            HideHostConnectionInfo();
            SetMenuVisibility(true);
        }
    }

    private void OnDestroy()
    {
        if (m_hostButton != null)
        {
            m_hostButton.clicked -= HandleStartHostClicked;
        }

        if (m_clientButton != null)
        {
            m_clientButton.clicked -= HandleStartClientClicked;
        }

        if (m_clientDisconnect != null)
        {
            m_clientDisconnect.clicked -= HandleDisconnectClicked;
        }

        if (m_playerNameField != null)
        {
            m_playerNameField.UnregisterValueChangedCallback(HandlePlayerNameChanged);
        }

        if (m_connectionCodeField != null)
        {
            m_connectionCodeField.UnregisterValueChangedCallback(HandleConnectionCodeChanged);
        }
    }

    private void Update()
    {
        if (GameplayTextInputBlocker.IsTyping)
        {
            return;
        }

        if (Keyboard.current == null || Keyboard.current.escapeKey.wasPressedThisFrame == false)
        {
            return;
        }

        bool canToggle =
            m_isMenuVisible == false ||
            (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening);

        if (canToggle == false)
        {
            return;
        }

        SetMenuVisibility(!m_isMenuVisible);
    }

    public void ShowHostConnectionInfo(string shareCode)
    {
        if (m_hostInfoLabel == null)
        {
            return;
        }

        m_hostInfoLabel.text = $"Relay Code: {shareCode}";
        m_hostInfoLabel.style.display = DisplayStyle.Flex;
    }

    public void SetConnectionCode(string connectionCode)
    {
        if (m_connectionCodeField == null)
        {
            return;
        }

        m_connectionCodeField.SetValueWithoutNotify(connectionCode ?? string.Empty);
    }

    public void HideHostConnectionInfo()
    {
        if (m_hostInfoLabel == null)
        {
            return;
        }

        m_hostInfoLabel.style.display = DisplayStyle.None;
        m_hostInfoLabel.text = string.Empty;
    }

    public void SetMenuVisibility(bool isVisible)
    {
        m_isMenuVisible = isVisible;

        if (m_root != null)
        {
            m_root.style.display = isVisible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        GameplayMenuState.SetMenuOpen(isVisible);
    }

    private void InitializeInputFields()
    {
        if (m_playerNameField != null)
        {
            m_playerNameField.maxLength = MultiplayerSessionSettings.MaxPlayerNameLength;
            m_playerNameField.value = MultiplayerSessionSettings.LoadPlayerName();
            m_playerNameField.RegisterValueChangedCallback(HandlePlayerNameChanged);
        }

        if (m_connectionCodeField != null)
        {
            m_connectionCodeField.maxLength = MultiplayerSessionSettings.RelayJoinCodeLength;
            m_connectionCodeField.value = MultiplayerSessionSettings.LoadConnectionCode();
            m_connectionCodeField.RegisterValueChangedCallback(HandleConnectionCodeChanged);
        }
    }

    private void HandlePlayerNameChanged(ChangeEvent<string> evt)
    {
        MultiplayerSessionSettings.SavePlayerName(evt.newValue);
    }

    private void HandleConnectionCodeChanged(ChangeEvent<string> evt)
    {
        string normalizedCode = MultiplayerSessionSettings.NormalizeConnectionCode(evt.newValue);
        if (m_connectionCodeField != null &&
            string.Equals(normalizedCode, evt.newValue, StringComparison.Ordinal) == false)
        {
            m_connectionCodeField.SetValueWithoutNotify(normalizedCode);
        }

        MultiplayerSessionSettings.SaveConnectionCode(normalizedCode);
    }

    private void HandleStartHostClicked()
    {
        OnStartHostRequested?.Invoke();
    }

    private void HandleStartClientClicked()
    {
        OnStartClientRequested?.Invoke();
    }

    private void HandleDisconnectClicked()
    {
        OnDisconnectRequested?.Invoke();
    }

    public void DisableButtons()
    {
        m_hostButton.SetEnabled(false);
        m_clientButton.SetEnabled(false);
        m_clientDisconnect.SetEnabled(true);
        m_playerNameField?.SetEnabled(false);
        m_connectionCodeField?.SetEnabled(false);
    }

    public void EnableButtons()
    {
        m_hostButton.SetEnabled(true);
        m_clientButton.SetEnabled(true);
        m_clientDisconnect.SetEnabled(false);
        m_playerNameField?.SetEnabled(true);
        m_connectionCodeField?.SetEnabled(true);
    }
}
