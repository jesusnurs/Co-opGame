using Unity.Netcode;
using UnityEngine;

public class ResourceSpawner : NetworkBehaviour
{
    [SerializeField] private NetworkObject m_woodPrefab, m_stonePrefab;

    public bool SpawnResource(ObjectType type, Vector3 position)
    {
        if (IsServer == false)
        {
            return false;
        }

        NetworkObject resource = GetPrefabByType(type);
        if (resource == null)
        {
            return false;
        }

        Vector3 spawnPosition = position;
        spawnPosition.y += 0.1f;

        GameObject instance = Instantiate(resource.gameObject, spawnPosition,
            Quaternion.Euler(0, UnityEngine.Random.Range(0, 360), 0));
        instance.GetComponent<NetworkObject>().Spawn();
        return true;
    }

    public bool SpawnThrownResource(
        ObjectType type,
        Vector3 startPosition,
        Vector3 initialVelocity,
        float gravity,
        Transform ignoredRoot)
    {
        if (IsServer == false)
        {
            return false;
        }

        NetworkObject resource = GetPrefabByType(type);
        if (resource == null)
        {
            return false;
        }

        GameObject instance = Instantiate(
            resource.gameObject,
            startPosition,
            Quaternion.Euler(0, UnityEngine.Random.Range(0, 360), 0));

        NetworkObject networkObject = instance.GetComponent<NetworkObject>();
        networkObject.Spawn();

        if (instance.TryGetComponent(out PickableBase pickable))
        {
            pickable.ThrowAlongArc(startPosition, initialVelocity, gravity, ignoredRoot);
            return true;
        }

        return false;
    }

    private NetworkObject GetPrefabByType(ObjectType type)
    {
        return type switch
        {
            ObjectType.Wood => m_woodPrefab,
            ObjectType.Stone => m_stonePrefab,
            _ => null
        };
    }
}
