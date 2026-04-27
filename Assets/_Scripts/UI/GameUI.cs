using UnityEngine;
using UnityEngine.UIElements;

public class GameUI : MonoBehaviour
{
    [SerializeField] private UIDocument uiDocument;
    private VisualElement m_root;
    private VisualElement m_throwChargeContainer;
    private VisualElement m_throwChargeFill;

    public static GameUI Instance { get; private set; }
    public VisualElement RootElement => m_root;

    private void Awake()
    {
        Instance = this;
    }

    private void Start()
    {
        CacheReferences();
        SetThrowCharge(0f, false);
        HandleMenuStateChanged(true);
    }

    private void OnEnable()
    {
        GameplayMenuState.OnMenuStateChanged += HandleMenuStateChanged;
    }

    private void OnDisable()
    {
        GameplayMenuState.OnMenuStateChanged -= HandleMenuStateChanged;
        SetThrowCharge(0f, false);
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
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
        if (m_root == null)
        {
            CacheReferences();
        }

        if (m_root == null)
        {
            return;
        }

        m_root.style.display = isMenuOpen ? DisplayStyle.None : DisplayStyle.Flex;
        if (isMenuOpen)
        {
            SetThrowCharge(0f, false);
        }
    }
}
