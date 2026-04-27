using Unity.Netcode;
using UnityEngine;

public partial class PlayerController
{
    [Header("Throw")]
    [SerializeField]
    private float m_throwChargeSpeed = 1.5f;
    [SerializeField]
    private float m_minThrowSpeed = 4f;
    [SerializeField]
    private float m_maxThrowSpeed = 15f;
    [SerializeField]
    private float m_throwGravity = 18f;
    [SerializeField]
    private float m_throwSpawnForwardOffset = 0.75f;
    [SerializeField]
    private float m_throwSpawnHeight = 1.2f;
    [SerializeField]
    private float m_throwUpwardBias = 0.2f;

    [Header("Drop Placement")]
    [SerializeField, Min(0f)]
    private float m_dropPlacementRadius = 0.9f;
    [SerializeField, Min(0.05f)]
    private float m_dropClearanceRadius = 0.2f;
    [SerializeField]
    private LayerMask m_dropBlockingMask = Physics.DefaultRaycastLayers;

    [Header("Throw Animation")]
    [SerializeField]
    private string m_throwAnimationLayerName = "UpperBody";
    [SerializeField]
    private string m_throwAnimationStateName = "Throw";
    [SerializeField]
    private string m_throwAnimationStartTrigger = "ThrowStart";
    [SerializeField]
    private string m_throwAnimationSpeedParameter = "ThrowSpeed";
    [SerializeField]
    private float m_throwAnimationBlendDuration = 0.08f;
    [SerializeField]
    [Range(0f, 1f)]
    private float m_throwWindupNormalizedTime = 0.42f;

    private bool m_isChargingThrow;
    private float m_throwChargeNormalized;
    private float m_throwChargeDirection = 1f;
    private bool m_isThrowAnimationChargeActive;
    private bool m_isThrowAnimationPoseHeld;

    private int m_throwAnimationLayerIndex = -1;
    private int m_throwAnimationStateShortHash;
    private int m_throwAnimationStartTriggerHash;
    private int m_throwAnimationSpeedHash;
    private bool m_hasThrowAnimationStartTrigger;
    private bool m_hasThrowAnimationSpeedParameter;
    private bool m_hasPendingThrow;
    private Vector3 m_pendingThrowStartPosition;
    private Vector3 m_pendingThrowInitialVelocity;
    private float m_pendingThrowGravity;
    private readonly Collider[] m_dropOverlapHits = new Collider[16];

    private static readonly Vector3 ScreenCenterViewport = new Vector3(0.5f, 0.5f, 0f);

    private bool IsThrowBusy => m_isChargingThrow || m_hasPendingThrow;

    private void InitializeThrowController()
    {
        CacheThrowAnimationHashes();
        CacheThrowAnimationSetup();
    }

    private void HandleThrowStarted()
    {
        if (IsOwner == false)
        {
            return;
        }

        if (CanChargeThrow() == false)
        {
            return;
        }

        m_isChargingThrow = true;
        m_throwChargeNormalized = 0f;
        m_throwChargeDirection = 1f;
        UpdateThrowChargeUI();

        StartThrowAnimation();
        RequestThrowAnimationStartServerRpc();
    }

    private void HandleThrowReleased()
    {
        if (IsOwner == false || m_isChargingThrow == false)
        {
            return;
        }

        if (CanChargeThrow() == false)
        {
            CancelThrowCharge(true);
            return;
        }

        float throwSpeed = Mathf.Lerp(m_minThrowSpeed, m_maxThrowSpeed, m_throwChargeNormalized);
        Vector3 throwDirection = GetThrowDirection();
        throwDirection = (throwDirection + Vector3.up * m_throwUpwardBias).normalized;
        if (throwDirection.sqrMagnitude <= 0.0001f)
        {
            throwDirection = transform.forward;
        }

        Vector3 throwStartPosition = GetThrowStartPosition(throwDirection);
        Vector3 initialVelocity = throwDirection * throwSpeed;
        QueuePendingThrow(throwStartPosition, initialVelocity, m_throwGravity);
        RequestThrowAnimationReleaseServerRpc();
        ReleaseThrowAnimation();
        CancelThrowCharge(false);
    }

