using System.Collections.Generic;
using UnityEngine;

public class DestinationManager : MonoBehaviour
{
    [Header("Path Visualizer Reference")]
    public PathVisualizer pathVisualizer;

    [Header("Target Objects in Room")]
    public List<Transform> targets = new List<Transform>();

    public void SelectDestination(int targetIndex)
    {
        if (pathVisualizer == null) return;

        if (targetIndex >= 0 && targetIndex < targets.Count)
        {
            pathVisualizer.destinationTarget = targets[targetIndex];
        }
    }

    public void SetDestinationByTransform(Transform newTarget)
    {
        if (pathVisualizer == null) return;
        pathVisualizer.destinationTarget = newTarget;
    }
}