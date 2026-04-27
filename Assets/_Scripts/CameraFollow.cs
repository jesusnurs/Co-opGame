using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

public class CameraFollow : NetworkBehaviour
{
    [Header("Look")]
    [SerializeField]
    private float m_mouseSensitivity = 0.12f;
    [SerializeField]
    private float m_gamepadSensitivity = 170f;
    [SerializeField]
    private float m_minPitch = -55f;
    [SerializeField]
    private float m_maxPitch = 75f;

    [Header("Third Person")]
    [SerializeField]
    private float m_thirdPersonDistance = 3f;
    [SerializeField]
    private float m_thirdPersonPivotHeight = 1.7f;
    [SerializeField]
    private float m_thirdPersonShoulderOffset = 0.35f;
    [SerializeField]
    private float m_cameraSmoothTime = 0.05f;

    [Header("First Person")]
    [SerializeField]
    private float m_firstPersonHeight = 1.65f;
    [SerializeField]
    private float m_firstPersonFov = 88f;
    [SerializeField]
    private float m_fovLerpSpeed = 12f;

    [Header("Camera Collision")]
    [SerializeField]
    private float m_cameraCollisionRadius = 0.2f;
    [SerializeField]
    private float m_cameraCollisionPadding = 0.1f;
    [SerializeField]
    private LayerMask m_cameraCollisionMask = Physics.DefaultRaycastLayers;

    private const Key TogglePerspectiveKey = Key.F;

    private Camera m_camera;
    private Vector3 m_originPosition;
    private Quaternion m_originRotation;
    private Vector3 m_cameraVelocity;
    private float m_yaw;
    private float m_pitch;
    private bool m_isFirstPersonView;
    private bool m_isMenuOpen;
    private bool m_allowFirstPersonBodyRotation = true;
    private float m_defaultFov;
    private CursorLockMode m_originCursorLockMode;
    private bool m_originCursorVisible;

    private RendererState[] m_rendererStates;
    private readonly HashSet<Renderer> m_firstPersonVisibleRenderers = new();

    public bool IsFirstPersonView => m_isFirstPersonView;
    public float CurrentYaw => m_yaw;

    public Vector3 PlanarForward => Quaternion.Euler(0f, m_yaw, 0f) * Vector3.forward;
    public Vector3 PlanarRight => Quaternion.Euler(0f, m_yaw, 0f) * Vector3.right;

    private struct RendererState
    {
        public Renderer Renderer;
        public ShadowCastingMode ShadowCastingMode;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsOwner == false)
        {
            return;
        }

        m_camera = Camera.main;
        if (m_camera == null)
        {
            Debug.LogError("Camera.main was not found for owner camera follow.");
            return;
        }

        m_originPosition = m_camera.transform.position;
        m_originRotation = m_camera.transform.rotation;
        m_defaultFov = m_camera.fieldOfView;
        m_originCursorLockMode = Cursor.lockState;
        m_originCursorVisible = Cursor.visible;

        Vector3 euler = m_camera.transform.rotation.eulerAngles;
        m_yaw = euler.y;
        m_pitch = NormalizePitch(euler.x);
        m_pitch = Mathf.Clamp(m_pitch, m_minPitch, m_maxPitch);

