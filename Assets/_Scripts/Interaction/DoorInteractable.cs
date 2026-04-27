using System.Collections;
using Unity.Netcode;
using UnityEngine;

public class DoorInteractable : NetworkBehaviour, IInteractable
{
    [Header("References")]
    [SerializeField] private SelectionOutline m_outline;
    [SerializeField] private Transform m_doorTransform;

    [Header("Animation")]
    [SerializeField, Min(1f)] private float m_openAngle = 95f;
    [SerializeField, Min(0.05f)] private float m_animationDuration = 0.32f;
    [SerializeField] private AnimationCurve m_motionCurve = new(
        new Keyframe(0f, 0f, 0f, 2.8f),
        new Keyframe(0.72f, 1.08f, 0f, 0f),
        new Keyframe(1f, 1f, -0.35f, 0f));
    [SerializeField] private bool m_invertOpenDirection;

    private readonly NetworkVariable<float> m_targetLocalYaw = new(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private Quaternion m_closedLocalRotation;
    private Coroutine m_animationRoutine;

    private void Awake()
    {
        if (m_doorTransform == null)
        {
            m_doorTransform = transform;
        }

        m_closedLocalRotation = m_doorTransform.localRotation;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_targetLocalYaw.OnValueChanged += HandleTargetYawChanged;
        ApplyTargetRotationImmediate(m_targetLocalYaw.Value);
    }

    public override void OnNetworkDespawn()
    {
        if (m_animationRoutine != null)
        {
            StopCoroutine(m_animationRoutine);
            m_animationRoutine = null;
        }

        m_targetLocalYaw.OnValueChanged -= HandleTargetYawChanged;
        base.OnNetworkDespawn();
    }

    public void ToggleSelection(bool isSelected)
    {
        if (m_outline != null)
        {
            m_outline.ToggleOutline(isSelected);
        }
    }

    public void ToggleDoorServer(Vector3 interactorWorldPosition)
    {
        if (IsServer == false)
        {
            return;
        }

        bool isOpen = Mathf.Abs(m_targetLocalYaw.Value) > 0.01f;
        if (isOpen)
        {
            m_targetLocalYaw.Value = 0f;
            return;
        }

        float openDirectionSign = GetOpenDirectionSign(interactorWorldPosition);
        m_targetLocalYaw.Value = openDirectionSign * Mathf.Abs(m_openAngle);
    }

    private float GetOpenDirectionSign(Vector3 interactorWorldPosition)
    {
        Vector3 toInteractor = interactorWorldPosition - m_doorTransform.position;
        float sideDot = Vector3.Dot(m_doorTransform.forward, toInteractor);
        float openDirectionSign = sideDot >= 0f ? -1f : 1f;

        if (m_invertOpenDirection)
        {
            openDirectionSign *= -1f;
        }

        return openDirectionSign;
    }

    private void HandleTargetYawChanged(float previousValue, float newValue)
    {
        AnimateToTargetYaw(newValue);
    }

    private void AnimateToTargetYaw(float targetYaw)
    {
        if (m_doorTransform == null)
        {
            return;
        }

        Quaternion targetRotation = m_closedLocalRotation * Quaternion.Euler(0f, targetYaw, 0f);
        if (m_animationRoutine != null)
        {
            StopCoroutine(m_animationRoutine);
            m_animationRoutine = null;
        }

        if (m_animationDuration <= 0.001f || Quaternion.Angle(m_doorTransform.localRotation, targetRotation) <= 0.01f)
        {
            m_doorTransform.localRotation = targetRotation;
            return;
        }

        m_animationRoutine = StartCoroutine(AnimateDoorRoutine(targetRotation));
    }

    private IEnumerator AnimateDoorRoutine(Quaternion targetRotation)
    {
        Quaternion startRotation = m_doorTransform.localRotation;
        float elapsed = 0f;
        while (elapsed < m_animationDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / m_animationDuration);
            float easedT = EvaluateMotionCurve(t);
            m_doorTransform.localRotation = Quaternion.SlerpUnclamped(startRotation, targetRotation, easedT);
            yield return null;
        }

        m_doorTransform.localRotation = targetRotation;
        m_animationRoutine = null;
    }

    private float EvaluateMotionCurve(float t)
    {
        if (m_motionCurve == null || m_motionCurve.length == 0)
        {
            return t;
        }

        return m_motionCurve.Evaluate(t);
    }

    private void ApplyTargetRotationImmediate(float targetYaw)
    {
        if (m_doorTransform == null)
        {
            return;
        }

        m_doorTransform.localRotation = m_closedLocalRotation * Quaternion.Euler(0f, targetYaw, 0f);
    }
}
