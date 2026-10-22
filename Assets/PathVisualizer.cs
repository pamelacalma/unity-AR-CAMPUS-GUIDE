using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(LineRenderer))]
public class PathVisualizer : MonoBehaviour
{
    [Header("Target References")]
    public Transform userTransform;       // Assign 'Main Camera' here
    public Transform destinationTarget;  // Assign target destination object
    public Transform fallbackEntrance;   // Fallback target if main target is null

    [Header("Line Settings")]
    [SerializeField] private float pathHeightOffset = 0.05f; // Lifts line 5cm above floor to avoid clipping
    [SerializeField] private float sampleRadius = 5.0f;     // NavMesh search radius in meters

    private LineRenderer lineRenderer;
    private NavMeshPath path;

    private void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
        path = new NavMeshPath();
    }

    private void Update()
    {
        DrawPath();
    }

    /// <summary>
    /// Dynamically update the active target (called by DestinationManager).
    /// </summary>
    public void SetDestination(Transform newTarget)
    {
        destinationTarget = newTarget;
    }

    /// <summary>
    /// Projects camera/target positions onto the floor NavMesh and updates LineRenderer vertices.
    /// </summary>
    public void DrawPath()
    {
        Transform activeTarget = destinationTarget != null ? destinationTarget : fallbackEntrance;

        if (userTransform == null || activeTarget == null)
        {
            lineRenderer.positionCount = 0;
            return;
        }

        // 1. Project user camera down to the nearest floor NavMesh point
        if (NavMesh.SamplePosition(userTransform.position, out NavMeshHit startHit, sampleRadius, NavMesh.AllAreas))
        {
            // 2. Project target destination down to the nearest floor NavMesh point
            if (NavMesh.SamplePosition(activeTarget.position, out NavMeshHit targetHit, sampleRadius, NavMesh.AllAreas))
            {
                // 3. Calculate path across the floor mesh
                if (NavMesh.CalculatePath(startHit.position, targetHit.position, NavMesh.AllAreas, path))
                {
                    if (path.status == NavMeshPathStatus.PathComplete || path.status == NavMeshPathStatus.PathPartial)
                    {
                        lineRenderer.positionCount = path.corners.Length;

                        // 4. Apply vertical offset to prevent line floor-flickering (z-fighting)
                        for (int i = 0; i < path.corners.Length; i++)
                        {
                            Vector3 floorPoint = path.corners[i] + (Vector3.up * pathHeightOffset);
                            lineRenderer.SetPosition(i, floorPoint);
                        }
                        return;
                    }
                }
            }
        }

        // If pathfinding fails or user is out of range, clear line
        lineRenderer.positionCount = 0;
    }
}