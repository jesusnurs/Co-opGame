using UnityEngine;

/// <summary>
/// AgentMover doesnt need to ask "IsOwner" question becuase the Input is only provided for the Owner. So while we could
/// make it a NetworkBehaviour and do the IsOwner check, it is not really necessary and we can save some performance by keeping it as a regular MonoBehaviour.
/// </summary>
public class AgentMover : MonoBehaviour
{
    [SerializeField]
    private CharacterController m_characterController;
    [SerializeField]
    private Animator m_animator;

    [SerializeField]
    private float m_walkSpeed = 4f;
    [SerializeField]
    private float m_rotationSpeed = 540f;
    [SerializeField]
    private float m_gravity = -25f;
    [SerializeField]
    private float m_groundedVerticalSpeed = -2f;
    [SerializeField]
    private float m_terminalFallSpeed = 40f;

    private float m_verticalSpeed;

    public void Move(Vector3 worldDirection, bool forceFacingYaw, float facingYaw)
    {
        if (m_characterController == null ||
            m_characterController.enabled == false ||
            m_characterController.gameObject.activeInHierarchy == false)
        {
            if (m_animator != null)
            {
                m_animator.SetFloat("Movement", 0f);
            }

            return;
        }

        if (worldDirection.sqrMagnitude > 1f)
        {
            worldDirection.Normalize();
        }

        if (m_characterController.isGrounded && m_verticalSpeed < 0f)
        {
            m_verticalSpeed = m_groundedVerticalSpeed;
        }

        m_verticalSpeed += m_gravity * Time.deltaTime;
        m_verticalSpeed = Mathf.Max(m_verticalSpeed, -m_terminalFallSpeed);

        Vector3 planarVelocity = worldDirection * m_walkSpeed;
        Vector3 velocity = planarVelocity + Vector3.up * m_verticalSpeed;
        m_characterController.Move(velocity * Time.deltaTime);

        if (m_characterController.isGrounded && m_verticalSpeed < 0f)
        {
            m_verticalSpeed = m_groundedVerticalSpeed;
        }

        if (forceFacingYaw)
        {
            transform.rotation = Quaternion.Euler(0f, facingYaw, 0f);
        }
        else if (worldDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(worldDirection, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                targetRotation,
                m_rotationSpeed * Time.deltaTime);
        }

        float moveAmount = new Vector2(worldDirection.x, worldDirection.z).magnitude;
        m_animator.SetFloat("Movement", moveAmount);
    }
}
