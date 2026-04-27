using System.Collections;
using Unity.Netcode;
using UnityEngine;

public partial class PlayerController
{
    [Header("Seating")]
    [SerializeField] private string m_sitDownStateName = "Sit_Down";
    [SerializeField] private string m_sitDownFallbackStateName = "Sit_Chair_Down";
    [SerializeField] private string m_sitIdleStateName = "Sit_Down_Idle";
    [SerializeField] private string m_sitIdleFallbackStateName = "Sit_Chair_Idle";
    [SerializeField] private string m_standUpStateName = "Sit_Down_StandUp";
    [SerializeField] private string m_standUpFallbackStateName = "Sit_Chair_StandUp";
    [SerializeField, Min(0f)] private float m_seatAnimationCrossFadeDuration = 0.08f;
    [SerializeField, Min(0f)] private float m_seatExitInputThreshold = 0.1f;
    [SerializeField, Range(0.1f, 1f)] private float m_seatStandUpUnlockNormalizedTime = 0.8f;
    [SerializeField] private bool m_disableCharacterControllerWhileSeated = true;

    private const ulong NoSeatNetworkObjectId = ulong.MaxValue;
    private const int InvalidSeatSlotIndex = -1;

    private CharacterController m_characterController;
    private SeatInteractable m_currentSeat;
    private int m_currentSeatSlotIndex = InvalidSeatSlotIndex;
    private bool m_isSeated;
    private bool m_isSeatExitRequested;
    private bool m_characterControllerWasEnabledBeforeSeat;
    private Coroutine m_switchToSitIdleCoroutine;
    private Coroutine m_releaseSeatExitCoroutine;

    private int m_sitDownStateHash;
    private int m_sitIdleStateHash;
    private int m_standUpStateHash;
    private bool m_hasSitDownState;
    private bool m_hasSitIdleState;
    private bool m_hasStandUpState;
    private float m_sitDownClipLength;
    private float m_standUpClipLength;

    private SeatInteractable m_serverSeat;

    private void InitializeSeatController()
    {
        m_characterController = GetComponent<CharacterController>();
        CacheSeatAnimationSetup();
    }

    private void HandleSeatOnNetworkSpawn()
    {
        CacheSeatAnimationSetup();
    }

    private void HandleSeatOnDisable()
    {
        StopSeatIdleRoutine();
        StopSeatExitRoutine();
        ExitSeatLocal(playStandAnimation: false);
        m_isSeatExitRequested = false;
    }

    private void HandleSeatOnNetworkDespawn()
    {
        if (IsServer)
        {
            ReleaseCurrentSeatServer(out _, out _);
        }

        StopSeatIdleRoutine();
        StopSeatExitRoutine();
        ExitSeatLocal(playStandAnimation: false);
        m_isSeatExitRequested = false;
    }

    private bool IsSeatBlockingGameplay()
    {
        return m_isSeated || m_isSeatExitRequested;
    }

    private void RequestSeatInteraction(ulong seatNetworkObjectId)
    {
        if (IsSeatBlockingGameplay() || IsEmoteBlockingGameplay())
        {
            return;
        }

        RequestSeatInteractionServerRpc(seatNetworkObjectId);
    }

    private bool HandleSeatMovementInput(ref Vector2 movementInput)
    {
        if (m_isSeated == false)
        {
            if (m_isSeatExitRequested)
            {
                movementInput = Vector2.zero;
                return true;
            }

            return false;
        }

        float threshold = Mathf.Max(0f, m_seatExitInputThreshold);
        if (m_isSeatExitRequested == false &&
            movementInput.sqrMagnitude > threshold * threshold)
        {
            m_isSeatExitRequested = true;
            RequestLeaveSeatServerRpc();
        }

        movementInput = Vector2.zero;
        return true;
    }

