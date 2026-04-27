using UnityEngine;

public class InteractionDetector : MonoBehaviour
{
    [SerializeField] private float m_detectionRadius = 3f;
    [SerializeField] private float m_detectionAngle = 60f;
    [SerializeField] private LayerMask m_pickupLayer;
    [SerializeField] private LayerMask m_lineOfSightMask = Physics.DefaultRaycastLayers;
    [SerializeField] private Vector2 m_cursorViewportPoint = new(0.5f, 0.5f);

    private IInteractable m_closestInteractable;
    public IInteractable ClosestInteractable => m_closestInteractable;

    private bool m_isOwner = false;
    private Camera m_ownerCamera;
    private bool m_detectionEnabled = true;

    private const int MaxOverlapHits = 64;
    private const int MaxVisibilityHits = 32;

    private readonly Collider[] m_overlapHits = new Collider[MaxOverlapHits];
    private readonly RaycastHit[] m_visibilityHits = new RaycastHit[MaxVisibilityHits];

    public void Initialize(bool isOwner)
    {
        m_isOwner = isOwner;
    }

    public void SetDetectionEnabled(bool isEnabled)
    {
        if (m_detectionEnabled == isEnabled)
        {
            return;
        }

        m_detectionEnabled = isEnabled;
        if (m_detectionEnabled == false)
        {
            SetCurrentInteractable(null);
        }
    }

    private void Update()
    {
        if (m_isOwner == false || m_detectionEnabled == false)
        {
            return;
        }

        if (GameManager.Instance != null && GameManager.Instance.IsGameplayInputBlocked)
        {
            SetCurrentInteractable(null);
            return;
        }

        DetectInteractables();
    }

    private void OnDisable()
    {
        SetCurrentInteractable(null);
    }

    private void DetectInteractables()
    {
        if (TryGetOwnerCamera(out Camera ownerCamera) == false)
        {
            SetCurrentInteractable(null);
            return;
        }

        Ray cursorRay = ownerCamera.ViewportPointToRay(m_cursorViewportPoint);
        int hitsCount = Physics.OverlapSphereNonAlloc(
            transform.position,
            m_detectionRadius,
            m_overlapHits,
            m_pickupLayer,
            QueryTriggerInteraction.Collide);

        float smallestCursorOffsetSqr = float.MaxValue;
        float closestDistance = float.MaxValue;
        IInteractable candidate = null;

        for (int i = 0; i < hitsCount; i++)
        {
            Collider hit = m_overlapHits[i];
            m_overlapHits[i] = null;
            if (hit == null || hit.transform.root == transform)
            {
                continue;
            }

            IInteractable pickable = hit.GetComponent<IInteractable>();
            if (pickable == null)
            {
                pickable = hit.GetComponentInParent<IInteractable>();
            }

            if (pickable == null)
                continue;
            if (pickable is PickableBase pickableBase && pickableBase.CanBePickedUp == false)
            {
                continue;
            }

            Vector3 targetPoint = GetInteractionPoint(hit, cursorRay);
            Vector3 directionToPickable = targetPoint - cursorRay.origin;
            float distanceFromCamera = directionToPickable.magnitude;
            if (distanceFromCamera <= 0.001f)
            {
                continue;
            }

            directionToPickable /= distanceFromCamera;
            float angleToPickable = Vector3.Angle(cursorRay.direction, directionToPickable);

            if (angleToPickable > m_detectionAngle * 0.5f)
                continue;

            if (HasLineOfSight(cursorRay.origin, targetPoint, pickable) == false)
            {
                continue;
            }

            Vector3 viewportPoint = ownerCamera.WorldToViewportPoint(targetPoint);
            if (viewportPoint.z <= 0f)
            {
                continue;
            }

            Vector2 cursorOffset = new(
                viewportPoint.x - m_cursorViewportPoint.x,
                viewportPoint.y - m_cursorViewportPoint.y);

            float cursorOffsetSqr = cursorOffset.sqrMagnitude;
            float distance = Vector3.Distance(transform.position, targetPoint);

            bool isBetterCursorCandidate = cursorOffsetSqr < smallestCursorOffsetSqr - 0.0001f;
            bool isSameCursorDistance = Mathf.Abs(cursorOffsetSqr - smallestCursorOffsetSqr) <= 0.0001f;
            bool isCloserByDistance = distance < closestDistance;

            if (isBetterCursorCandidate || (isSameCursorDistance && isCloserByDistance))
            {
                smallestCursorOffsetSqr = cursorOffsetSqr;
                closestDistance = distance;
                candidate = pickable;
            }
        }

        SetCurrentInteractable(candidate);
    }

    private bool TryGetOwnerCamera(out Camera ownerCamera)
    {
        if (m_ownerCamera == null)
        {
            m_ownerCamera = Camera.main;
        }

        ownerCamera = m_ownerCamera;
        return ownerCamera != null;
    }

    private Vector3 GetInteractionPoint(Collider hitCollider, Ray cursorRay)
    {
        Vector3 colliderCenter = hitCollider.bounds.center;
        float projectionDistance = Vector3.Dot(colliderCenter - cursorRay.origin, cursorRay.direction);
        projectionDistance = Mathf.Max(0f, projectionDistance);
        Vector3 projectedPointOnRay = cursorRay.origin + cursorRay.direction * projectionDistance;
        return hitCollider.ClosestPoint(projectedPointOnRay);
    }

    private bool HasLineOfSight(Vector3 origin, Vector3 targetPoint, IInteractable candidate)
    {
        Vector3 direction = targetPoint - origin;
        float distance = direction.magnitude;
        if (distance <= 0.001f)
        {
            return true;
        }

        direction /= distance;

        int visibilityHits = Physics.RaycastNonAlloc(
            origin,
            direction,
            m_visibilityHits,
            distance,
            m_lineOfSightMask,
            QueryTriggerInteraction.Collide);

        float closestVisibleHitDistance = float.MaxValue;
        int closestHitIndex = -1;

        for (int i = 0; i < visibilityHits; i++)
        {
            RaycastHit hit = m_visibilityHits[i];
            if (hit.collider == null || hit.collider.transform.root == transform)
            {
                continue;
            }

            if (hit.distance < closestVisibleHitDistance)
            {
                closestVisibleHitDistance = hit.distance;
                closestHitIndex = i;
            }
        }

        if (closestHitIndex < 0)
        {
            return true;
        }

        IInteractable hitInteractable = m_visibilityHits[closestHitIndex].collider.GetComponent<IInteractable>();
        if (hitInteractable == null)
        {
            hitInteractable = m_visibilityHits[closestHitIndex].collider.GetComponentInParent<IInteractable>();
        }

        return hitInteractable == candidate;
    }

    private void SetCurrentInteractable(IInteractable candidate)
    {
        if (candidate == m_closestInteractable)
        {
            return;
        }

        if (m_closestInteractable != null)
        {
            m_closestInteractable.ToggleSelection(false);
        }

        m_closestInteractable = candidate;

        if (m_closestInteractable != null)
        {
            m_closestInteractable.ToggleSelection(true);
        }
    }
}
