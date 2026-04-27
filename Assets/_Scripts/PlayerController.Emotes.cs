using System.Collections;
using Unity.Netcode;
using UnityEngine;

public partial class PlayerController
{
    [Header("Emotes")]
    [SerializeField, Min(0f)] private float m_emoteAnimationCrossFadeDuration = 0.08f;
    [SerializeField, Min(0f)] private float m_emoteExitInputThreshold = 0.1f;
    [SerializeField] private string m_locomotionStateName = "Blend Tree";
    [SerializeField] private string m_locomotionFallbackStateName = "Move";
    [SerializeField] private string m_cheeringStateName = "Cheering";
    [SerializeField] private string m_wavingStateName = "Waving";
    [SerializeField] private string m_lieDownStateName = "Lie_Down";
    [SerializeField] private string m_lieIdleStateName = "Lie_Idle";
    [SerializeField] private string m_lieStandUpStateName = "Lie_StandUp";
    [SerializeField] private string m_sitFloorDownStateName = "Sit_Floor_Down";
    [SerializeField] private string m_sitFloorDownFallbackStateName = "Sit_Down";
    [SerializeField] private string m_sitFloorIdleStateName = "Sit_Floor_Idle";
    [SerializeField] private string m_sitFloorIdleFallbackStateName = "Sit_Down_Idle";
    [SerializeField] private string m_sitFloorStandUpStateName = "Sit_Floor_StandUp";
    [SerializeField] private string m_sitFloorStandUpFallbackStateName = "Sit_Down_StandUp";
    [SerializeField] private string m_sitUpsStateName = "Sit_Ups";
    [SerializeField] private string m_pushUpsStateName = "Push_Ups";
    [SerializeField, Range(0.1f, 1f)] private float m_emoteStandUpUnlockNormalizedTime = 0.8f;
    [SerializeField] private bool m_disableCharacterControllerWhileLieSitEmote = true;

    private enum EmoteType
    {
        None = 0,
        Cheering = 1,
        Waving = 2,
        LieDown = 3,
        SitFloor = 4,
        SitUps = 5,
        PushUps = 6
    }

    private bool m_isEmoting;
    private bool m_isEmoteExitRequested;
    private bool m_isEmoteStandUpInProgress;
    private bool m_isCharacterControllerLockedByEmote;
    private bool m_characterControllerWasEnabledBeforeEmote;
    private EmoteType m_activeEmote = EmoteType.None;
    private Coroutine m_switchToEmoteIdleCoroutine;
    private Coroutine m_returnToLocomotionCoroutine;
    private Coroutine m_autoStopEmoteCoroutine;

    private int m_locomotionStateHash;
    private int m_cheeringStateHash;
    private int m_wavingStateHash;
    private int m_lieDownStateHash;
    private int m_lieIdleStateHash;
    private int m_lieStandUpStateHash;
    private int m_sitFloorDownStateHash;
    private int m_sitFloorIdleStateHash;
    private int m_sitFloorStandUpStateHash;
    private int m_sitUpsStateHash;
    private int m_pushUpsStateHash;

    private bool m_hasLocomotionState;
    private bool m_hasCheeringState;
    private bool m_hasWavingState;
    private bool m_hasLieDownState;
    private bool m_hasLieIdleState;
    private bool m_hasLieStandUpState;
    private bool m_hasSitFloorDownState;
    private bool m_hasSitFloorIdleState;
    private bool m_hasSitFloorStandUpState;
    private bool m_hasSitUpsState;
    private bool m_hasPushUpsState;

    private float m_lieDownClipLength;
    private float m_lieStandUpClipLength;
    private float m_sitFloorDownClipLength;
    private float m_sitFloorStandUpClipLength;
    private float m_cheeringClipLength;
    private float m_wavingClipLength;

    private void InitializeEmoteController()
    {
        CacheEmoteAnimationSetup();
    }

    private void HandleEmoteOnNetworkSpawn()
    {
        CacheEmoteAnimationSetup();
    }

    private void HandleEmoteOnDisable()
    {
        StopEmoteRoutines();
        ExitEmoteLocal(playStandUpAnimation: false, restoreLocomotion: false);
        ReleaseCharacterControllerLockFromEmote();
        m_isEmoteExitRequested = false;
        m_isEmoteStandUpInProgress = false;
    }