        CacheRendererStates();
        GameplayMenuState.OnMenuStateChanged += HandleMenuStateChanged;
        HandleMenuStateChanged(GameplayMenuState.IsMenuOpen);
        ApplyCameraImmediately();
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner && m_camera != null)
        {
            GameplayMenuState.OnMenuStateChanged -= HandleMenuStateChanged;
            RestoreRendererStates();
            m_camera.transform.SetPositionAndRotation(m_originPosition, m_originRotation);
            m_camera.fieldOfView = m_defaultFov;
            SetCursorCapture(false);
        }

        base.OnNetworkDespawn();
    }

    private void LateUpdate()
    {
        if (IsOwner == false || m_camera == null)
        {
            return;
        }

        if (m_isMenuOpen == false &&
            Keyboard.current != null &&
            Keyboard.current[TogglePerspectiveKey].wasPressedThisFrame)
        {
            TogglePerspective();
        }

        if (m_isMenuOpen == false)
        {
            Vector2 lookInput = GetLookInput();
            m_yaw += lookInput.x;
            m_pitch = Mathf.Clamp(m_pitch - lookInput.y, m_minPitch, m_maxPitch);
        }

        if (m_isFirstPersonView && m_allowFirstPersonBodyRotation)
        {
            transform.rotation = Quaternion.Euler(0f, m_yaw, 0f);
        }

        Quaternion lookRotation = Quaternion.Euler(m_pitch, m_yaw, 0f);
        Vector3 targetPosition = m_isFirstPersonView
            ? GetFirstPersonPosition()
            : GetThirdPersonPosition(lookRotation);
        float targetFov = m_isFirstPersonView ? m_firstPersonFov : m_defaultFov;

        if (m_isFirstPersonView)
        {
            m_camera.transform.position = targetPosition;
            m_cameraVelocity = Vector3.zero;
        }
        else
        {
            m_camera.transform.position = Vector3.SmoothDamp(
                m_camera.transform.position,
                targetPosition,
                ref m_cameraVelocity,
                m_cameraSmoothTime);
        }

        m_camera.transform.rotation = lookRotation;
        m_camera.fieldOfView = Mathf.Lerp(
            m_camera.fieldOfView,
            targetFov,
            Mathf.Clamp01(Time.deltaTime * m_fovLerpSpeed));
    }

    private void HandleMenuStateChanged(bool isMenuOpen)
    {
        m_isMenuOpen = isMenuOpen;
        SetCursorCapture(isMenuOpen == false);
    }

    private void TogglePerspective()
    {
        m_isFirstPersonView = !m_isFirstPersonView;
        if (m_isFirstPersonView)
        {
            ApplyFirstPersonRendererState();
            return;
        }

        RestoreRendererStates();
    }

    private void ApplyCameraImmediately()
    {
        Quaternion lookRotation = Quaternion.Euler(m_pitch, m_yaw, 0f);
        Vector3 startPosition = GetThirdPersonPosition(lookRotation);
        m_camera.transform.SetPositionAndRotation(startPosition, lookRotation);
        m_camera.fieldOfView = m_defaultFov;
        RestoreRendererStates();
    }

    public void SetFirstPersonVisibleObjects(params GameObject[] objectsToKeepVisible)
    {
        m_firstPersonVisibleRenderers.Clear();
        if (objectsToKeepVisible == null)
        {
            return;
        }

        for (int i = 0; i < objectsToKeepVisible.Length; i++)
        {
            GameObject rootObject = objectsToKeepVisible[i];
            if (rootObject == null)
            {
                continue;
            }

            Renderer[] renderers = rootObject.GetComponentsInChildren<Renderer>(true);
            for (int j = 0; j < renderers.Length; j++)
            {
                m_firstPersonVisibleRenderers.Add(renderers[j]);
            }
        }
    }

    public void SetFirstPersonBodyRotationEnabled(bool isEnabled)
    {
        m_allowFirstPersonBodyRotation = isEnabled;
    }

    private Vector2 GetLookInput()
    {
        Vector2 lookInput = Vector2.zero;
        if (Mouse.current != null)
        {
            lookInput += Mouse.current.delta.ReadValue() * m_mouseSensitivity;
        }

        if (Gamepad.current != null)
        {
            lookInput += Gamepad.current.rightStick.ReadValue() * (m_gamepadSensitivity * Time.deltaTime);
        }

        return lookInput;
    }

    private Vector3 GetFirstPersonPosition()
    {
        return transform.position + Vector3.up * m_firstPersonHeight;
    }

    private Vector3 GetThirdPersonPosition(Quaternion lookRotation)
    {
        Vector3 pivot = transform.position + Vector3.up * m_thirdPersonPivotHeight;
        Vector3 shoulderOffset = lookRotation * (Vector3.right * m_thirdPersonShoulderOffset);
        Vector3 rayOrigin = pivot + shoulderOffset;

        Vector3 desiredPosition = rayOrigin - lookRotation * (Vector3.forward * m_thirdPersonDistance);
        Vector3 direction = desiredPosition - rayOrigin;
        float distance = direction.magnitude;
        if (distance <= 0.001f)
        {
            return desiredPosition;
        }

        direction /= distance;

        RaycastHit[] hits = Physics.SphereCastAll(
            rayOrigin,
            m_cameraCollisionRadius,
            direction,
            distance,
            m_cameraCollisionMask,
            QueryTriggerInteraction.Ignore);

        float minDistance = distance;
        bool foundBlockingHit = false;
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.collider == null || hit.collider.transform.root == transform)
            {
                continue;
            }

            foundBlockingHit = true;
            if (hit.distance < minDistance)
            {
                minDistance = hit.distance;
            }
        }

        if (foundBlockingHit)
        {
            float safeDistance = Mathf.Max(0.1f, minDistance - m_cameraCollisionPadding);
            return rayOrigin + direction * safeDistance;
        }

        return desiredPosition;
    }

    private void CacheRendererStates()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        m_rendererStates = new RendererState[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            m_rendererStates[i] = new RendererState
            {
                Renderer = renderers[i],
                ShadowCastingMode = renderers[i].shadowCastingMode
            };
        }
    }

    private void ApplyFirstPersonRendererState()
    {
        if (m_rendererStates == null)
        {
            return;
        }

        for (int i = 0; i < m_rendererStates.Length; i++)
        {
            Renderer renderer = m_rendererStates[i].Renderer;
            if (renderer != null)
            {
                if (m_firstPersonVisibleRenderers.Contains(renderer))
                {
                    renderer.shadowCastingMode = m_rendererStates[i].ShadowCastingMode;
                    continue;
                }

                renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            }
        }
    }

    private void RestoreRendererStates()
    {
        if (m_rendererStates == null)
        {
            return;
        }

        for (int i = 0; i < m_rendererStates.Length; i++)
        {
            Renderer renderer = m_rendererStates[i].Renderer;
            if (renderer != null)
            {
                renderer.shadowCastingMode = m_rendererStates[i].ShadowCastingMode;
            }
        }
    }

    private void SetCursorCapture(bool capture)
    {
        if (capture)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            return;
        }

        Cursor.lockState = m_originCursorLockMode;
        Cursor.visible = m_originCursorVisible;
    }

    private static float NormalizePitch(float angle)
    {
        if (angle > 180f)
        {
            angle -= 360f;
        }

        return angle;
    }
}
