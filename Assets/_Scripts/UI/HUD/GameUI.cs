using UnityEngine;
using UnityEngine.UIElements;

public class GameUI : MonoBehaviour
{
    [SerializeField] private UIDocument uiDocument;
    private VisualElement m_root;
    private VisualElement m_throwChargeContainer;
    private VisualElement m_throwChargeFill;
    private VisualElement m_shiftHudContainer;
    private Label m_shiftTimerValueLabel;
    private Label m_shiftQuotaValueLabel;
    private VisualElement m_resultOverlay;
    private Label m_resultTitleLabel;
    private Label m_resultMessageLabel;
    private Label m_resultHintLabel;
    private Button m_restartButton;
    private GameManager m_gameManager;
    private bool m_isMenuOpen = true;
    private bool m_isResultVisible;

    public static GameUI Instance { get; private set; }
    public VisualElement RootElement => m_root;

    private void Awake()
    {
        Instance = this;
    }

    private void Start()
    {
        CacheReferences();
        BuildShiftHud();
        BindGameManager();
        SetThrowCharge(0f, false);
        HandleMenuStateChanged(GameplayMenuState.IsMenuOpen);
        RefreshShiftUi();
    }

    private void OnEnable()
    {
        GameplayMenuState.OnMenuStateChanged += HandleMenuStateChanged;
    }

    private void OnDisable()
    {
        GameplayMenuState.OnMenuStateChanged -= HandleMenuStateChanged;
        UnbindGameManager();
        SetThrowCharge(0f, false);
    }