    private void HandleEmoteOnNetworkDespawn()
    {
        StopEmoteRoutines();
        ExitEmoteLocal(playStandUpAnimation: false, restoreLocomotion: false);
        ReleaseCharacterControllerLockFromEmote();
        m_isEmoteExitRequested = false;
        m_isEmoteStandUpInProgress = false;
    }

    private bool IsEmoteBlockingGameplay()
    {
        return m_isEmoting || m_isEmoteExitRequested || m_isEmoteStandUpInProgress;
    }

    private void HandleEmotePressed(int emoteKey)
    {
        if (IsOwner == false)
        {
            return;
        }

        EmoteType emoteType = ConvertEmoteKeyToType(emoteKey);
        if (emoteType == EmoteType.None)
        {
            return;
        }

        if (GameplayMenuState.IsMenuOpen ||
            m_isInteracting ||
            m_isChopping ||
            IsThrowBusy ||
            IsSeatBlockingGameplay())
        {
            return;
        }

        if (m_isEmoting && m_activeEmote == emoteType)
        {
            if (m_isEmoteExitRequested == false)
            {
                m_isEmoteExitRequested = true;
                RequestStopEmoteServerRpc();
            }

            return;
        }

        RequestPlayEmoteServerRpc((int)emoteType);
    }

    private static EmoteType ConvertEmoteKeyToType(int emoteKey)
    {
        return emoteKey switch
        {
            1 => EmoteType.Cheering,
            2 => EmoteType.Waving,
            3 => EmoteType.LieDown,
            4 => EmoteType.SitFloor,
            5 => EmoteType.SitUps,
            6 => EmoteType.PushUps,
            _ => EmoteType.None
        };
    }

    private static bool TryParseEmoteType(int rawValue, out EmoteType emoteType)
    {
        emoteType = ConvertEmoteKeyToType(rawValue);
        if (rawValue == (int)EmoteType.None)
        {
            emoteType = EmoteType.None;
            return true;
        }

        return emoteType != EmoteType.None;
    }

    [Rpc(SendTo.Server)]
    private void RequestPlayEmoteServerRpc(int rawEmoteType)
    {
        if (TryParseEmoteType(rawEmoteType, out EmoteType emoteType) == false ||
            emoteType == EmoteType.None)
        {
            return;
        }

        if (m_serverSeat != null)
        {
            return;
        }

        ApplyEmoteStateClientRpc(rawEmoteType);
    }