    private void HandleThrowTapped()
    {
        if (IsOwner == false)
        {
            return;
        }

        if (CanChargeThrow() == false)
        {
            return;
        }

        RequestDropWithPlacementServerRpc(transform.position, GetPreferredDropForward());
        PlayPickUpAnimation(ignoreInteractAction: true);
    }

    private bool DropCurrentItem()
    {
        return DropCurrentItem(transform.position, GetPreferredDropForward());
    }

    private bool DropCurrentItem(Vector3 placementOrigin, Vector3 preferredForward)
    {
        if (TryGetDropPosition(placementOrigin, preferredForward, out Vector3 dropPosition) == false)
        {
            return false;
        }

        return ReleaseHeldItem(dropPosition, Vector3.zero, 0f, false);
    }

    private void ThrowCurrentItem(Vector3 startPosition, Vector3 initialVelocity, float gravity)
    {
        ReleaseHeldItem(startPosition, initialVelocity, gravity, true);
    }

    private bool ReleaseHeldItem(
        Vector3 releasePosition,
        Vector3 initialVelocity,
        float gravity,
        bool shouldThrow)
    {
        if(IsServer == false)
        {
            return false;
        }
        if(m_heldObjectType.Value == ObjectType.None)
        {
            m_heldNetworkObjectId.Value = ulong.MaxValue;
            return false;
        }

        bool releaseSucceeded = false;

        if(m_heldObjectType.Value is ObjectType.Axe or ObjectType.PickAxe)
        {
            if(NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(
                m_heldNetworkObjectId.Value, out NetworkObject target))
            {
                if(target.TryGetComponent(out PickableTool pickableItem))
                {
                    if (shouldThrow)
                    {
                        pickableItem.ThrowAlongArc(releasePosition, initialVelocity, gravity, transform);
                    }
                    else
                    {
                        pickableItem.Drop(releasePosition);
                    }

                    releaseSucceeded = true;
                }
            }
        }
        else
        {
            if (m_resourceSpawner == null)
            {
                m_resourceSpawner = FindAnyObjectByType<ResourceSpawner>();
            }

            if (m_resourceSpawner != null)
            {
                if (shouldThrow)
                {
                    releaseSucceeded = m_resourceSpawner.SpawnThrownResource(
                        m_heldObjectType.Value,
                        releasePosition,
                        initialVelocity,
                        gravity,
                        transform);
                }
                else
                {
                    releaseSucceeded = m_resourceSpawner.SpawnResource(m_heldObjectType.Value, releasePosition);
                }
            }
        }

        if (releaseSucceeded == false)
        {
            return false;
        }

        m_heldObjectType.Value = ObjectType.None;
        m_heldNetworkObjectId.Value = ulong.MaxValue;
        return true;
    }

    [Rpc(SendTo.Server)]
    private void RequestDropWithPlacementServerRpc(Vector3 placementOrigin, Vector3 preferredForward)
    {
        DropCurrentItem(placementOrigin, preferredForward);
    }

    [Rpc(SendTo.Server)]
    private void RequestThrowServerRpc(Vector3 startPosition, Vector3 initialVelocity, float gravity)
    {
        ThrowCurrentItem(startPosition, initialVelocity, gravity);
    }

    [Rpc(SendTo.Server)]
    private void RequestThrowAnimationStartServerRpc()
    {
        StartThrowAnimationClientRpc();
    }