    private void OnDestroy()
    {
        if (m_restartButton != null)
        {
            m_restartButton.clicked -= HandleRestartClicked;
        }

        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void Update()
    {
        if (m_root == null)
        {
            CacheReferences();
            BuildShiftHud();
        }

        if (m_gameManager == null)
        {
            BindGameManager();
        }

        RefreshShiftStats();
    }

    public void SetThrowCharge(float normalizedValue, bool isVisible)
    {
        if (m_root == null)
        {
            CacheReferences();
        }

        if (m_throwChargeContainer == null || m_throwChargeFill == null)
        {
            return;
        }

        float clamped = Mathf.Clamp01(normalizedValue);
        m_throwChargeContainer.style.display = isVisible ? DisplayStyle.Flex : DisplayStyle.None;
        m_throwChargeFill.style.width = Length.Percent(clamped * 100f);
    }

    private void CacheReferences()
    {
        if (uiDocument == null)
        {
            return;
        }

        m_root = uiDocument.rootVisualElement;
        m_throwChargeContainer = m_root.Q<VisualElement>("ThrowChargeContainer");
        m_throwChargeFill = m_root.Q<VisualElement>("ThrowChargeFill");
    }

    private void HandleMenuStateChanged(bool isMenuOpen)
    {
        m_isMenuOpen = isMenuOpen;
        RefreshRootVisibility();

        if (isMenuOpen || m_isResultVisible)
        {
            SetThrowCharge(0f, false);
        }
    }

    private void BindGameManager()
    {
        GameManager gameManager = FindAnyObjectByType<GameManager>();
        if (gameManager == m_gameManager)
        {
            return;
        }

        UnbindGameManager();
        m_gameManager = gameManager;

        if (m_gameManager != null)
        {
            m_gameManager.OnShiftDataChanged += HandleShiftDataChanged;
        }

        RefreshShiftUi();
    }

    private void UnbindGameManager()
    {
        if (m_gameManager == null)
        {
            return;
        }

        m_gameManager.OnShiftDataChanged -= HandleShiftDataChanged;
        m_gameManager = null;
    }

    private void HandleShiftDataChanged()
    {
        RefreshShiftUi();
    }

    private void BuildShiftHud()
    {
        if (m_root == null || m_shiftHudContainer != null)
        {
            return;
        }

        m_shiftHudContainer = new VisualElement
        {
            name = "ShiftHudContainer",
            pickingMode = PickingMode.Ignore
        };
        m_shiftHudContainer.style.position = Position.Absolute;
        m_shiftHudContainer.style.left = 0f;
        m_shiftHudContainer.style.right = 0f;
        m_shiftHudContainer.style.top = 16f;
        m_shiftHudContainer.style.flexDirection = FlexDirection.Row;
        m_shiftHudContainer.style.justifyContent = Justify.Center;

        VisualElement shiftStatsCard = new VisualElement
        {
            pickingMode = PickingMode.Ignore
        };
        shiftStatsCard.style.flexDirection = FlexDirection.Row;
        shiftStatsCard.style.alignItems = Align.Center;
        shiftStatsCard.style.paddingLeft = 18f;
        shiftStatsCard.style.paddingRight = 18f;
        shiftStatsCard.style.paddingTop = 12f;
        shiftStatsCard.style.paddingBottom = 12f;
        shiftStatsCard.style.backgroundColor = new Color(0.05f, 0.07f, 0.09f, 0.84f);
        shiftStatsCard.style.borderTopLeftRadius = 14f;
        shiftStatsCard.style.borderTopRightRadius = 14f;
        shiftStatsCard.style.borderBottomLeftRadius = 14f;
        shiftStatsCard.style.borderBottomRightRadius = 14f;
        shiftStatsCard.style.borderLeftWidth = 1f;
        shiftStatsCard.style.borderRightWidth = 1f;
        shiftStatsCard.style.borderTopWidth = 1f;
        shiftStatsCard.style.borderBottomWidth = 1f;
        shiftStatsCard.style.borderLeftColor = new Color(0.95f, 0.74f, 0.35f, 0.32f);
        shiftStatsCard.style.borderRightColor = new Color(0.95f, 0.74f, 0.35f, 0.32f);
        shiftStatsCard.style.borderTopColor = new Color(0.95f, 0.74f, 0.35f, 0.32f);
        shiftStatsCard.style.borderBottomColor = new Color(0.95f, 0.74f, 0.35f, 0.32f);

        VisualElement shiftTimerStat = CreateShiftStat("Shift", out m_shiftTimerValueLabel);
        shiftTimerStat.style.marginRight = 18f;
        shiftStatsCard.Add(shiftTimerStat);
        shiftStatsCard.Add(CreateShiftStat("Quota", out m_shiftQuotaValueLabel));
        m_shiftHudContainer.Add(shiftStatsCard);

        m_resultOverlay = new VisualElement
        {
            name = "ShiftResultOverlay"
        };
        m_resultOverlay.style.position = Position.Absolute;
        m_resultOverlay.style.left = 0f;
        m_resultOverlay.style.right = 0f;
        m_resultOverlay.style.top = 0f;
        m_resultOverlay.style.bottom = 0f;
        m_resultOverlay.style.justifyContent = Justify.Center;
        m_resultOverlay.style.alignItems = Align.Center;
        m_resultOverlay.style.backgroundColor = new Color(0.02f, 0.03f, 0.04f, 0.76f);
        m_resultOverlay.style.display = DisplayStyle.None;

        VisualElement resultCard = new VisualElement();
        resultCard.style.width = 460f;
        resultCard.style.maxWidth = Length.Percent(92f);
        resultCard.style.paddingLeft = 30f;
        resultCard.style.paddingRight = 30f;
        resultCard.style.paddingTop = 28f;
        resultCard.style.paddingBottom = 28f;
        resultCard.style.backgroundColor = new Color(0.08f, 0.1f, 0.12f, 0.98f);
        resultCard.style.borderTopLeftRadius = 22f;
        resultCard.style.borderTopRightRadius = 22f;
        resultCard.style.borderBottomLeftRadius = 22f;
        resultCard.style.borderBottomRightRadius = 22f;
        resultCard.style.borderLeftWidth = 1f;
        resultCard.style.borderRightWidth = 1f;
        resultCard.style.borderTopWidth = 1f;
        resultCard.style.borderBottomWidth = 1f;
        resultCard.style.borderLeftColor = new Color(1f, 0.85f, 0.46f, 0.45f);
        resultCard.style.borderRightColor = new Color(1f, 0.85f, 0.46f, 0.45f);
        resultCard.style.borderTopColor = new Color(1f, 0.85f, 0.46f, 0.45f);
        resultCard.style.borderBottomColor = new Color(1f, 0.85f, 0.46f, 0.45f);

        m_resultTitleLabel = new Label();
        m_resultTitleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        m_resultTitleLabel.style.fontSize = 28f;
        m_resultTitleLabel.style.color = new Color(0.99f, 0.95f, 0.88f, 1f);
        m_resultTitleLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        m_resultTitleLabel.style.marginBottom = 14f;

        m_resultMessageLabel = new Label();
        m_resultMessageLabel.style.fontSize = 17f;
        m_resultMessageLabel.style.color = new Color(0.92f, 0.95f, 0.98f, 0.96f);
        m_resultMessageLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        m_resultMessageLabel.style.whiteSpace = WhiteSpace.Normal;
        m_resultMessageLabel.style.marginBottom = 10f;

        m_resultHintLabel = new Label("Esc: open menu and leave the match");
        m_resultHintLabel.style.fontSize = 14f;
        m_resultHintLabel.style.color = new Color(0.78f, 0.83f, 0.88f, 0.78f);
        m_resultHintLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        m_resultHintLabel.style.whiteSpace = WhiteSpace.Normal;

        m_restartButton = new Button(HandleRestartClicked)
        {
            text = "Restart Shift"
        };
        m_restartButton.style.alignSelf = Align.Center;
        m_restartButton.style.marginTop = 12f;
        m_restartButton.style.paddingLeft = 22f;
        m_restartButton.style.paddingRight = 22f;
        m_restartButton.style.paddingTop = 10f;
        m_restartButton.style.paddingBottom = 10f;
        m_restartButton.style.backgroundColor = new Color(0.92f, 0.69f, 0.28f, 0.96f);
        m_restartButton.style.color = new Color(0.08f, 0.08f, 0.08f, 0.98f);
        m_restartButton.style.borderTopLeftRadius = 10f;
        m_restartButton.style.borderTopRightRadius = 10f;
        m_restartButton.style.borderBottomLeftRadius = 10f;
        m_restartButton.style.borderBottomRightRadius = 10f;
        m_restartButton.style.borderLeftWidth = 0f;
        m_restartButton.style.borderRightWidth = 0f;
        m_restartButton.style.borderTopWidth = 0f;
        m_restartButton.style.borderBottomWidth = 0f;

        resultCard.Add(m_resultTitleLabel);
        resultCard.Add(m_resultMessageLabel);
        resultCard.Add(m_resultHintLabel);
        resultCard.Add(m_restartButton);
        m_resultOverlay.Add(resultCard);

        m_root.Add(m_shiftHudContainer);
        m_root.Add(m_resultOverlay);
    }

    private VisualElement CreateShiftStat(string caption, out Label valueLabel)
    {
        VisualElement statContainer = new VisualElement
        {
            pickingMode = PickingMode.Ignore
        };
        statContainer.style.minWidth = 110f;

        Label captionLabel = new Label(caption);
        captionLabel.style.fontSize = 12f;
        captionLabel.style.color = new Color(0.76f, 0.81f, 0.87f, 0.86f);
        captionLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        captionLabel.style.marginBottom = 2f;

        valueLabel = new Label("--:--");
        valueLabel.style.fontSize = 22f;
        valueLabel.style.color = new Color(1f, 0.94f, 0.8f, 1f);
        valueLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        valueLabel.style.unityTextAlign = TextAnchor.MiddleCenter;

        statContainer.Add(captionLabel);
        statContainer.Add(valueLabel);
        return statContainer;
    }

    private void RefreshShiftUi()
    {
        if (m_root == null)
        {
            CacheReferences();
            BuildShiftHud();
        }

        if (m_root == null || m_shiftHudContainer == null || m_resultOverlay == null)
        {
            return;
        }

        ShiftState shiftState = m_gameManager != null ? m_gameManager.CurrentShiftState : ShiftState.Lobby;
        bool hasShiftUi = shiftState != ShiftState.Lobby;
        m_shiftHudContainer.style.display = hasShiftUi ? DisplayStyle.Flex : DisplayStyle.None;

        RefreshShiftStats();

        m_isResultVisible = shiftState is ShiftState.Victory or ShiftState.Defeat;
        m_resultOverlay.style.display = m_isResultVisible ? DisplayStyle.Flex : DisplayStyle.None;

        if (m_isResultVisible)
        {
            SetThrowCharge(0f, false);
            RefreshResultText(shiftState);
        }

        RefreshResultActions();
        RefreshRootVisibility();
    }

    private void RefreshShiftStats()
    {
        if (m_shiftTimerValueLabel == null || m_shiftQuotaValueLabel == null)
        {
            return;
        }

        if (m_gameManager == null || m_gameManager.CurrentShiftState == ShiftState.Lobby)
        {
            m_shiftTimerValueLabel.text = "--:--";
            m_shiftQuotaValueLabel.text = "0 / 0";
            return;
        }

        int currentQuota = Mathf.Max(0, m_gameManager.CurrentQuota);
        int requiredQuota = Mathf.Max(0, m_gameManager.RequiredQuota);
        float remainingTime = m_gameManager.RemainingShiftTimeSeconds;

        m_shiftTimerValueLabel.text = FormatTime(remainingTime);
        m_shiftQuotaValueLabel.text = $"{currentQuota} / {requiredQuota}";
    }

    private void RefreshResultText(ShiftState shiftState)
    {
        if (m_resultTitleLabel == null || m_resultMessageLabel == null)
        {
            return;
        }

        int currentQuota = Mathf.Max(0, m_gameManager != null ? m_gameManager.CurrentQuota : 0);
        int requiredQuota = Mathf.Max(0, m_gameManager != null ? m_gameManager.RequiredQuota : 0);

        if (shiftState == ShiftState.Victory)
        {
            m_resultTitleLabel.text = "Shift Complete";
            m_resultMessageLabel.text = $"Quota reached: {currentQuota}/{requiredQuota}. All pallets were filled in time.";
            return;
        }

        m_resultTitleLabel.text = "Shift Failed";
        m_resultMessageLabel.text = $"Time is up. You only collected {currentQuota}/{requiredQuota} pallet resources.";
    }

    private void RefreshResultActions()
    {
        if (m_resultHintLabel == null || m_restartButton == null)
        {
            return;
        }

        if (m_isResultVisible == false || m_gameManager == null)
        {
            m_restartButton.style.display = DisplayStyle.None;
            m_restartButton.SetEnabled(false);
            m_restartButton.text = "Restart Shift";
            m_resultHintLabel.text = "Esc: open menu and leave the match";
            return;
        }

        if (m_gameManager.IsLocalHost)
        {
            m_restartButton.style.display = DisplayStyle.Flex;
            m_restartButton.text = m_gameManager.IsShiftRestartInProgress
                ? "Restarting..."
                : "Restart Shift";
            m_restartButton.SetEnabled(m_gameManager.CanLocalRestartShift);
            m_resultHintLabel.text = "Host can restart the shift or press Esc to leave the match.";
            return;
        }

        m_restartButton.style.display = DisplayStyle.None;
        m_restartButton.SetEnabled(false);
        m_restartButton.text = "Restart Shift";
        m_resultHintLabel.text = "Waiting for host to restart the shift. Press Esc to leave the match.";
    }

    private void HandleRestartClicked()
    {
        if (m_gameManager == null)
        {
            return;
        }

        if (m_gameManager.TryRestartShift())
        {
            RefreshResultActions();
        }
    }

    private void RefreshRootVisibility()
    {
        if (m_root == null)
        {
            return;
        }

        m_root.style.display = m_isMenuOpen && m_isResultVisible == false
            ? DisplayStyle.None
            : DisplayStyle.Flex;
    }

    private static string FormatTime(float seconds)
    {
        int totalSeconds = Mathf.CeilToInt(Mathf.Max(0f, seconds));
        int minutes = totalSeconds / 60;
        int remainingSeconds = totalSeconds % 60;
        return $"{minutes:00}:{remainingSeconds:00}";
    }
}
