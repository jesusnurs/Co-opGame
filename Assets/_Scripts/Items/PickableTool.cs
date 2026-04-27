using Unity.Netcode.Components;
using UnityEngine;

public class PickableTool : PickableBase
{
    [SerializeField]
    private ComponentController m_componentContrller;

    protected override void ApplyAvailabilityState(bool newValue)
    {
        if(IsServer)
            m_componentContrller.SetEnabled(newValue);
    }

    protected override void OnPickedUp()
    {
        //no code
    }

    public void Drop(Vector3 position)
    {
        if(IsServer == false)
        {
            return;
        }
        Vector3 dropPosition = position;
        dropPosition.y += 1f;
        SetWorldPositionImmediate(dropPosition);
        ResetRigidbodyMotion();
        m_isInThrowFlight.Value = false;
        m_isAvailable.Value = true;
    }
}