    [Rpc(SendTo.Server)]
    private void RequestSeatInteractionServerRpc(ulong seatNetworkObjectId)
    {
        if (NetworkManager == null || NetworkManager.SpawnManager == null)
        {
            return;
        }

        if (NetworkManager.SpawnManager.SpawnedObjects
            .TryGetValue(seatNetworkObjectId, out NetworkObject seatObject) == false)
        {
            return;
        }

        if (seatObject.TryGetComponent(out SeatInteractable seatInteractable) == false)
        {
            return;
        }

        if (m_serverSeat != null && m_serverSeat != seatInteractable)
        {
            ReleaseCurrentSeatServer(out _, out _);
        }

        if (seatInteractable.TryOccupySeatServer(NetworkObject.NetworkObjectId, out int slotIndex) == false)
        {
            return;
        }

        m_serverSeat = seatInteractable;
        ApplySeatStateClientRpc(seatNetworkObjectId, slotIndex);
    }

    [Rpc(SendTo.Server)]
    private void RequestLeaveSeatServerRpc()
    {
        if (ReleaseCurrentSeatServer(out _, out _))
        {
            ApplySeatStateClientRpc(NoSeatNetworkObjectId, InvalidSeatSlotIndex);
        }
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void ApplySeatStateClientRpc(ulong seatNetworkObjectId, int seatSlotIndex)
    {
        if (seatNetworkObjectId == NoSeatNetworkObjectId || seatSlotIndex < 0)
        {
            ExitSeatLocal(playStandAnimation: true);
            return;
        }

        if (TryGetSeatInteractable(seatNetworkObjectId, out SeatInteractable seatInteractable) == false)
        {
            ExitSeatLocal(playStandAnimation: true);
            return;
        }

        EnterSeatLocal(seatInteractable, seatSlotIndex);
    }

    private bool ReleaseCurrentSeatServer(out ulong releasedSeatNetworkObjectId, out int releasedSeatSlotIndex)
    {
        releasedSeatNetworkObjectId = NoSeatNetworkObjectId;
        releasedSeatSlotIndex = InvalidSeatSlotIndex;

        if (IsServer == false || m_serverSeat == null)
        {
            return false;
        }

        releasedSeatNetworkObjectId = m_serverSeat.NetworkObject.NetworkObjectId;
        bool released = m_serverSeat.ReleaseSeatServer(
            NetworkObject.NetworkObjectId,
            out releasedSeatSlotIndex);

        m_serverSeat = null;
        return released;
    }

    private bool TryGetSeatInteractable(ulong seatNetworkObjectId, out SeatInteractable seatInteractable)
    {
        seatInteractable = null;

        if (NetworkManager == null || NetworkManager.SpawnManager == null)
        {
            return false;
        }

        if (NetworkManager.SpawnManager.SpawnedObjects
            .TryGetValue(seatNetworkObjectId, out NetworkObject seatObject) == false)
        {
            return false;
        }

        return seatObject.TryGetComponent(out seatInteractable);
    }

    private void EnterSeatLocal(SeatInteractable seatInteractable, int seatSlotIndex)
    {
        if (seatInteractable == null)
        {
            return;
        }

        Transform seatAttachPoint = seatInteractable.GetSeatAttachPoint(seatSlotIndex);
        if (seatAttachPoint == null)
        {
            return;
        }

        if (m_isSeated && (m_currentSeat != seatInteractable || m_currentSeatSlotIndex != seatSlotIndex))
        {
            ExitSeatLocal(playStandAnimation: false);
        }
        else if (m_isSeated && m_currentSeat == seatInteractable && m_currentSeatSlotIndex == seatSlotIndex)
        {
            m_isSeatExitRequested = false;
            return;
        }

        StopSeatIdleRoutine();
        StopSeatExitRoutine();

        m_currentSeat = seatInteractable;
        m_currentSeatSlotIndex = seatSlotIndex;
        m_isSeated = true;
        m_isSeatExitRequested = false;
        if (m_cameraFollow != null)
        {
            m_cameraFollow.SetFirstPersonBodyRotationEnabled(false);
        }
        if (m_interactionDetector != null)
        {
            m_interactionDetector.SetDetectionEnabled(false);
        }

        m_isChopping = false;
        m_isInteracting = false;
        ClearPendingThrow();
        CancelThrowCharge(IsOwner);

        if (m_characterController == null)
        {
            m_characterController = GetComponent<CharacterController>();
        }

        if (m_disableCharacterControllerWhileSeated && m_characterController != null)
        {
            m_characterControllerWasEnabledBeforeSeat = m_characterController.enabled;
            m_characterController.enabled = false;
        }

        SnapToSeatAttachPoint(seatAttachPoint);

        PlaySitDownAnimation();
    }

    private void ExitSeatLocal(bool playStandAnimation)
    {
        bool wasSeated = m_isSeated;
        StopSeatIdleRoutine();
        StopSeatExitRoutine();

        m_currentSeat = null;
        m_currentSeatSlotIndex = InvalidSeatSlotIndex;
        m_isSeated = false;
        m_isSeatExitRequested = false;
        if (m_cameraFollow != null)
        {
            m_cameraFollow.SetFirstPersonBodyRotationEnabled(true);
        }
        if (m_interactionDetector != null)
        {
            m_interactionDetector.SetDetectionEnabled(true);
        }

        if (m_disableCharacterControllerWhileSeated &&
            m_characterController != null &&
            m_characterControllerWasEnabledBeforeSeat)
        {
            m_characterController.enabled = true;
        }

        m_characterControllerWasEnabledBeforeSeat = false;

        if (wasSeated && playStandAnimation)
        {
            if (PlayStandUpAnimation())
            {
                m_isSeatExitRequested = true;
                float waitDuration = m_standUpClipLength *
                    Mathf.Clamp(m_seatStandUpUnlockNormalizedTime, 0.1f, 1f);
                if (waitDuration > 0.01f)
                {
                    m_releaseSeatExitCoroutine = StartCoroutine(
                        ReleaseSeatExitAfterDelay(waitDuration));
                }
                else
                {
                    m_isSeatExitRequested = false;
                }
            }
        }
    }

    private void UpdateSeatedPose()
    {
        if (m_isSeated == false || m_currentSeat == null || m_currentSeatSlotIndex < 0)
        {
            return;
        }

        Transform seatAttachPoint = m_currentSeat.GetSeatAttachPoint(m_currentSeatSlotIndex);
        if (seatAttachPoint == null)
        {
            return;
        }

        SnapToSeatAttachPoint(seatAttachPoint);
    }

    private void SnapToSeatAttachPoint(Transform seatAttachPoint)
    {
        if (seatAttachPoint == null)
        {
            return;
        }

        transform.SetPositionAndRotation(seatAttachPoint.position, seatAttachPoint.rotation);
    }

    private void CacheSeatAnimationSetup()
    {
        if (m_animator == null)
        {
            return;
        }

        m_hasSitDownState = TryResolveAnimatorState(
            0,
            m_sitDownStateName,
            m_sitDownFallbackStateName,
            out m_sitDownStateHash,
            out string sitDownResolvedName);

        m_hasSitIdleState = TryResolveAnimatorState(
            0,
            m_sitIdleStateName,
            m_sitIdleFallbackStateName,
            out m_sitIdleStateHash,
            out _);

        m_hasStandUpState = TryResolveAnimatorState(
            0,
            m_standUpStateName,
            m_standUpFallbackStateName,
            out m_standUpStateHash,
            out string standUpResolvedName);

        m_sitDownClipLength = GetAnimationClipLength(sitDownResolvedName);
        m_standUpClipLength = GetAnimationClipLength(standUpResolvedName);
    }

    private bool TryResolveAnimatorState(
        int layerIndex,
        string preferredStateName,
        string fallbackStateName,
        out int stateHash,
        out string resolvedStateName)
    {
        resolvedStateName = null;
        stateHash = 0;

        if (m_animator == null)
        {
            return false;
        }

        if (TryFindAnimatorState(layerIndex, preferredStateName, out stateHash))
        {
            resolvedStateName = preferredStateName;
            return true;
        }

        if (TryFindAnimatorState(layerIndex, fallbackStateName, out stateHash))
        {
            resolvedStateName = fallbackStateName;
            return true;
        }

        return false;
    }

    private bool TryFindAnimatorState(int layerIndex, string stateName, out int stateHash)
    {
        stateHash = 0;
        if (string.IsNullOrWhiteSpace(stateName))
        {
            return false;
        }

        stateHash = Animator.StringToHash(stateName);
        return m_animator.HasState(layerIndex, stateHash);
    }

    private float GetAnimationClipLength(string clipName)
    {
        if (string.IsNullOrWhiteSpace(clipName) || m_animator == null || m_animator.runtimeAnimatorController == null)
        {
            return 0f;
        }

        AnimationClip[] clips = m_animator.runtimeAnimatorController.animationClips;
        for (int i = 0; i < clips.Length; i++)
        {
            AnimationClip clip = clips[i];
            if (clip == null || clip.name != clipName)
            {
                continue;
            }

            return clip.length;
        }

        return 0f;
    }

    private void PlaySitDownAnimation()
    {
        CacheSeatAnimationSetup();
        if (m_animator == null)
        {
            return;
        }

        if (m_hasSitDownState)
        {
            m_animator.CrossFadeInFixedTime(
                m_sitDownStateHash,
                m_seatAnimationCrossFadeDuration,
                0,
                0f);

            if (m_hasSitIdleState)
            {
                float waitDuration = m_sitDownClipLength;
                if (waitDuration > 0.01f)
                {
                    m_switchToSitIdleCoroutine = StartCoroutine(
                        SwitchToSitIdleAfterDelay(waitDuration));
                }
            }

            return;
        }

        if (m_hasSitIdleState)
        {
            m_animator.CrossFadeInFixedTime(
                m_sitIdleStateHash,
                m_seatAnimationCrossFadeDuration,
                0,
                0f);
        }
    }

    private bool PlayStandUpAnimation()
    {
        CacheSeatAnimationSetup();
        if (m_animator == null || m_hasStandUpState == false)
        {
            return false;
        }

        m_animator.CrossFadeInFixedTime(
            m_standUpStateHash,
            m_seatAnimationCrossFadeDuration,
            0,
            0f);

        return true;
    }

    private IEnumerator SwitchToSitIdleAfterDelay(float delay)
    {
        yield return new WaitForSeconds(Mathf.Max(0f, delay));
        m_switchToSitIdleCoroutine = null;

        if (m_isSeated == false || m_hasSitIdleState == false || m_animator == null)
        {
            yield break;
        }

        m_animator.CrossFadeInFixedTime(
            m_sitIdleStateHash,
            m_seatAnimationCrossFadeDuration,
            0,
            0f);
    }

    private void StopSeatIdleRoutine()
    {
        if (m_switchToSitIdleCoroutine == null)
        {
            return;
        }

        StopCoroutine(m_switchToSitIdleCoroutine);
        m_switchToSitIdleCoroutine = null;
    }

    private IEnumerator ReleaseSeatExitAfterDelay(float delay)
    {
        yield return new WaitForSeconds(Mathf.Max(0f, delay));
        m_releaseSeatExitCoroutine = null;
        m_isSeatExitRequested = false;
    }

    private void StopSeatExitRoutine()
    {
        if (m_releaseSeatExitCoroutine == null)
        {
            return;
        }

        StopCoroutine(m_releaseSeatExitCoroutine);
        m_releaseSeatExitCoroutine = null;
    }
}