    [Rpc(SendTo.Server)]
    private void RequestThrowAnimationReleaseServerRpc()
    {
        ReleaseThrowAnimationClientRpc();
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void StartThrowAnimationClientRpc()
    {
        if (IsOwner)
        {
            return;
        }

        StartThrowAnimation();
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void ReleaseThrowAnimationClientRpc()
    {
        if (IsOwner)
        {
            return;
        }

        ReleaseThrowAnimation();
    }

    private void QueuePendingThrow(Vector3 startPosition, Vector3 initialVelocity, float gravity)
    {
        m_pendingThrowStartPosition = startPosition;
        m_pendingThrowInitialVelocity = initialVelocity;
        m_pendingThrowGravity = gravity;
        m_hasPendingThrow = true;
    }

    private void ClearPendingThrow()
    {
        m_hasPendingThrow = false;
        m_pendingThrowStartPosition = Vector3.zero;
        m_pendingThrowInitialVelocity = Vector3.zero;
        m_pendingThrowGravity = 0f;
    }

    private void CacheThrowAnimationHashes()
    {
        m_throwAnimationStateShortHash = Animator.StringToHash(m_throwAnimationStateName);
        m_throwAnimationStartTriggerHash = Animator.StringToHash(m_throwAnimationStartTrigger);
        m_throwAnimationSpeedHash = Animator.StringToHash(m_throwAnimationSpeedParameter);
    }

    private void CacheThrowAnimationSetup()
    {
        if (m_animator == null)
        {
            return;
        }

        m_throwAnimationLayerIndex = m_animator.GetLayerIndex(m_throwAnimationLayerName);
        m_hasThrowAnimationStartTrigger = false;
        m_hasThrowAnimationSpeedParameter = false;

        AnimatorControllerParameter[] parameters = m_animator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            AnimatorControllerParameter parameter = parameters[i];
            if (parameter.type == AnimatorControllerParameterType.Trigger &&
                parameter.nameHash == m_throwAnimationStartTriggerHash)
            {
                m_hasThrowAnimationStartTrigger = true;
            }
            else if (parameter.type == AnimatorControllerParameterType.Float &&
                parameter.nameHash == m_throwAnimationSpeedHash)
            {
                m_hasThrowAnimationSpeedParameter = true;
            }
        }
    }

    private void HandleThrowReleaseAction()
    {
        if (IsOwner == false || m_hasPendingThrow == false)
        {
            return;
        }

        if (m_heldObjectType.Value == ObjectType.None)
        {
            ClearPendingThrow();
            return;
        }

        RequestThrowServerRpc(
            m_pendingThrowStartPosition,
            m_pendingThrowInitialVelocity,
            m_pendingThrowGravity);
        ClearPendingThrow();
    }

    private void StartThrowAnimation()
    {
        CacheThrowAnimationSetup();
        if (m_animator == null || m_throwAnimationLayerIndex < 0)
        {
            return;
        }

        m_isThrowAnimationChargeActive = true;
        m_isThrowAnimationPoseHeld = false;
        SetThrowAnimationPlaybackSpeed(1f);

        if (m_hasThrowAnimationStartTrigger)
        {
            m_animator.ResetTrigger(m_throwAnimationStartTriggerHash);
            m_animator.SetTrigger(m_throwAnimationStartTriggerHash);
            return;
        }

        m_animator.CrossFadeInFixedTime(
            $"{m_throwAnimationLayerName}.{m_throwAnimationStateName}",
            m_throwAnimationBlendDuration,
            m_throwAnimationLayerIndex,
            0f);
    }

    private void ReleaseThrowAnimation()
    {
        m_isThrowAnimationChargeActive = false;
        m_isThrowAnimationPoseHeld = false;
        SetThrowAnimationPlaybackSpeed(1f);
    }

    private void UpdateThrowAnimationPlayback()
    {
        if (m_animator == null)
        {
            return;
        }

        if (m_throwAnimationLayerIndex < 0)
        {
            CacheThrowAnimationSetup();
            if (m_throwAnimationLayerIndex < 0)
            {
                return;
            }
        }

        AnimatorStateInfo throwLayerState = m_animator.GetCurrentAnimatorStateInfo(m_throwAnimationLayerIndex);
        if (throwLayerState.shortNameHash != m_throwAnimationStateShortHash)
        {
            if (m_isThrowAnimationPoseHeld)
            {
                m_isThrowAnimationPoseHeld = false;
                SetThrowAnimationPlaybackSpeed(1f);
            }

            return;
        }

        if (m_isThrowAnimationChargeActive == false)
        {
            if (m_isThrowAnimationPoseHeld)
            {
                m_isThrowAnimationPoseHeld = false;
                SetThrowAnimationPlaybackSpeed(1f);
            }

            return;
        }

        if (m_isThrowAnimationPoseHeld)
        {
            return;
        }

        float normalizedTime = throwLayerState.normalizedTime;
        normalizedTime -= Mathf.Floor(normalizedTime);

        if (normalizedTime >= m_throwWindupNormalizedTime)
        {
            m_isThrowAnimationPoseHeld = true;
            SetThrowAnimationPlaybackSpeed(0f);
        }
    }

    private void SetThrowAnimationPlaybackSpeed(float speed)
    {
        if (m_animator == null || m_hasThrowAnimationSpeedParameter == false)
        {
            return;
        }

        m_animator.SetFloat(m_throwAnimationSpeedHash, speed);
    }

    private void UpdateThrowCharge()
    {
        if (m_isChargingThrow == false)
        {
            return;
        }

        if (CanChargeThrow() == false)
        {
            CancelThrowCharge(true);
            return;
        }

        m_throwChargeNormalized += m_throwChargeDirection * (m_throwChargeSpeed * Time.deltaTime);

        if (m_throwChargeNormalized >= 1f)
        {
            m_throwChargeNormalized = 1f;
            m_throwChargeDirection = -1f;
        }
        else if (m_throwChargeNormalized <= 0f)
        {
            m_throwChargeNormalized = 0f;
            m_throwChargeDirection = 1f;
        }

        UpdateThrowChargeUI();
    }

    private bool CanChargeThrow()
    {
        return GameplayMenuState.IsMenuOpen == false &&
               m_isSeated == false &&
               m_isSeatExitRequested == false &&
               m_isEmoting == false &&
               m_isEmoteExitRequested == false &&
               m_isInteracting == false &&
               m_isChopping == false &&
               m_hasPendingThrow == false &&
               m_heldObjectType.Value != ObjectType.None;
    }

    private void CancelThrowCharge(bool notifyNetworkRelease)
    {
        bool wasCharging = m_isChargingThrow;
        m_isChargingThrow = false;
        m_throwChargeNormalized = 0f;
        m_throwChargeDirection = 1f;
        ReleaseThrowAnimation();
        UpdateThrowChargeUI();

        if (notifyNetworkRelease && wasCharging && IsOwner && IsSpawned)
        {
            RequestThrowAnimationReleaseServerRpc();
        }
    }

    private void UpdateThrowChargeUI()
    {
        if (IsOwner == false)
        {
            return;
        }

        GameUI.Instance?.SetThrowCharge(m_throwChargeNormalized, m_isChargingThrow);
    }

    private Vector3 GetThrowDirection()
    {
        Camera mainCamera = Camera.main;
        if (mainCamera != null)
        {
            return mainCamera.ViewportPointToRay(ScreenCenterViewport).direction.normalized;
        }

        if (m_cameraFollow != null)
        {
            return m_cameraFollow.PlanarForward;
        }

        return transform.forward;
    }

    private Vector3 GetThrowStartPosition(Vector3 throwDirection)
    {
        Vector3 planarForward = Vector3.ProjectOnPlane(throwDirection, Vector3.up);
        if (planarForward.sqrMagnitude <= 0.0001f)
        {
            planarForward = m_cameraFollow != null ? m_cameraFollow.PlanarForward : transform.forward;
        }

        planarForward.Normalize();
        return transform.position +
               Vector3.up * m_throwSpawnHeight +
               planarForward * m_throwSpawnForwardOffset;
    }

    private bool TryGetDropPosition(Vector3 placementOrigin, Vector3 preferredForward, out Vector3 dropPosition)
    {
        Vector3 playerPosition = placementOrigin;
        float searchRadius = Mathf.Max(0f, m_dropPlacementRadius);
        if (searchRadius <= 0.001f)
        {
            if (IsDropPositionValid(playerPosition, playerPosition))
            {
                dropPosition = playerPosition;
                return true;
            }

            dropPosition = Vector3.zero;
            return false;
        }

        Vector3 forward = GetDropForward(preferredForward);
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        if (right.sqrMagnitude <= 0.0001f)
        {
            right = Vector3.right;
        }

        right.Normalize();

        float farRadius = searchRadius;
        float midRadius = searchRadius * 0.72f;
        float nearRadius = searchRadius * 0.48f;

        if (TryGetDropCandidate(playerPosition, forward, right, farRadius, 1f, 0f, out dropPosition)) return true;
        if (TryGetDropCandidate(playerPosition, forward, right, farRadius, 1f, 0.55f, out dropPosition)) return true;
        if (TryGetDropCandidate(playerPosition, forward, right, farRadius, 1f, -0.55f, out dropPosition)) return true;
        if (TryGetDropCandidate(playerPosition, forward, right, midRadius, 1f, 0f, out dropPosition)) return true;
        if (TryGetDropCandidate(playerPosition, forward, right, midRadius, 0.8f, 0.65f, out dropPosition)) return true;
        if (TryGetDropCandidate(playerPosition, forward, right, midRadius, 0.8f, -0.65f, out dropPosition)) return true;
        if (TryGetDropCandidate(playerPosition, forward, right, nearRadius, 0f, 1f, out dropPosition)) return true;
        if (TryGetDropCandidate(playerPosition, forward, right, nearRadius, 0f, -1f, out dropPosition)) return true;
        if (TryGetDropCandidate(playerPosition, forward, right, nearRadius, -0.45f, 0.8f, out dropPosition)) return true;
        if (TryGetDropCandidate(playerPosition, forward, right, nearRadius, -0.45f, -0.8f, out dropPosition)) return true;
        if (TryGetDropCandidate(playerPosition, forward, right, nearRadius, -1f, 0f, out dropPosition)) return true;

        if (IsDropPositionValid(playerPosition, playerPosition))
        {
            dropPosition = playerPosition;
            return true;
        }

        dropPosition = Vector3.zero;
        return false;
    }

    private bool TryGetDropCandidate(
        Vector3 playerPosition,
        Vector3 forward,
        Vector3 right,
        float radius,
        float forwardWeight,
        float rightWeight,
        out Vector3 dropPosition)
    {
        Vector3 direction = forward * forwardWeight + right * rightWeight;
        if (direction.sqrMagnitude <= 0.0001f)
        {
            dropPosition = Vector3.zero;
            return false;
        }

        direction.Normalize();
        Vector3 candidatePosition = playerPosition + direction * radius;
        if (IsDropPositionValid(playerPosition, candidatePosition))
        {
            dropPosition = candidatePosition;
            return true;
        }

        dropPosition = Vector3.zero;
        return false;
    }

    private Vector3 GetDropForward(Vector3 sourceForward)
    {
        Vector3 forward = Vector3.ProjectOnPlane(sourceForward, Vector3.up);
        if (forward.sqrMagnitude <= 0.0001f)
        {
            forward = Quaternion.Euler(0f, transform.eulerAngles.y, 0f) * Vector3.forward;
        }

        return forward.normalized;
    }

    private Vector3 GetPreferredDropForward()
    {
        if (IsOwner && m_cameraFollow != null)
        {
            Vector3 cameraPlanarForward = m_cameraFollow.PlanarForward;
            if (cameraPlanarForward.sqrMagnitude > 0.0001f)
            {
                return cameraPlanarForward.normalized;
            }
        }

        return GetDropForward(transform.forward);
    }

    private bool IsDropPositionValid(Vector3 playerPosition, Vector3 candidatePosition)
    {
        float checkRadius = Mathf.Max(0.05f, m_dropClearanceRadius);

        Vector3 castOrigin = playerPosition + Vector3.up * 0.45f;
        Vector3 castTarget = candidatePosition + Vector3.up * 0.45f;
        Vector3 castDirection = castTarget - castOrigin;
        float castDistance = castDirection.magnitude;
        if (castDistance > 0.0001f)
        {
            RaycastHit[] castHits = Physics.SphereCastAll(
                castOrigin,
                checkRadius * 0.65f,
                castDirection / castDistance,
                castDistance,
                m_dropBlockingMask,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < castHits.Length; i++)
            {
                RaycastHit castHit = castHits[i];
                if (castHit.collider == null || castHit.collider.transform.root == transform)
                {
                    continue;
                }

                return false;
            }
        }

        Vector3 overlapCenter = candidatePosition + Vector3.up * (checkRadius + 0.05f);
        int hitCount = Physics.OverlapSphereNonAlloc(
            overlapCenter,
            checkRadius,
            m_dropOverlapHits,
            m_dropBlockingMask,
            QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hitCount; i++)
        {
            Collider hitCollider = m_dropOverlapHits[i];
            if (hitCollider == null || hitCollider.transform.root == transform)
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
