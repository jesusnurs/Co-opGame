using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

public abstract class PickableBase : NetworkBehaviour, IInteractable
{
    protected NetworkVariable<bool> m_isAvailable = new(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    protected NetworkVariable<bool> m_isInThrowFlight = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    [SerializeField] private SelectionOutline m_outline;
    [SerializeField] private ObjectType m_objectType;
    [Header("Physics")]
    [SerializeField] private Rigidbody m_rigidbody;
    [SerializeField] private NetworkTransform m_networkTransform;
    [SerializeField] private Collider m_interactionTriggerCollider;
    [SerializeField] private Collider m_physicsCollider;
    [SerializeField] private float m_throwMaxDuration = 4f;
    [SerializeField] private float m_throwMinFlightDuration = 0.15f;
    [SerializeField] private float m_throwRestVelocity = 0.15f;
    [SerializeField] private float m_throwRestAngularVelocity = 2f;
    [SerializeField] private float m_ignoreThrowerCollisionDuration = 0.2f;

    private readonly List<ColliderPair> m_ignoredCollisionPairs = new();
    private Coroutine m_throwRoutine;
    private Coroutine m_restoreIgnoredCollisionRoutine;
    private float m_throwGravity;

    private struct ColliderPair
    {
        public Collider SelfCollider;
        public Collider OtherCollider;
    }

    public bool CanBePickedUp => m_isAvailable.Value && m_isInThrowFlight.Value == false;
    public ObjectType ObjectType => m_objectType;

    private void Awake()
    {
        CachePhysicsReferences();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_isAvailable.OnValueChanged += OnAvailabilityChanged;
        m_isInThrowFlight.OnValueChanged += OnThrowFlightChanged;
        ApplyAvailabilityState(m_isAvailable.Value);
        ApplyPhysicsState();
    }

    override public void OnNetworkDespawn()
    {
        StopThrowRoutine(clearFlightState: false);
        ClearIgnoredOwnerCollisions();
        m_isAvailable.OnValueChanged -= OnAvailabilityChanged;
        m_isInThrowFlight.OnValueChanged -= OnThrowFlightChanged;
        base.OnNetworkDespawn();
    }

    private void OnAvailabilityChanged(bool previousValue, bool newValue)
    {
        ApplyAvailabilityState(newValue);
        ApplyPhysicsState();
    }

    private void OnThrowFlightChanged(bool previousValue, bool newValue)
    {
        ApplyPhysicsState();
    }

    protected abstract void ApplyAvailabilityState(bool newValue);

    public void PickUp()
    {
        if (IsServer  == false)
        {
            return;
        }

        StopThrowRoutine(clearFlightState: true);
        ClearIgnoredOwnerCollisions();
        ResetRigidbodyMotion();
        m_isInThrowFlight.Value = false;
        m_isAvailable.Value = false;
        OnPickedUp();
    }

    protected abstract void OnPickedUp();

    public void ThrowAlongArc(
        Vector3 startPosition,
        Vector3 initialVelocity,
        float gravity,
        Transform ignoredRoot = null)
    {
        if (IsServer == false)
        {
            return;
        }

        CachePhysicsReferences();
        StopThrowRoutine(clearFlightState: true);

        m_throwGravity = Mathf.Max(0.1f, gravity);
        SetWorldPositionImmediate(startPosition);
        m_isAvailable.Value = true;
        m_isInThrowFlight.Value = true;
        ApplyPhysicsState();

        if (m_rigidbody != null)
        {
            m_rigidbody.position = startPosition;
            m_rigidbody.rotation = transform.rotation;
            m_rigidbody.linearVelocity = initialVelocity;
            m_rigidbody.angularVelocity = Vector3.zero;
            m_rigidbody.WakeUp();
        }

        IgnoreThrowerCollisionsTemporarily(ignoredRoot);
        m_throwRoutine = StartCoroutine(ThrowRoutine());
    }

    private IEnumerator ThrowRoutine()
    {
        float elapsed = 0f;
        float minFlightTime = Mathf.Max(0f, m_throwMinFlightDuration);
        float targetGravity = Mathf.Max(0.1f, m_throwGravity);
        float defaultGravity = Mathf.Abs(Physics.gravity.y);
        float gravityDelta = targetGravity - defaultGravity;

        WaitForFixedUpdate waitForFixedUpdate = new();
        while (elapsed < m_throwMaxDuration)
        {
            yield return waitForFixedUpdate;

            if (m_rigidbody != null && Mathf.Abs(gravityDelta) > 0.001f)
            {
                m_rigidbody.AddForce(Vector3.down * gravityDelta, ForceMode.Acceleration);
            }

            elapsed += Time.fixedDeltaTime;
            if (elapsed < minFlightTime)
            {
                continue;
            }

            if (IsRigidbodySettled())
            {
                break;
            }
        }

        m_isInThrowFlight.Value = false;
        m_throwRoutine = null;
        ApplyPhysicsState();
    }

    private bool IsRigidbodySettled()
    {
        if (m_rigidbody == null)
        {
            return true;
        }

        if (m_rigidbody.IsSleeping())
        {
            return true;
        }

        return m_rigidbody.linearVelocity.sqrMagnitude <= m_throwRestVelocity * m_throwRestVelocity &&
               m_rigidbody.angularVelocity.sqrMagnitude <= m_throwRestAngularVelocity * m_throwRestAngularVelocity;
    }

    private void ApplyPhysicsState()
    {
        CachePhysicsReferences();

        bool isAvailable = m_isAvailable.Value;
        bool isInFlight = m_isInThrowFlight.Value;

        if (m_interactionTriggerCollider != null)
        {
            m_interactionTriggerCollider.enabled = isAvailable;
        }

        if (m_physicsCollider != null)
        {
            m_physicsCollider.enabled = isAvailable;
        }

        if (m_rigidbody == null)
        {
            return;
        }

        bool shouldSimulatePhysics = isAvailable && IsServer;
        m_rigidbody.isKinematic = !shouldSimulatePhysics;
        m_rigidbody.useGravity = shouldSimulatePhysics;
        m_rigidbody.detectCollisions = isAvailable;
        m_rigidbody.interpolation = shouldSimulatePhysics
            ? RigidbodyInterpolation.Interpolate
            : RigidbodyInterpolation.None;
        m_rigidbody.collisionDetectionMode = isInFlight
            ? CollisionDetectionMode.ContinuousDynamic
            : CollisionDetectionMode.ContinuousSpeculative;

        if (shouldSimulatePhysics == false)
        {
            ResetRigidbodyMotion();
        }
    }

    private void StopThrowRoutine(bool clearFlightState)
    {
        if (m_throwRoutine != null)
        {
            StopCoroutine(m_throwRoutine);
            m_throwRoutine = null;
        }

        if (clearFlightState && IsServer)
        {
            m_isInThrowFlight.Value = false;
        }
    }

    private void IgnoreThrowerCollisionsTemporarily(Transform ignoredRoot)
    {
        ClearIgnoredOwnerCollisions();

        if (ignoredRoot == null)
        {
            return;
        }

        Collider[] ownColliders = GetComponents<Collider>();
        Collider[] ownerColliders = ignoredRoot.GetComponentsInChildren<Collider>(true);
        if (ownColliders.Length == 0 || ownerColliders.Length == 0)
        {
            return;
        }

        for (int i = 0; i < ownColliders.Length; i++)
        {
            Collider ownCollider = ownColliders[i];
            if (ownCollider == null)
            {
                continue;
            }

            for (int j = 0; j < ownerColliders.Length; j++)
            {
                Collider ownerCollider = ownerColliders[j];
                if (ownerCollider == null)
                {
                    continue;
                }

                if (ownCollider == ownerCollider)
                {
                    continue;
                }

                Physics.IgnoreCollision(ownCollider, ownerCollider, true);
                m_ignoredCollisionPairs.Add(new ColliderPair
                {
                    SelfCollider = ownCollider,
                    OtherCollider = ownerCollider
                });
            }
        }

        if (m_ignoredCollisionPairs.Count == 0)
        {
            return;
        }

        m_restoreIgnoredCollisionRoutine = StartCoroutine(
            RestoreIgnoredCollisionsAfterDelay(m_ignoreThrowerCollisionDuration));
    }

    private IEnumerator RestoreIgnoredCollisionsAfterDelay(float duration)
    {
        yield return new WaitForSeconds(Mathf.Max(0f, duration));
        ClearIgnoredOwnerCollisions();
    }

    private void ClearIgnoredOwnerCollisions()
    {
        if (m_restoreIgnoredCollisionRoutine != null)
        {
            StopCoroutine(m_restoreIgnoredCollisionRoutine);
            m_restoreIgnoredCollisionRoutine = null;
        }

        if (m_ignoredCollisionPairs.Count == 0)
        {
            return;
        }

        for (int i = 0; i < m_ignoredCollisionPairs.Count; i++)
        {
            ColliderPair pair = m_ignoredCollisionPairs[i];
            if (pair.SelfCollider == null || pair.OtherCollider == null)
            {
                continue;
            }

            Physics.IgnoreCollision(pair.SelfCollider, pair.OtherCollider, false);
        }

        m_ignoredCollisionPairs.Clear();
    }

    private void CachePhysicsReferences()
    {
        if (m_rigidbody == null)
        {
            m_rigidbody = GetComponent<Rigidbody>();
        }

        if (m_networkTransform == null)
        {
            m_networkTransform = GetComponent<NetworkTransform>();
        }

        if (m_interactionTriggerCollider != null && m_physicsCollider != null)
        {
            return;
        }

        Collider[] colliders = GetComponents<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null)
            {
                continue;
            }

            if (collider.isTrigger)
            {
                if (m_interactionTriggerCollider == null)
                {
                    m_interactionTriggerCollider = collider;
                }

                continue;
            }

            if (m_physicsCollider == null)
            {
                m_physicsCollider = collider;
            }
        }
    }

    protected void SetWorldPositionImmediate(Vector3 position)
    {
        transform.position = position;

        if (m_rigidbody != null)
        {
            m_rigidbody.position = position;
        }

        if (m_networkTransform != null && m_networkTransform.CanCommitToTransform)
        {
            m_networkTransform.Teleport(position, transform.rotation, transform.localScale);
        }
    }

    protected void ResetRigidbodyMotion()
    {
        if (m_rigidbody == null)
        {
            return;
        }

        m_rigidbody.linearVelocity = Vector3.zero;
        m_rigidbody.angularVelocity = Vector3.zero;
    }

    public void ToggleSelection(bool isSelected)
    {
        if (m_outline != null)
            m_outline.ToggleOutline(isSelected);
    }
}