    [Rpc(SendTo.Server)]
    private void RequestStopEmoteServerRpc()
    {
        ApplyEmoteStateClientRpc((int)EmoteType.None);
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void ApplyEmoteStateClientRpc(int rawEmoteType)
    {
        if (TryParseEmoteType(rawEmoteType, out EmoteType emoteType) == false)
        {
            return;
        }

        if (emoteType == EmoteType.None)
        {
            ExitEmoteLocal(playStandUpAnimation: true);
            return;
        }

        EnterEmoteLocal(emoteType);
    }

    private bool HandleEmoteMovementInput(ref Vector2 movementInput)
    {
        if (m_isEmoting == false)
        {
            if (m_isEmoteExitRequested || m_isEmoteStandUpInProgress)
            {
                movementInput = Vector2.zero;
                return true;
            }

            return false;
        }

        float threshold = Mathf.Max(0f, m_emoteExitInputThreshold);
        if (m_isEmoteExitRequested == false &&
            movementInput.sqrMagnitude > threshold * threshold)
        {
            m_isEmoteExitRequested = true;
            RequestStopEmoteServerRpc();
        }

        movementInput = Vector2.zero;
        return true;
    }

    private void EnterEmoteLocal(EmoteType emoteType)
    {
        if (emoteType == EmoteType.None || m_isSeated || m_isSeatExitRequested)
        {
            return;
        }

        CacheEmoteAnimationSetup();
        StopEmoteRoutines();

        if (m_isEmoting && m_activeEmote == emoteType)
        {
            m_isEmoteExitRequested = false;
            return;
        }

        m_isEmoting = true;
        m_isEmoteExitRequested = false;
        m_isEmoteStandUpInProgress = false;
        m_activeEmote = emoteType;

        if (ShouldLockCharacterControllerForEmote(emoteType))
        {
            TryLockCharacterControllerForEmote();
        }
        else
        {
            ReleaseCharacterControllerLockFromEmote();
        }

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

        if (TryPlayEmoteStart(emoteType) == false)
        {
            ExitEmoteLocal(playStandUpAnimation: false);
        }
    }

    private void ExitEmoteLocal(bool playStandUpAnimation)
    {
        ExitEmoteLocal(playStandUpAnimation, restoreLocomotion: true);
    }

    private void ExitEmoteLocal(bool playStandUpAnimation, bool restoreLocomotion)
    {
        bool wasEmoting = m_isEmoting;
        EmoteType previousEmote = m_activeEmote;

        m_isEmoting = false;
        m_isEmoteExitRequested = false;
        m_isEmoteStandUpInProgress = false;
        m_activeEmote = EmoteType.None;

        StopEmoteRoutines();

        if (m_cameraFollow != null)
        {
            m_cameraFollow.SetFirstPersonBodyRotationEnabled(true);
        }

        if (m_interactionDetector != null)
        {
            m_interactionDetector.SetDetectionEnabled(true);
        }

        bool shouldKeepControllerLockedForStandUp =
            playStandUpAnimation &&
            ShouldLockCharacterControllerForEmote(previousEmote);

        if (shouldKeepControllerLockedForStandUp == false)
        {
            ReleaseCharacterControllerLockFromEmote();
        }

        if (wasEmoting == false || restoreLocomotion == false)
        {
            return;
        }

        if (playStandUpAnimation && TryPlayEmoteStandUp(previousEmote))
        {
            return;
        }

        ReleaseCharacterControllerLockFromEmote();
        PlayLocomotionState();
    }

    private bool TryPlayEmoteStart(EmoteType emoteType)
    {
        return emoteType switch
        {
            EmoteType.Cheering => TryPlayOneShotState(
                m_hasCheeringState,
                m_cheeringStateHash,
                m_cheeringClipLength,
                EmoteType.Cheering),
            EmoteType.Waving => TryPlayOneShotState(
                m_hasWavingState,
                m_wavingStateHash,
                m_wavingClipLength,
                EmoteType.Waving),
            EmoteType.LieDown => TryPlayTransitionEmote(
                m_hasLieDownState,
                m_lieDownStateHash,
                m_lieDownClipLength,
                m_hasLieIdleState,
                m_lieIdleStateHash,
                EmoteType.LieDown),
            EmoteType.SitFloor => TryPlayTransitionEmote(
                m_hasSitFloorDownState,
                m_sitFloorDownStateHash,
                m_sitFloorDownClipLength,
                m_hasSitFloorIdleState,
                m_sitFloorIdleStateHash,
                EmoteType.SitFloor),
            EmoteType.SitUps => TryPlayState(m_hasSitUpsState, m_sitUpsStateHash),
            EmoteType.PushUps => TryPlayState(m_hasPushUpsState, m_pushUpsStateHash),
            _ => false
        };
    }

    private bool TryPlayTransitionEmote(
        bool hasDownState,
        int downStateHash,
        float downClipLength,
        bool hasIdleState,
        int idleStateHash,
        EmoteType emoteType)
    {
        if (hasDownState == false && hasIdleState == false)
        {
            return false;
        }

        if (hasDownState)
        {
            CrossFadeBaseLayerState(downStateHash);

            if (hasIdleState)
            {
                if (downClipLength > 0.01f)
                {
                    m_switchToEmoteIdleCoroutine = StartCoroutine(
                        SwitchToEmoteIdleAfterDelay(downClipLength, idleStateHash, emoteType));
                }
                else
                {
                    CrossFadeBaseLayerState(idleStateHash);
                }
            }

            return true;
        }

        CrossFadeBaseLayerState(idleStateHash);
        return true;
    }

    private bool TryPlayState(bool hasState, int stateHash)
    {
        if (hasState == false)
        {
            return false;
        }

        CrossFadeBaseLayerState(stateHash);
        return true;
    }

    private bool TryPlayOneShotState(
        bool hasState,
        int stateHash,
        float clipLength,
        EmoteType emoteType)
    {
        if (TryPlayState(hasState, stateHash) == false)
        {
            return false;
        }

        if (IsOwner && clipLength > 0.01f)
        {
            m_autoStopEmoteCoroutine = StartCoroutine(
                AutoStopOneShotEmoteAfterDelay(clipLength, emoteType));
        }

        return true;
    }

    private bool TryPlayEmoteStandUp(EmoteType emoteType)
    {
        bool hasStandUpState;
        int standUpStateHash;
        float standUpClipLength;

        switch (emoteType)
        {
            case EmoteType.LieDown:
                hasStandUpState = m_hasLieStandUpState;
                standUpStateHash = m_lieStandUpStateHash;
                standUpClipLength = m_lieStandUpClipLength;
                break;

            case EmoteType.SitFloor:
                hasStandUpState = m_hasSitFloorStandUpState;
                standUpStateHash = m_sitFloorStandUpStateHash;
                standUpClipLength = m_sitFloorStandUpClipLength;
                break;

            default:
                return false;
        }

        if (hasStandUpState == false)
        {
            return false;
        }

        CrossFadeBaseLayerState(standUpStateHash);
        m_isEmoteExitRequested = true;
        m_isEmoteStandUpInProgress = true;

        float waitDuration = standUpClipLength *
            Mathf.Clamp(m_emoteStandUpUnlockNormalizedTime, 0.1f, 1f);

        if (waitDuration > 0.01f)
        {
            m_returnToLocomotionCoroutine = StartCoroutine(
                ReturnToLocomotionAfterDelay(waitDuration));
        }
        else
        {
            m_isEmoteExitRequested = false;
            m_isEmoteStandUpInProgress = false;
            ReleaseCharacterControllerLockFromEmote();
            PlayLocomotionState();
        }

        return true;
    }

    private IEnumerator SwitchToEmoteIdleAfterDelay(float delay, int idleStateHash, EmoteType expectedEmoteType)
    {
        yield return new WaitForSeconds(Mathf.Max(0f, delay));
        m_switchToEmoteIdleCoroutine = null;

        if (m_isEmoting == false ||
            m_isEmoteExitRequested ||
            m_activeEmote != expectedEmoteType ||
            m_animator == null)
        {
            yield break;
        }

        CrossFadeBaseLayerState(idleStateHash);
    }

    private IEnumerator ReturnToLocomotionAfterDelay(float delay)
    {
        yield return new WaitForSeconds(Mathf.Max(0f, delay));
        m_returnToLocomotionCoroutine = null;

        if (m_isEmoting)
        {
            yield break;
        }

        m_isEmoteExitRequested = false;
        m_isEmoteStandUpInProgress = false;
        ReleaseCharacterControllerLockFromEmote();

        if (m_animator == null)
        {
            yield break;
        }

        PlayLocomotionState();
    }

    private void PlayLocomotionState()
    {
        CacheEmoteAnimationSetup();
        if (m_hasLocomotionState == false)
        {
            return;
        }

        CrossFadeBaseLayerState(m_locomotionStateHash);
    }

    private void CrossFadeBaseLayerState(int stateHash)
    {
        if (m_animator == null)
        {
            return;
        }

        m_animator.CrossFadeInFixedTime(
            stateHash,
            m_emoteAnimationCrossFadeDuration,
            0,
            0f);
    }

    private void CacheEmoteAnimationSetup()
    {
        if (m_animator == null)
        {
            return;
        }

        m_hasLocomotionState = TryResolveAnimatorState(
            0,
            m_locomotionStateName,
            m_locomotionFallbackStateName,
            out m_locomotionStateHash,
            out _);

        m_hasCheeringState = TryResolveAnimatorState(
            0,
            m_cheeringStateName,
            null,
            out m_cheeringStateHash,
            out string cheeringResolvedName);

        m_hasWavingState = TryResolveAnimatorState(
            0,
            m_wavingStateName,
            null,
            out m_wavingStateHash,
            out string wavingResolvedName);

        m_hasLieDownState = TryResolveAnimatorState(
            0,
            m_lieDownStateName,
            null,
            out m_lieDownStateHash,
            out string lieDownResolvedName);

        m_hasLieIdleState = TryResolveAnimatorState(
            0,
            m_lieIdleStateName,
            null,
            out m_lieIdleStateHash,
            out _);

        m_hasLieStandUpState = TryResolveAnimatorState(
            0,
            m_lieStandUpStateName,
            null,
            out m_lieStandUpStateHash,
            out string lieStandUpResolvedName);

        m_hasSitFloorDownState = TryResolveAnimatorState(
            0,
            m_sitFloorDownStateName,
            m_sitFloorDownFallbackStateName,
            out m_sitFloorDownStateHash,
            out string sitFloorDownResolvedName);

        m_hasSitFloorIdleState = TryResolveAnimatorState(
            0,
            m_sitFloorIdleStateName,
            m_sitFloorIdleFallbackStateName,
            out m_sitFloorIdleStateHash,
            out _);

        m_hasSitFloorStandUpState = TryResolveAnimatorState(
            0,
            m_sitFloorStandUpStateName,
            m_sitFloorStandUpFallbackStateName,
            out m_sitFloorStandUpStateHash,
            out string sitFloorStandUpResolvedName);

        m_hasSitUpsState = TryResolveAnimatorState(
            0,
            m_sitUpsStateName,
            null,
            out m_sitUpsStateHash,
            out _);

        m_hasPushUpsState = TryResolveAnimatorState(
            0,
            m_pushUpsStateName,
            null,
            out m_pushUpsStateHash,
            out _);

        m_lieDownClipLength = GetAnimationClipLength(lieDownResolvedName);
        m_lieStandUpClipLength = GetAnimationClipLength(lieStandUpResolvedName);
        m_sitFloorDownClipLength = GetAnimationClipLength(sitFloorDownResolvedName);
        m_sitFloorStandUpClipLength = GetAnimationClipLength(sitFloorStandUpResolvedName);
        m_cheeringClipLength = GetAnimationClipLength(cheeringResolvedName);
        m_wavingClipLength = GetAnimationClipLength(wavingResolvedName);
    }

    private void StopEmoteRoutines()
    {
        if (m_switchToEmoteIdleCoroutine != null)
        {
            StopCoroutine(m_switchToEmoteIdleCoroutine);
            m_switchToEmoteIdleCoroutine = null;
        }

        if (m_returnToLocomotionCoroutine != null)
        {
            StopCoroutine(m_returnToLocomotionCoroutine);
            m_returnToLocomotionCoroutine = null;
        }

        if (m_autoStopEmoteCoroutine != null)
        {
            StopCoroutine(m_autoStopEmoteCoroutine);
            m_autoStopEmoteCoroutine = null;
        }
    }

    private bool ShouldLockCharacterControllerForEmote(EmoteType emoteType)
    {
        return emoteType is EmoteType.LieDown or EmoteType.SitFloor;
    }

    private void TryLockCharacterControllerForEmote()
    {
        if (m_disableCharacterControllerWhileLieSitEmote == false)
        {
            return;
        }

        if (m_characterController == null)
        {
            m_characterController = GetComponent<CharacterController>();
        }

        if (m_characterController == null || m_isCharacterControllerLockedByEmote)
        {
            return;
        }

        m_characterControllerWasEnabledBeforeEmote = m_characterController.enabled;
        if (m_characterControllerWasEnabledBeforeEmote)
        {
            m_characterController.enabled = false;
        }

        m_isCharacterControllerLockedByEmote = true;
    }

    private void ReleaseCharacterControllerLockFromEmote()
    {
        if (m_isCharacterControllerLockedByEmote == false)
        {
            return;
        }

        if (m_characterController == null)
        {
            m_characterController = GetComponent<CharacterController>();
        }

        if (m_characterController != null && m_characterControllerWasEnabledBeforeEmote)
        {
            m_characterController.enabled = true;
        }

        m_characterControllerWasEnabledBeforeEmote = false;
        m_isCharacterControllerLockedByEmote = false;
    }

    private IEnumerator AutoStopOneShotEmoteAfterDelay(float delay, EmoteType expectedEmoteType)
    {
        yield return new WaitForSeconds(Mathf.Max(0f, delay));
        m_autoStopEmoteCoroutine = null;

        if (IsOwner == false ||
            m_isEmoting == false ||
            m_activeEmote != expectedEmoteType ||
            m_isEmoteExitRequested ||
            m_isEmoteStandUpInProgress)
        {
            yield break;
        }

        m_isEmoteExitRequested = true;
        RequestStopEmoteServerRpc();
    }
}
