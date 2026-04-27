using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Adrenak.UniMic;
using Adrenak.UniVoice;
using Adrenak.UniVoice.Filters;
using Adrenak.UniVoice.Inputs;
using Adrenak.UniVoice.Networks;
using Adrenak.UniVoice.Outputs;

public class InGameChatVoiceController : MonoBehaviour
{
    private const string ChatClientToServerMessage = "ITH_CHAT_C2S";
    private const string ChatServerToClientMessage = "ITH_CHAT_S2C";
    private const int MaxChatMessageLength = 180;
    private const int MaxChatFeedEntries = 35;
    private const float ChatAutoHideDelay = 6f;
    private const float ArrowScrollStep = 30f;
    private const float MicToastDuration = 1.2f;

    private static InGameChatVoiceController s_instance;

    private readonly Queue<VisualElement> m_renderedEntries = new Queue<VisualElement>();
    private readonly List<ChatEntry> m_pendingEntries = new List<ChatEntry>();

    private NetworkManager m_networkManager;
    private bool m_networkHandlersRegistered;

    private bool m_voiceInitialized;
    private bool m_voiceInitializationInProgress;
    private bool m_voiceUnavailable;
    private bool m_voiceConnected;
    private bool m_voiceUnavailableNotified;
    private bool m_micMuted = true;
    private ClientSession<int> m_voiceSession;
    private IAudioClient<int> m_voiceClient;
    private IAudioServer<int> m_voiceServer;
    private Mic.Device m_micDevice;

    private bool m_uiReady;
    private bool m_isOverlayVisible = true;
    private float m_lastOverlayActivityTime;
    private VisualElement m_overlayRoot;
    private Label m_voiceStatusLabel;
    private ScrollView m_chatScrollView;
    private VisualElement m_chatMessagesContainer;
    private VisualElement m_chatInputRow;
    private TextField m_chatInputField;
    private Label m_hintLabel;
    private Label m_bottomToastLabel;
    private float m_bottomToastHideAt;

    private struct ChatEntry
    {
        public string Sender;
        public string Message;
        public bool IsSystem;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<InGameChatVoiceController>() != null)
        {
            return;
        }

