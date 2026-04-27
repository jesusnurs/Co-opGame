using Unity.Netcode;
using UnityEngine;

public partial class PlayerController : NetworkBehaviour
{
    [SerializeField]
    private MyPlayerInput m_playerInput;
    [SerializeField]
    private AgentMover m_agentMover;
    [SerializeField]
    private CameraFollow m_cameraFollow;

    [SerializeField]
    private InteractionDetector m_interactionDetector;
    [SerializeField]
    private Animator m_animator;
    [SerializeField]
    private AnimationEvents m_animationEvents;

    private bool m_isInteracting, m_isChopping;
    [SerializeField]
    private GameObject m_axeModel, m_pickAxeModel, m_woodModel, m_stoneModel;

    private ResourceSpawner m_resourceSpawner;
    private NetworkVariable<ulong> m_heldNetworkObjectId = new(ulong.MaxValue);
    private NetworkVariable<ObjectType> m_heldObjectType = new(ObjectType.None);
    private bool m_ignoreNextInteractAnimationEvent;
    [SerializeField, Min(0f)]
    private float m_interactionRepeatDelay = 0.08f;
    private float m_nextInteractionAllowedTime;
    private bool m_wasMovementBlockedLastFrame;

    private void Awake()
    {
        m_resourceSpawner = FindAnyObjectByType<ResourceSpawner>();

        if (m_cameraFollow == null)
        {
            m_cameraFollow = GetComponent<CameraFollow>();
        }

        if (m_cameraFollow != null)
        {
            m_cameraFollow.SetFirstPersonVisibleObjects(
                m_axeModel,
                m_pickAxeModel,
                m_woodModel,
                m_stoneModel);
        }

        InitializeThrowController();
        InitializeSeatController();
        InitializeEmoteController();
    }

    private void OnEnable()
    {
        m_playerInput.OnPickUpPressed += HandlePickUpPressed;
        m_playerInput.OnInteractPressed += HandleActionPressed;
        m_playerInput.OnThrowStarted += HandleThrowStarted;
        m_playerInput.OnThrowReleased += HandleThrowReleased;
        m_playerInput.OnThrowTapped += HandleThrowTapped;
        m_playerInput.OnEmotePressed += HandleEmotePressed;
    }

    private void HandlePickUpPressed()
    {
        if (IsOwner == false)
            return;
        if (m_isChopping || IsThrowBusy || GameplayMenuState.IsMenuOpen || IsSeatBlockingGameplay() || IsEmoteBlockingGameplay())
            return;
        if (Time.time < m_nextInteractionAllowedTime)
            return;

        IInteractable interactionTarget = m_interactionDetector.ClosestInteractable;
        if (interactionTarget == null)
            return;
        if (TryRequestInteraction(interactionTarget, out bool shouldPlayInteractAnimation) == false)
            return;

        m_nextInteractionAllowedTime = Time.time + m_interactionRepeatDelay;
        if (shouldPlayInteractAnimation)
        {
            PlayPickUpAnimation(ignoreInteractAction: true);
        }
    }

    private void OnDisable()
    {
        m_playerInput.OnPickUpPressed -= HandlePickUpPressed;
        m_playerInput.OnInteractPressed -= HandleActionPressed;
        m_playerInput.OnThrowStarted -= HandleThrowStarted;
        m_playerInput.OnThrowReleased -= HandleThrowReleased;
        m_playerInput.OnThrowTapped -= HandleThrowTapped;
        m_playerInput.OnEmotePressed -= HandleEmotePressed;
        m_ignoreNextInteractAnimationEvent = false;
        m_nextInteractionAllowedTime = 0f;
        m_wasMovementBlockedLastFrame = false;
        ClearPendingThrow();
        CancelThrowCharge(IsOwner);
        HandleSeatOnDisable();
        HandleEmoteOnDisable();
    }

