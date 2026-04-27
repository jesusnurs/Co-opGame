using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public class MyPlayerInput : NetworkBehaviour
{
    [SerializeField]
    private InputActionReference m_movementReference;
    public Vector2 MovementInput { get; private set; }
    public event Action OnPickUpPressed;
    public event Action OnInteractPressed;
    public event Action OnThrowStarted;
    public event Action OnThrowReleased;
    public event Action OnThrowTapped;
    public event Action<int> OnEmotePressed;

    private Vector2 m_rawInput;
    [SerializeField]
    private float m_smoothTime = 0.1f;
    [SerializeField, Min(0f)]
    private float m_throwHoldDuration = 0.15f;

    private bool m_isThrowInputPressed;
    private bool m_hasThrowHoldStarted;
    private float m_throwPressedTimestamp;

    public Vector2 ReadImmediateMovementInput()
    {
        if (IsOwner == false || m_movementReference == null || m_movementReference.action == null)
        {
            return Vector2.zero;
        }

        return m_movementReference.action.ReadValue<Vector2>();
    }

    // Update is called once per frame
    void Update()
    {
        if(IsOwner == false)
        {
            ResetThrowInputState();
            return;
        }

        if (GameplayTextInputBlocker.IsTyping)
        {
            m_rawInput = Vector2.zero;
            MovementInput = Vector2.MoveTowards(
                MovementInput,
                Vector2.zero,
                Time.deltaTime / Mathf.Max(0.0001f, m_smoothTime));
            ResetThrowInputState();
            return;
        }

        if (GameplayMenuState.IsMenuOpen)
        {
            m_rawInput = Vector2.zero;
            MovementInput = Vector2.MoveTowards(
                MovementInput,
                Vector2.zero,
                Time.deltaTime / m_smoothTime);
            ResetThrowInputState();
            return;
        }

        m_rawInput = m_movementReference.action.ReadValue<Vector2>();
        MovementInput = Vector2.MoveTowards
            (MovementInput, m_rawInput, Time.deltaTime / m_smoothTime);
        if (Keyboard.current != null)
        {
            if (Keyboard.current.eKey.wasPressedThisFrame)
            {
                OnPickUpPressed?.Invoke();
            }

            if (Keyboard.current.spaceKey.wasPressedThisFrame)
            {
                m_isThrowInputPressed = true;
                m_hasThrowHoldStarted = false;
                m_throwPressedTimestamp = Time.time;
            }

            if (m_isThrowInputPressed &&
                m_hasThrowHoldStarted == false &&
                Keyboard.current.spaceKey.isPressed &&
                Time.time - m_throwPressedTimestamp >= m_throwHoldDuration)
            {
                m_hasThrowHoldStarted = true;
                OnThrowStarted?.Invoke();
            }

            if (Keyboard.current.spaceKey.wasReleasedThisFrame)
            {
                if (m_isThrowInputPressed)
                {
                    if (m_hasThrowHoldStarted == false &&
                        Time.time - m_throwPressedTimestamp >= m_throwHoldDuration)
                    {
                        m_hasThrowHoldStarted = true;
                        OnThrowStarted?.Invoke();
                    }

                    if (m_hasThrowHoldStarted)
                    {
                        OnThrowReleased?.Invoke();
                    }
                    else
                    {
                        OnThrowTapped?.Invoke();
                    }
                }

                ResetThrowInputState();
            }

            if (Keyboard.current.digit1Key.wasPressedThisFrame || Keyboard.current.numpad1Key.wasPressedThisFrame)
            {
                OnEmotePressed?.Invoke(1);
            }
            else if (Keyboard.current.digit2Key.wasPressedThisFrame || Keyboard.current.numpad2Key.wasPressedThisFrame)
            {
                OnEmotePressed?.Invoke(2);
            }
            else if (Keyboard.current.digit3Key.wasPressedThisFrame || Keyboard.current.numpad3Key.wasPressedThisFrame)
            {
                OnEmotePressed?.Invoke(3);
            }
            else if (Keyboard.current.digit4Key.wasPressedThisFrame || Keyboard.current.numpad4Key.wasPressedThisFrame)
            {
                OnEmotePressed?.Invoke(4);
            }
            else if (Keyboard.current.digit5Key.wasPressedThisFrame || Keyboard.current.numpad5Key.wasPressedThisFrame)
            {
                OnEmotePressed?.Invoke(5);
            }
            else if (Keyboard.current.digit6Key.wasPressedThisFrame || Keyboard.current.numpad6Key.wasPressedThisFrame)
            {
                OnEmotePressed?.Invoke(6);
            }
        }
        if(Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            OnInteractPressed?.Invoke();
        }
    }

    private void ResetThrowInputState()
    {
        m_isThrowInputPressed = false;
        m_hasThrowHoldStarted = false;
        m_throwPressedTimestamp = 0f;
    }
}