        GameObject controllerObject = new GameObject(nameof(InGameChatVoiceController));
        controllerObject.AddComponent<InGameChatVoiceController>();
    }

    private void Awake()
    {
        if (s_instance != null && s_instance != this)
        {
            Destroy(gameObject);
            return;
        }

        s_instance = this;
        DontDestroyOnLoad(gameObject);
        TryPrewarmUniVoice();
    }

    private void OnDestroy()
    {
        if (s_instance == this)
        {
            s_instance = null;
        }

        GameplayTextInputBlocker.SetTyping(false);
        TearDownUniVoice(true);
        UnregisterNetworkHandlers();
    }

    private void Start()
    {
        TryPrewarmUniVoice();
    }

    private void Update()
    {
        EnsureUiReady();
        EnsureNetworkHandlers();
        HandleKeyboardShortcuts();
        UpdateOverlayVisibility();
        UpdateBottomToast();
        TickVoiceSession();
        UpdateVoiceBadge();
    }

    private void EnsureUiReady()
    {
        if (m_uiReady && (m_overlayRoot == null || m_overlayRoot.panel == null))
        {
            m_uiReady = false;
            m_isOverlayVisible = true;
            m_lastOverlayActivityTime = 0f;
            m_overlayRoot = null;
            m_voiceStatusLabel = null;
            m_chatScrollView = null;
            m_chatMessagesContainer = null;
            m_chatInputRow = null;
            m_chatInputField = null;
            m_hintLabel = null;
            m_bottomToastLabel = null;
            m_bottomToastHideAt = 0f;
            m_renderedEntries.Clear();
        }

        if (m_uiReady)
        {
            return;
        }

        GameUI gameUi = GameUI.Instance;
        if (gameUi == null || gameUi.RootElement == null)
        {
            return;
        }

        BuildUi(gameUi.RootElement);
        m_uiReady = true;
        m_lastOverlayActivityTime = Time.unscaledTime;
        FlushPendingEntries();
        AppendSystemMessage("Enter/T - chat, V - mute mic.");
    }

    private void BuildUi(VisualElement root)
    {
        m_overlayRoot = new VisualElement { name = "CommsOverlay" };
        m_overlayRoot.style.position = Position.Absolute;
        m_overlayRoot.style.left = 18;
        m_overlayRoot.style.bottom = 18;
        m_overlayRoot.style.width = 430;
        m_overlayRoot.style.paddingLeft = 12;
        m_overlayRoot.style.paddingRight = 12;
        m_overlayRoot.style.paddingTop = 10;
        m_overlayRoot.style.paddingBottom = 10;
        m_overlayRoot.style.backgroundColor = new Color(0.06f, 0.08f, 0.11f, 0.46f);
        m_overlayRoot.style.borderTopLeftRadius = 12;
        m_overlayRoot.style.borderTopRightRadius = 12;
        m_overlayRoot.style.borderBottomLeftRadius = 12;
        m_overlayRoot.style.borderBottomRightRadius = 12;
        m_overlayRoot.style.borderLeftWidth = 1;
        m_overlayRoot.style.borderRightWidth = 1;
        m_overlayRoot.style.borderTopWidth = 1;
        m_overlayRoot.style.borderBottomWidth = 1;
        m_overlayRoot.style.borderLeftColor = new Color(0.35f, 0.5f, 0.62f, 0.5f);
        m_overlayRoot.style.borderRightColor = new Color(0.35f, 0.5f, 0.62f, 0.5f);
        m_overlayRoot.style.borderTopColor = new Color(0.35f, 0.5f, 0.62f, 0.5f);
        m_overlayRoot.style.borderBottomColor = new Color(0.35f, 0.5f, 0.62f, 0.5f);

        VisualElement headerRow = new VisualElement();
        headerRow.style.flexDirection = FlexDirection.Row;
        headerRow.style.justifyContent = Justify.SpaceBetween;
        headerRow.style.marginBottom = 8;

        Label titleLabel = new Label("TEAM COMMS");
        titleLabel.style.fontSize = 13;
        titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        titleLabel.style.color = new Color(0.82f, 0.9f, 1f, 1f);

        m_voiceStatusLabel = new Label("VOICE OFFLINE");
        m_voiceStatusLabel.style.fontSize = 11;
        m_voiceStatusLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        m_voiceStatusLabel.style.color = new Color(0.64f, 0.69f, 0.74f, 1f);

        headerRow.Add(titleLabel);
        headerRow.Add(m_voiceStatusLabel);

        m_chatScrollView = new ScrollView(ScrollViewMode.Vertical);
        m_chatScrollView.focusable = false;
        m_chatScrollView.style.height = 170;
        m_chatScrollView.style.marginBottom = 8;
        m_chatScrollView.style.backgroundColor = new Color(0.02f, 0.03f, 0.04f, 0.22f);
        m_chatScrollView.style.borderTopLeftRadius = 8;
        m_chatScrollView.style.borderTopRightRadius = 8;
        m_chatScrollView.style.borderBottomLeftRadius = 8;
        m_chatScrollView.style.borderBottomRightRadius = 8;
        m_chatScrollView.style.paddingLeft = 6;
        m_chatScrollView.style.paddingRight = 6;
        m_chatScrollView.style.paddingTop = 6;
        m_chatScrollView.style.paddingBottom = 6;
        m_chatScrollView.verticalScrollerVisibility = ScrollerVisibility.Hidden;
        m_chatScrollView.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
        m_chatScrollView.verticalScroller.style.display = DisplayStyle.None;
        m_chatScrollView.horizontalScroller.style.display = DisplayStyle.None;

        m_chatMessagesContainer = new VisualElement();
        m_chatMessagesContainer.style.flexDirection = FlexDirection.Column;
        m_chatScrollView.Add(m_chatMessagesContainer);

        m_chatInputRow = new VisualElement();
        m_chatInputRow.style.display = DisplayStyle.None;
        m_chatInputRow.style.flexDirection = FlexDirection.Row;
        m_chatInputRow.style.marginBottom = 6;

        m_chatInputField = new TextField();
        m_chatInputField.label = string.Empty;
        m_chatInputField.maxLength = MaxChatMessageLength;
        m_chatInputField.style.flexGrow = 1;
        m_chatInputField.style.minHeight = 30;
        m_chatInputField.style.marginRight = 6;

        Button sendButton = new Button(TrySendCurrentInput) { text = "Send" };
        sendButton.style.minWidth = 74;
        sendButton.style.height = 30;
        sendButton.style.unityFontStyleAndWeight = FontStyle.Bold;
        sendButton.style.backgroundColor = new Color(0.14f, 0.58f, 0.9f, 0.95f);

        m_chatInputRow.Add(m_chatInputField);
        m_chatInputRow.Add(sendButton);

        m_hintLabel = new Label("Enter/T - chat, V - mute mic");
        m_hintLabel.style.fontSize = 11;
        m_hintLabel.style.color = new Color(0.75f, 0.78f, 0.82f, 0.95f);

        m_overlayRoot.Add(headerRow);
        m_overlayRoot.Add(m_chatScrollView);
        m_overlayRoot.Add(m_chatInputRow);
        m_overlayRoot.Add(m_hintLabel);
        root.Add(m_overlayRoot);

        VisualElement toastContainer = new VisualElement();
        toastContainer.style.position = Position.Absolute;
        toastContainer.style.left = 0f;
        toastContainer.style.right = 0f;
        toastContainer.style.bottom = 14f;
        toastContainer.style.alignItems = Align.Center;
        toastContainer.pickingMode = PickingMode.Ignore;

        m_bottomToastLabel = new Label();
        m_bottomToastLabel.style.display = DisplayStyle.None;
        m_bottomToastLabel.style.paddingLeft = 10f;
        m_bottomToastLabel.style.paddingRight = 10f;
        m_bottomToastLabel.style.paddingTop = 4f;
        m_bottomToastLabel.style.paddingBottom = 4f;
        m_bottomToastLabel.style.backgroundColor = new Color(0.02f, 0.03f, 0.04f, 0.64f);
        m_bottomToastLabel.style.color = new Color(0.92f, 0.96f, 1f, 1f);
        m_bottomToastLabel.style.fontSize = 11f;
        m_bottomToastLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        m_bottomToastLabel.style.borderTopLeftRadius = 8f;
        m_bottomToastLabel.style.borderTopRightRadius = 8f;
        m_bottomToastLabel.style.borderBottomLeftRadius = 8f;
        m_bottomToastLabel.style.borderBottomRightRadius = 8f;
        m_bottomToastLabel.style.borderLeftWidth = 1f;
        m_bottomToastLabel.style.borderRightWidth = 1f;
        m_bottomToastLabel.style.borderTopWidth = 1f;
        m_bottomToastLabel.style.borderBottomWidth = 1f;
        m_bottomToastLabel.style.borderLeftColor = new Color(0.58f, 0.68f, 0.76f, 0.55f);
        m_bottomToastLabel.style.borderRightColor = new Color(0.58f, 0.68f, 0.76f, 0.55f);
        m_bottomToastLabel.style.borderTopColor = new Color(0.58f, 0.68f, 0.76f, 0.55f);
        m_bottomToastLabel.style.borderBottomColor = new Color(0.58f, 0.68f, 0.76f, 0.55f);

        toastContainer.Add(m_bottomToastLabel);
        root.Add(toastContainer);
    }

    private void HandleKeyboardShortcuts()
    {
        if (GameplayMenuState.IsMenuOpen)
        {
            if (GameplayTextInputBlocker.IsTyping)
            {
                CloseChatInput(true);
            }

            return;
        }

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return;
        }

        bool enterPressed = keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame;
        bool downPressed = keyboard.downArrowKey.wasPressedThisFrame;
        bool upPressed = keyboard.upArrowKey.wasPressedThisFrame;

        if (GameplayTextInputBlocker.IsTyping)
        {
            if (upPressed || downPressed)
            {
                ScrollChatByArrows(upPressed, downPressed);
            }

            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                CloseChatInput(true);
            }
            else if (enterPressed)
            {
                TrySendCurrentInput();
            }

            return;
        }

        if (enterPressed || keyboard.tKey.wasPressedThisFrame)
        {
            OpenChatInput();
            return;
        }

        if (upPressed || downPressed)
        {
            ScrollChatByArrows(upPressed, downPressed);
        }

        if (keyboard.vKey.wasPressedThisFrame)
        {
            ToggleMicMute();
        }
    }

    private void OpenChatInput()
    {
        if (m_chatInputRow == null || m_chatInputField == null)
        {
            return;
        }

        ShowOverlay();
        m_chatInputRow.style.display = DisplayStyle.Flex;
        m_chatInputField.Focus();
        GameplayTextInputBlocker.SetTyping(true);
        RegisterOverlayActivity();
    }

    private void CloseChatInput(bool clearInputValue)
    {
        if (m_chatInputRow == null || m_chatInputField == null)
        {
            GameplayTextInputBlocker.SetTyping(false);
            RegisterOverlayActivity();
            return;
        }

        if (clearInputValue)
        {
            m_chatInputField.SetValueWithoutNotify(string.Empty);
        }

        m_chatInputField.Blur();
        m_chatInputRow.style.display = DisplayStyle.None;
        GameplayTextInputBlocker.SetTyping(false);
        RegisterOverlayActivity();
    }

    private void TrySendCurrentInput()
    {
        if (m_chatInputField == null)
        {
            return;
        }

        string message = NormalizeChatMessage(m_chatInputField.value);
        if (string.IsNullOrEmpty(message))
        {
            CloseChatInput(true);
            return;
        }

        SendTextChatMessage(message);
        CloseChatInput(true);
    }

    private void SendTextChatMessage(string message)
    {
        if (m_networkManager == null || !m_networkManager.IsListening)
        {
            AppendSystemMessage("Connect to a multiplayer session first.");
            return;
        }

        string senderName = MultiplayerSessionSettings.LoadPlayerName();
        if (m_networkManager.IsServer)
        {
            RelayTextMessageToAll(m_networkManager.LocalClientId, senderName, message);
            return;
        }

        try
        {
            using FastBufferWriter writer = new FastBufferWriter(1024, Allocator.Temp);
            writer.WriteValueSafe(senderName);
            writer.WriteValueSafe(message);
            m_networkManager.CustomMessagingManager.SendNamedMessage(
                ChatClientToServerMessage,
                NetworkManager.ServerClientId,
                writer,
                NetworkDelivery.ReliableSequenced);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Failed to send chat message: {exception.Message}");
        }
    }

    private void RelayTextMessageToAll(ulong senderClientId, string fallbackSenderName, string rawMessage)
    {
        if (m_networkManager == null || !m_networkManager.IsServer)
        {
            return;
        }

        string message = NormalizeChatMessage(rawMessage);
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        string displayName;
        if (!PlayerNameUI.TryGetRegisteredName(senderClientId, out displayName))
        {
            displayName = MultiplayerSessionSettings.ResolvePlayerName(fallbackSenderName, senderClientId);
        }

        using FastBufferWriter writer = new FastBufferWriter(1024, Allocator.Temp);
        writer.WriteValueSafe(displayName);
        writer.WriteValueSafe(message);
        m_networkManager.CustomMessagingManager.SendNamedMessageToAll(
            ChatServerToClientMessage,
            writer,
            NetworkDelivery.ReliableSequenced);
    }

    private void HandleChatClientToServer(ulong senderClientId, FastBufferReader reader)
    {
        if (m_networkManager == null || !m_networkManager.IsServer)
        {
            return;
        }

        try
        {
            reader.ReadValueSafe(out string senderNameFromClient);
            reader.ReadValueSafe(out string message);
            RelayTextMessageToAll(senderClientId, senderNameFromClient, message);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Failed to decode chat payload from client {senderClientId}: {exception.Message}");
        }
    }

    private void HandleChatServerToClient(ulong senderClientId, FastBufferReader reader)
    {
        try
        {
            reader.ReadValueSafe(out string senderName);
            reader.ReadValueSafe(out string message);
            AppendChatMessage(senderName, message);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Failed to decode chat payload from server {senderClientId}: {exception.Message}");
        }
    }

    private void EnsureNetworkHandlers()
    {
        NetworkManager singleton = NetworkManager.Singleton;
        if (singleton == null || !singleton.IsListening)
        {
            if (m_networkHandlersRegistered)
            {
                UnregisterNetworkHandlers();
            }

            return;
        }

        if (m_networkHandlersRegistered && m_networkManager == singleton)
        {
            return;
        }

        if (m_networkHandlersRegistered)
        {
            UnregisterNetworkHandlers();
        }

        m_networkManager = singleton;
        CustomMessagingManager customMessagingManager = m_networkManager.CustomMessagingManager;
        customMessagingManager.RegisterNamedMessageHandler(ChatClientToServerMessage, HandleChatClientToServer);
        customMessagingManager.RegisterNamedMessageHandler(ChatServerToClientMessage, HandleChatServerToClient);

        m_networkManager.OnClientConnectedCallback += HandleClientConnected;
        m_networkManager.OnClientDisconnectCallback += HandleClientDisconnected;
        m_networkHandlersRegistered = true;
        m_voiceUnavailable = false;
        m_voiceUnavailableNotified = false;
    }

    private void UnregisterNetworkHandlers()
    {
        if (!m_networkHandlersRegistered || m_networkManager == null)
        {
            return;
        }

        try
        {
            CustomMessagingManager customMessagingManager = m_networkManager.CustomMessagingManager;
            customMessagingManager.UnregisterNamedMessageHandler(ChatClientToServerMessage);
            customMessagingManager.UnregisterNamedMessageHandler(ChatServerToClientMessage);
        }
        catch
        {
            // Ignore cleanup exceptions while network shuts down.
        }

        m_networkManager.OnClientConnectedCallback -= HandleClientConnected;
        m_networkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
        m_networkHandlersRegistered = false;
        m_networkManager = null;
    }

    private void HandleClientConnected(ulong clientId)
    {
        AppendSystemMessage($"{MultiplayerSessionSettings.ResolvePlayerName(string.Empty, clientId)} connected.");
    }

    private void HandleClientDisconnected(ulong clientId)
    {
        if (m_networkManager == null)
        {
            return;
        }

        if (clientId == m_networkManager.LocalClientId)
        {
            AppendSystemMessage("Disconnected.");
            CloseChatInput(true);
            return;
        }

        AppendSystemMessage($"{MultiplayerSessionSettings.ResolvePlayerName(string.Empty, clientId)} disconnected.");
    }

    private void TickVoiceSession()
    {
        if (NetworkManager.Singleton == null)
            return;

        if (m_voiceInitialized || m_voiceInitializationInProgress || m_voiceUnavailable)
        {
            return;
        }

        EnsureUniVoiceReady();
    }

    private void TryPrewarmUniVoice()
    {
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        if (m_voiceInitialized || m_voiceInitializationInProgress || m_voiceUnavailable)
        {
            return;
        }

        EnsureUniVoiceReady();
    }

    private void EnsureUniVoiceReady()
    {
        m_voiceInitializationInProgress = true;
        try
        {
            Mic.Init();

            IAudioInput input;
            m_micDevice = null;

            List<Mic.Device> devices = Mic.AvailableDevices;
            if (devices.Count == 0)
            {
                input = new EmptyAudioInput();
                m_micMuted = true;
                AppendSystemMessage("No microphone found. Voice listen-only mode.");
            }
            else
            {
                m_micDevice = devices[0];
                m_micDevice.StartRecording(60);
                input = new UniMicInput(m_micDevice);
                Debug.Log($"UniVoice microphone selected: {m_micDevice.Name}");
            }

            NGOClient ngoClient = new NGOClient();
            ngoClient.OnJoined += HandleUniVoiceJoined;
            ngoClient.OnLeft += HandleUniVoiceLeft;

            m_voiceClient = ngoClient;
            m_voiceSession = new ClientSession<int>(
                ngoClient,
                input,
                () =>
                {
                    StreamedAudioSourceOutput output = StreamedAudioSourceOutput.New();
                    output.gameObject.name = "UniVoicePeerAudio";
                    return output;
                });

            m_voiceSession.InputFilters.Add(new SimpleVadFilter(new SimpleVad()));
            m_voiceSession.InputFilters.Add(new ConcentusEncodeFilter());
            m_voiceSession.AddOutputFilter<ConcentusDecodeFilter>(() => new ConcentusDecodeFilter());

            // Keep server integration alive even before StartHost/StartServer.
            // NGOServer then catches host lifecycle callbacks at the right time.
            m_voiceServer = new NGOServer(ngoClient);

            m_voiceInitialized = true;
            m_voiceUnavailable = false;
            m_voiceUnavailableNotified = false;
            ApplyMicMuteState();
        }
        catch (Exception exception)
        {
            TearDownUniVoice(false);
            MarkVoiceUnavailable($"Voice setup failed ({exception.Message}). Text chat remains available.");
        }
        finally
        {
            m_voiceInitializationInProgress = false;
        }
    }

    private void TearDownUniVoice(bool clearUnavailableFlags)
    {
        try
        {
            if (m_voiceClient != null)
            {
                m_voiceClient.OnJoined -= HandleUniVoiceJoined;
                m_voiceClient.OnLeft -= HandleUniVoiceLeft;
            }
        }
        catch
        {
            // Ignore event detach issues during teardown.
        }

        try
        {
            m_voiceServer?.Dispose();
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Failed to dispose UniVoice server: {exception.Message}");
        }

        try
        {
            m_voiceSession?.Dispose();
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Failed to dispose UniVoice session: {exception.Message}");
        }

        if (m_micDevice != null && m_micDevice.IsRecording)
        {
            m_micDevice.StopRecording();
        }

        m_micDevice = null;
        m_voiceSession = null;
        m_voiceClient = null;
        m_voiceServer = null;

        m_voiceInitialized = false;
        m_voiceInitializationInProgress = false;
        m_voiceConnected = false;

        if (clearUnavailableFlags)
        {
            m_voiceUnavailable = false;
            m_voiceUnavailableNotified = false;
        }
    }

    private void HandleUniVoiceJoined(int localPeerId, List<int> peerIds)
    {
        m_voiceConnected = true;
        ApplyMicMuteState();
        AppendSystemMessage("Voice linked to session channel.");
    }

    private void HandleUniVoiceLeft()
    {
        m_voiceConnected = false;
    }

    private void ToggleMicMute()
    {
        m_micMuted = !m_micMuted;
        ApplyMicMuteState();
        ShowBottomToast(m_micMuted ? "Mic muted" : "Mic active");
    }

    private void ApplyMicMuteState()
    {
        if (!m_voiceInitialized || m_voiceSession == null)
        {
            return;
        }

        m_voiceSession.InputEnabled = !m_micMuted;
    }

    private void AppendSystemMessage(string message)
    {
        AppendEntry("System", message, true);
    }

    private void AppendChatMessage(string senderName, string message)
    {
        AppendEntry(senderName, message, false);
    }

    private void AppendEntry(string senderName, string message, bool isSystem)
    {
        string normalizedMessage = NormalizeChatMessage(message);
        if (string.IsNullOrEmpty(normalizedMessage))
        {
            return;
        }

        ChatEntry entry = new ChatEntry
        {
            Sender = string.IsNullOrWhiteSpace(senderName) ? "Player" : senderName.Trim(),
            Message = normalizedMessage,
            IsSystem = isSystem
        };

        if (!m_uiReady || m_chatMessagesContainer == null || m_chatScrollView == null)
        {
            m_pendingEntries.Add(entry);
            if (m_pendingEntries.Count > MaxChatFeedEntries)
            {
                m_pendingEntries.RemoveAt(0);
            }

            return;
        }

        RenderEntry(entry);
        if (!isSystem)
        {
            RegisterOverlayActivity();
        }
        else if (m_isOverlayVisible)
        {
            RegisterOverlayActivity();
        }
    }

    private void FlushPendingEntries()
    {
        if (!m_uiReady || m_chatMessagesContainer == null || m_chatScrollView == null)
        {
            return;
        }

        for (int i = 0; i < m_pendingEntries.Count; i++)
        {
            RenderEntry(m_pendingEntries[i]);
        }

        m_pendingEntries.Clear();
    }

    private void RenderEntry(ChatEntry entry)
    {
        VisualElement row = new VisualElement();
        row.style.marginBottom = 4;
        row.style.paddingLeft = 7;
        row.style.paddingRight = 7;
        row.style.paddingTop = 5;
        row.style.paddingBottom = 5;
        row.style.borderTopLeftRadius = 6;
        row.style.borderTopRightRadius = 6;
        row.style.borderBottomLeftRadius = 6;
        row.style.borderBottomRightRadius = 6;
        row.style.backgroundColor = entry.IsSystem
            ? new Color(0.34f, 0.46f, 0.6f, 0.16f)
            : new Color(0.12f, 0.15f, 0.2f, 0.24f);

        Label textLabel = new Label($"[{entry.Sender}] {entry.Message}");
        textLabel.style.whiteSpace = WhiteSpace.Normal;
        textLabel.style.fontSize = 12;
        textLabel.style.color = entry.IsSystem
            ? new Color(0.88f, 0.95f, 1f, 1f)
            : new Color(0.93f, 0.95f, 0.98f, 1f);

        row.Add(textLabel);
        m_chatMessagesContainer.Add(row);
        m_renderedEntries.Enqueue(row);

        while (m_renderedEntries.Count > MaxChatFeedEntries)
        {
            m_renderedEntries.Dequeue().RemoveFromHierarchy();
        }

        m_chatScrollView.ScrollTo(row);
    }

    private void UpdateVoiceBadge()
    {
        if (m_voiceStatusLabel == null || !m_isOverlayVisible)
        {
            return;
        }

        string statusText;
        Color statusColor;
        if (m_networkManager == null || !m_networkManager.IsListening)
        {
            statusText = "VOICE OFFLINE";
            statusColor = new Color(0.64f, 0.69f, 0.74f, 1f);
        }
        else if (m_voiceUnavailable)
        {
            statusText = "VOICE UNAVAILABLE";
            statusColor = new Color(1f, 0.72f, 0.32f, 1f);
        }
        else if (m_voiceInitializationInProgress)
        {
            statusText = "VOICE CONNECTING";
            statusColor = new Color(0.98f, 0.9f, 0.46f, 1f);
        }
        else if (m_voiceConnected)
        {
            statusText = m_micMuted ? "VOICE MUTED (V)" : "VOICE LIVE (V)";
            statusColor = m_micMuted ? new Color(1f, 0.82f, 0.54f, 1f) : new Color(0.54f, 1f, 0.74f, 1f);
        }
        else
        {
            statusText = "VOICE WAITING";
            statusColor = new Color(0.72f, 0.85f, 1f, 1f);
        }

        m_voiceStatusLabel.text = statusText;
        m_voiceStatusLabel.style.color = statusColor;
        UpdateHintText();
    }

    private void UpdateHintText()
    {
        if (m_hintLabel == null)
        {
            return;
        }

        m_hintLabel.text = GameplayTextInputBlocker.IsTyping
            ? "Enter - send, Esc - cancel"
            : "Enter/T - chat, V - mute mic";
    }

    private void ScrollChatByArrows(bool upPressed, bool downPressed)
    {
        if (m_chatScrollView == null || !m_isOverlayVisible)
        {
            return;
        }

        Vector2 offset = m_chatScrollView.scrollOffset;
        if (upPressed)
        {
            offset.y = Mathf.Max(0f, offset.y - ArrowScrollStep);
        }

        if (downPressed)
        {
            offset.y += ArrowScrollStep;
        }

        m_chatScrollView.scrollOffset = offset;
        RegisterOverlayActivity();
    }

    private void UpdateOverlayVisibility()
    {
        if (!m_uiReady || m_overlayRoot == null)
        {
            return;
        }

        if (GameplayTextInputBlocker.IsTyping || GameplayMenuState.IsMenuOpen)
        {
            ShowOverlay();
            return;
        }

        if (!m_isOverlayVisible)
        {
            return;
        }

        if (Time.unscaledTime - m_lastOverlayActivityTime >= ChatAutoHideDelay)
        {
            HideOverlay();
        }
    }

    private void RegisterOverlayActivity()
    {
        m_lastOverlayActivityTime = Time.unscaledTime;
        ShowOverlay();
        UpdateHintText();
    }

    private void ShowOverlay()
    {
        if (m_overlayRoot == null)
        {
            return;
        }

        if (!m_isOverlayVisible)
        {
            m_overlayRoot.style.display = DisplayStyle.Flex;
            m_isOverlayVisible = true;
        }
    }

    private void HideOverlay()
    {
        if (m_overlayRoot == null)
        {
            return;
        }

        m_overlayRoot.style.display = DisplayStyle.None;
        m_isOverlayVisible = false;
    }

    private void ShowBottomToast(string text)
    {
        if (m_bottomToastLabel == null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        m_bottomToastLabel.text = text;
        m_bottomToastLabel.style.display = DisplayStyle.Flex;
        m_bottomToastHideAt = Time.unscaledTime + MicToastDuration;
    }

    private void UpdateBottomToast()
    {
        if (m_bottomToastLabel == null || m_bottomToastLabel.style.display == DisplayStyle.None)
        {
            return;
        }

        if (Time.unscaledTime >= m_bottomToastHideAt)
        {
            m_bottomToastLabel.style.display = DisplayStyle.None;
        }
    }

    private void MarkVoiceUnavailable(string message)
    {
        m_voiceUnavailable = true;
        m_voiceConnected = false;

        if (!string.IsNullOrWhiteSpace(message))
        {
            Debug.LogWarning(message);
        }

        if (m_voiceUnavailableNotified)
        {
            return;
        }

        m_voiceUnavailableNotified = true;
        AppendSystemMessage("Voice disabled. Text chat works.");
    }

    private static string NormalizeChatMessage(string rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return string.Empty;
        }

        string normalized = rawMessage.Trim().Replace('\n', ' ').Replace('\r', ' ');
        if (normalized.Length > MaxChatMessageLength)
        {
            normalized = normalized.Substring(0, MaxChatMessageLength);
        }

        return normalized;
    }
}