    private void HandleActionPressed()
    {
        if(IsOwner == false)
        {
            return;
        }
        if (m_isChopping || m_isInteracting || IsThrowBusy || IsSeatBlockingGameplay() || IsEmoteBlockingGameplay())
            return;
        if(m_heldObjectType.Value is ObjectType.Axe or ObjectType.PickAxe)
        {
            m_isChopping = true;
            m_animator.SetTrigger("Chop");
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_interactionDetector.Initialize(IsOwner);
        m_heldObjectType.OnValueChanged += HandleHeldItemChanged;
        HandleItemOnJoin();
        HandleSeatOnNetworkSpawn();
        HandleEmoteOnNetworkSpawn();
        if (IsOwner)
        {
            m_animationEvents.OnInteract += HandleInteractAction;
            m_animationEvents.OnAnimationDone += HandleAnimationDone;
            m_animationEvents.OnChop += HandleChopAction;
            m_animationEvents.OnThrowRelease += HandleThrowReleaseAction;
        }
    }

    private void HandleChopAction()
    {
        if(m_heldObjectType.Value is ObjectType.Axe or ObjectType.PickAxe)
        {
            if(m_interactionDetector.ClosestInteractable is ResourceNode)
            {
                RequestResourceNodeInteractionServerRpc(
                    m_interactionDetector.ClosestInteractable.NetworkObject.NetworkObjectId);
            }
        }
    }

    [Rpc(SendTo.Server)]
    private void RequestResourceNodeInteractionServerRpc(ulong networkObjectId)
    {
        if (!NetworkManager.SpawnManager.SpawnedObjects
               .TryGetValue(networkObjectId, out NetworkObject target))
            return;

        if (!target.TryGetComponent(out ResourceNode node))
            return;

        node.Harvest(m_heldObjectType.Value);
    }

    private void HandleItemOnJoin()
    {
        if(m_heldObjectType.Value != ObjectType.None)
        {
            HandleHeldItemChanged(ObjectType.None, m_heldObjectType.Value);
        }
    }

    private void HandleHeldItemChanged(ObjectType previousValue, ObjectType newValue)
    {
        m_axeModel.SetActive(newValue == ObjectType.Axe);
        m_pickAxeModel.SetActive(newValue == ObjectType.PickAxe);
        m_woodModel.SetActive(newValue == ObjectType.Wood);
        m_stoneModel.SetActive(newValue == ObjectType.Stone);

        if (newValue == ObjectType.None)
        {
            ClearPendingThrow();
            CancelThrowCharge(IsOwner);
        }
    }

    private void HandleAnimationDone()
    {
        m_isInteracting = false;
        m_isChopping = false;
        m_ignoreNextInteractAnimationEvent = false;
    }

    private void HandleInteractAction()
    {
        // Interaction is now requested immediately on input press.
        // Animation event remains only for visual timing compatibility.
        m_ignoreNextInteractAnimationEvent = false;
    }

    private void PlayPickUpAnimation(bool ignoreInteractAction)
    {
        m_ignoreNextInteractAnimationEvent = ignoreInteractAction;
        m_animator.SetBool("Interact", true);
        m_isInteracting = true;
    }

    private bool TryRequestInteraction(IInteractable interactionTarget, out bool shouldPlayInteractAnimation)
    {
        shouldPlayInteractAnimation = false;

        if (interactionTarget == null)
        {
            return false;
        }

        ulong networkObjectId = interactionTarget.NetworkObject.NetworkObjectId;
        if (interactionTarget is PickableBase)
        {
            RequestPickUpServerRpc(
                networkObjectId,
                transform.position,
                GetPreferredDropForward());
            shouldPlayInteractAnimation = true;
            return true;
        }

        if (interactionTarget is ResourcePallet)
        {
            RequestGiveItemServerRpc(networkObjectId);
            shouldPlayInteractAnimation = true;
            return true;
        }

        if (interactionTarget is DoorInteractable)
        {
            RequestDoorInteractionServerRpc(networkObjectId, transform.position);
            shouldPlayInteractAnimation = true;
            return true;
        }

        if (interactionTarget is SeatInteractable)
        {
            RequestSeatInteraction(networkObjectId);
            return true;
        }

        return false;
    }

    [Rpc(SendTo.Server)]
    private void RequestGiveItemServerRpc(ulong networkObjectId)
    {
        if (!NetworkManager.SpawnManager.SpawnedObjects
                .TryGetValue(networkObjectId, out NetworkObject target))
            return;

        if (!target.TryGetComponent(out ResourcePallet resourcePallet))
            return;

        if (resourcePallet.Interact(m_heldObjectType.Value))
        {
            m_heldObjectType.Value = ObjectType.None;
            m_heldNetworkObjectId.Value = ulong.MaxValue;
        }
    }

    [Rpc(SendTo.Server)]
    private void RequestPickUpServerRpc(ulong networkObjectId, Vector3 placementOrigin, Vector3 preferredForward)
    {
        if(!NetworkManager.SpawnManager.SpawnedObjects
            .TryGetValue(networkObjectId, out NetworkObject target))
        {
            return;
        }
        if(!target.TryGetComponent(out PickableBase pickableItem))
        {
            return;
        }
        if(!pickableItem.CanBePickedUp)
        {
            return;
        }
        if(m_heldObjectType.Value != ObjectType.None)
        {
            if (DropCurrentItem(placementOrigin, preferredForward) == false)
            {
                return;
            }
        }
        if(pickableItem is PickableTool)
        {
            m_heldNetworkObjectId.Value = networkObjectId;
        }


        m_heldObjectType.Value = pickableItem.ObjectType;
        pickableItem.PickUp();
    }

    public override void OnNetworkDespawn()
    {
        m_heldObjectType.OnValueChanged -= HandleHeldItemChanged;
        HandleSeatOnNetworkDespawn();
        HandleEmoteOnNetworkDespawn();
        if (IsOwner)
        {
            ClearPendingThrow();
            CancelThrowCharge(true);
            RequestDropServerRpc();
            m_animationEvents.OnInteract -= HandleInteractAction;
            m_animationEvents.OnAnimationDone -= HandleAnimationDone;
            m_animationEvents.OnChop -= HandleChopAction;
            m_animationEvents.OnThrowRelease -= HandleThrowReleaseAction;
        }
        base.OnNetworkDespawn();
    }

    [Rpc(SendTo.Server)]
    private void RequestDropServerRpc()
    {
        if (DropCurrentItem() == false)
        {
            // Despawn safety: never leave held world object hidden forever.
            ReleaseHeldItem(transform.position, Vector3.zero, 0f, false);
        }
    }

    [Rpc(SendTo.Server)]
    private void RequestDoorInteractionServerRpc(ulong networkObjectId, Vector3 interactorPosition)
    {
        if (!NetworkManager.SpawnManager.SpawnedObjects
                .TryGetValue(networkObjectId, out NetworkObject target))
            return;

        if (!target.TryGetComponent(out DoorInteractable doorInteractable))
            return;

        doorInteractable.ToggleDoorServer(interactorPosition);
    }

    private void Update()
    {
        UpdateThrowAnimationPlayback();
        UpdateSeatedPose();

        if(IsOwner == false)
        {
            return;
        }

        UpdateThrowCharge();

        Vector2 movementInput = m_playerInput.MovementInput;
        bool isMovementBlocked = HandleSeatMovementInput(ref movementInput);
        isMovementBlocked |= HandleEmoteMovementInput(ref movementInput);

        if (isMovementBlocked)
        {
            movementInput = Vector2.zero;
            m_wasMovementBlockedLastFrame = true;
        }
        else if (m_wasMovementBlockedLastFrame)
        {
            m_wasMovementBlockedLastFrame = false;
            movementInput = m_playerInput.ReadImmediateMovementInput();
        }

        if(m_isChopping || GameplayMenuState.IsMenuOpen)
        {
            movementInput = Vector2.zero;
        }

        Vector3 planarDirection = GetMovementDirection(movementInput);

        bool forceFacingYaw = m_cameraFollow != null;
        float facingYaw = forceFacingYaw ? m_cameraFollow.CurrentYaw : transform.eulerAngles.y;

        m_agentMover.Move(planarDirection, forceFacingYaw, facingYaw);
    }

    private Vector3 GetMovementDirection(Vector2 movementInput)
    {
        if (movementInput.sqrMagnitude <= 0.0001f)
        {
            return Vector3.zero;
        }

        if (m_cameraFollow != null)
        {
            Vector3 forward = m_cameraFollow.PlanarForward;
            Vector3 right = m_cameraFollow.PlanarRight;
            return forward * movementInput.y + right * movementInput.x;
        }

        return transform.forward * movementInput.y + transform.right * movementInput.x;
    }
}
