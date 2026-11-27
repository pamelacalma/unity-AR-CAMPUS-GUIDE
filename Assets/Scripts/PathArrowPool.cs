using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Discrete turn and floor-change maneuver categories used by NavigationHUD.
/// </summary>
public enum NavManeuverType
{
    Straight = 0,
    SlightLeft = 1,
    TurnLeft = 2,
    SharpLeft = 3,
    SlightRight = 4,
    TurnRight = 5,
    SharpRight = 6,
    TurnAround = 7,
    FloorUp = 8,
    FloorDown = 9,
    Arrive = 10
}

public struct NavManeuverStep
{
    public NavManeuverType type;
    public float distanceFromUser;
    public float turnAngleDeg;
    public float elevationDeltaY;
    public Vector3 position;
}

public struct NavGuidanceSnapshot
{
    public bool valid;
    public Vector3 aimPoint;
    public float distanceRemaining;
    public float estimatedSeconds;
    public bool hasNextStep;
    public NavManeuverStep nextStep;
    public bool hasFollowingStep;
    public NavManeuverStep followingStep;
}

/// <summary>
/// Polyline sampling and turn-detection utilities for NavMesh paths.
/// </summary>
public class PathPolyline
{
    private readonly List<Vector3> corners = new List<Vector3>();
    private readonly List<float> cumulativeDistances = new List<float>();

    public int Count => corners.Count;
    public float TotalLength => cumulativeDistances.Count > 0 ? cumulativeDistances[cumulativeDistances.Count - 1] : 0f;
    public IReadOnlyList<Vector3> Corners => corners;

    public void UpdateFrom(Vector3[] buffer, int count)
    {
        corners.Clear();
        cumulativeDistances.Clear();
        if (buffer == null || count <= 0) return;

        float total = 0f;
        corners.Add(buffer[0]);
        cumulativeDistances.Add(0f);

        for (int i = 1; i < count; i++)
        {
            Vector3 prev = corners[corners.Count - 1];
            Vector3 cur = buffer[i];
            float dist = Vector3.Distance(prev, cur);
            if (dist < 1e-4f) continue;
            total += dist;
            corners.Add(cur);
            cumulativeDistances.Add(total);
        }
    }

    public void Clear()
    {
        corners.Clear();
        cumulativeDistances.Clear();
    }

    public float ProjectPointOntoPath(Vector3 worldPos)
    {
        if (corners.Count < 2) return 0f;

        Vector2 p = new Vector2(worldPos.x, worldPos.z);
        float bestDistSq = float.MaxValue;
        float bestPathDist = 0f;

        for (int i = 0; i < corners.Count - 1; i++)
        {
            Vector2 a = new Vector2(corners[i].x, corners[i].z);
            Vector2 b = new Vector2(corners[i + 1].x, corners[i + 1].z);
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            float t = lenSq > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq) : 0f;
            Vector2 closest = a + ab * t;
            float dSq = (p - closest).sqrMagnitude;
            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                bestPathDist = Mathf.Lerp(cumulativeDistances[i], cumulativeDistances[i + 1], t);
            }
        }
        return bestPathDist;
    }

    public bool TrySampleAtDistance(float distance, out Vector3 position, out Vector3 forward, float cornerBlendRadius = 0.35f)
    {
        position = Vector3.zero;
        forward = Vector3.forward;
        if (corners.Count < 2) return false;

        float total = TotalLength;
        if (total < 1e-4f) return false;

        distance = Mathf.Clamp(distance, 0f, total);
        int seg = corners.Count - 2;
        for (int i = 0; i < corners.Count - 1; i++)
        {
            if (distance <= cumulativeDistances[i + 1] || i == corners.Count - 2)
            {
                seg = i;
                break;
            }
        }

        float d0 = cumulativeDistances[seg];
        float d1 = cumulativeDistances[seg + 1];
        float segLen = Mathf.Max(1e-4f, d1 - d0);
        float t = Mathf.Clamp01((distance - d0) / segLen);
        position = Vector3.Lerp(corners[seg], corners[seg + 1], t);

        Vector3 dir = FlattenDirection(corners[seg + 1] - corners[seg]);
        if (cornerBlendRadius > 1e-3f)
        {
            float distFromStart = distance - d0;
            float distToEnd = d1 - distance;
            if (seg > 0 && distFromStart < cornerBlendRadius)
            {
                Vector3 prevDir = FlattenDirection(corners[seg] - corners[seg - 1]);
                float blend = Mathf.Clamp01(0.5f + 0.5f * (distFromStart / cornerBlendRadius));
                dir = Vector3.Slerp(prevDir, dir, blend);
            }
            else if (seg + 2 < corners.Count && distToEnd < cornerBlendRadius)
            {
                Vector3 nextDir = FlattenDirection(corners[seg + 2] - corners[seg + 1]);
                float blend = Mathf.Clamp01(0.5f * (distToEnd / cornerBlendRadius));
                dir = Vector3.Slerp(dir, nextDir, 0.5f - blend);
            }
        }

        forward = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
        return true;
    }

    public bool TryFindNextTurn(float fromDistance, float minTurnDeg, out float distanceToTurn, out float turnAngleDeg)
    {
        distanceToTurn = 0f;
        turnAngleDeg = 0f;
        if (corners.Count < 3) return false;

        for (int k = 1; k < corners.Count - 1; k++)
        {
            if (cumulativeDistances[k] <= fromDistance + 0.3f) continue;

            Vector3 inDir = FlattenDirection(corners[k] - corners[k - 1]);
            if (inDir.sqrMagnitude < 1e-4f) continue;

            // Accumulate tightly spaced chamfer corners (e.g. doorways) into one turn.
            int endCorner = k;
            float accumAngle = 0f;
            Vector3 refDir = inDir;
            while (endCorner < corners.Count - 1)
            {
                Vector3 outDir = FlattenDirection(corners[endCorner + 1] - corners[endCorner]);
                if (outDir.sqrMagnitude > 1e-4f)
                {
                    float stepAngle = Vector3.SignedAngle(refDir, outDir, Vector3.up);
                    if (endCorner == k || (Mathf.Sign(stepAngle) == Mathf.Sign(accumAngle) &&
                                           cumulativeDistances[endCorner] - cumulativeDistances[k] <= 1.2f))
                    {
                        accumAngle += stepAngle;
                        refDir = outDir;
                        if (Mathf.Abs(accumAngle) >= minTurnDeg) break;
                        endCorner++;
                        continue;
                    }
                }
                break;
            }

            if (Mathf.Abs(accumAngle) >= minTurnDeg)
            {
                distanceToTurn = Mathf.Max(0f, cumulativeDistances[k] - fromDistance);
                turnAngleDeg = accumAngle;
                return true;
            }
        }
        return false;
    }

    public int CollectUpcomingSteps(
        float fromDistance,
        float minTurnDeg,
        float floorDeltaThresholdMeters,
        List<NavManeuverStep> results,
        int maxSteps = 2)
    {
        if (results == null) return 0;
        results.Clear();
        if (corners.Count < 2 || maxSteps <= 0) return 0;

        for (int k = 1; k < corners.Count && results.Count < maxSteps; k++)
        {
            if (cumulativeDistances[k] <= fromDistance + 0.35f) continue;

            float segDy = corners[k].y - corners[k - 1].y;
            if (Mathf.Abs(segDy) >= floorDeltaThresholdMeters)
            {
                float totalDy = segDy;
                int endK = k;
                while (endK + 1 < corners.Count)
                {
                    float nextDy = corners[endK + 1].y - corners[endK].y;
                    if (Mathf.Sign(nextDy) == Mathf.Sign(segDy) && Mathf.Abs(nextDy) >= 0.4f)
                    {
                        totalDy += nextDy;
                        endK++;
                    }
                    else
                    {
                        break;
                    }
                }

                results.Add(new NavManeuverStep
                {
                    type = totalDy > 0f ? NavManeuverType.FloorUp : NavManeuverType.FloorDown,
                    distanceFromUser = Mathf.Max(0f, cumulativeDistances[k - 1] - fromDistance),
                    turnAngleDeg = 0f,
                    elevationDeltaY = totalDy,
                    position = corners[k - 1]
                });
                k = endK;
                continue;
            }

            if (k >= corners.Count - 1) continue;

            Vector3 flatIn = new Vector3(corners[k].x - corners[k - 1].x, 0f, corners[k].z - corners[k - 1].z);
            if (flatIn.sqrMagnitude < 1e-4f) continue;
            Vector3 inDir = flatIn.normalized;

            int endCorner = k;
            float accumAngle = 0f;
            Vector3 refDir = inDir;
            while (endCorner < corners.Count - 1)
            {
                Vector3 flatOut = new Vector3(
                    corners[endCorner + 1].x - corners[endCorner].x,
                    0f,
                    corners[endCorner + 1].z - corners[endCorner].z);
                if (flatOut.sqrMagnitude > 1e-4f)
                {
                    Vector3 outDir = flatOut.normalized;
                    float stepAngle = Vector3.SignedAngle(refDir, outDir, Vector3.up);
                    if (endCorner == k || (Mathf.Sign(stepAngle) == Mathf.Sign(accumAngle) &&
                                           cumulativeDistances[endCorner] - cumulativeDistances[k] <= 1.2f))
                    {
                        accumAngle += stepAngle;
                        refDir = outDir;
                        if (Mathf.Abs(accumAngle) >= 150f) break;
                        endCorner++;
                        continue;
                    }
                }
                break;
            }

            if (Mathf.Abs(accumAngle) >= minTurnDeg)
            {
                results.Add(new NavManeuverStep
                {
                    type = ClassifyHorizontalTurn(accumAngle),
                    distanceFromUser = Mathf.Max(0f, cumulativeDistances[k] - fromDistance),
                    turnAngleDeg = accumAngle,
                    elevationDeltaY = 0f,
                    position = corners[k]
                });
                k = endCorner;
            }
        }

        return results.Count;
    }

    private static NavManeuverType ClassifyHorizontalTurn(float angleDeg)
    {
        float abs = Mathf.Abs(angleDeg);
        if (abs >= 125f) return angleDeg > 0f ? NavManeuverType.SharpRight : NavManeuverType.SharpLeft;
        if (abs >= 50f) return angleDeg > 0f ? NavManeuverType.TurnRight : NavManeuverType.TurnLeft;
        return angleDeg > 0f ? NavManeuverType.SlightRight : NavManeuverType.SlightLeft;
    }

    private static Vector3 FlattenDirection(Vector3 delta)
    {
        Vector3 flat = new Vector3(delta.x, 0f, delta.z);
        if (flat.sqrMagnitude > 1e-6f) return flat.normalized;
        return delta.sqrMagnitude > 1e-6f ? delta.normalized : Vector3.forward;
    }
}

/// <summary>
/// Pools and animates 3D directional navigation arrows along a PathPolyline.
/// Generates a built-in 3D arrow mesh and URP-safe material if no prefab/material is provided.
/// </summary>
public class PathArrowPool
{
    private static Mesh cachedArrowMesh;

    private readonly List<Transform> instances = new List<Transform>();
    private Transform container;
    private Material runtimeMaterial;
    private GameObject activePrefab;
    private Material activeMaterialOverride;
    private Color activeColor;
    private Scene targetScene;

    public void Configure(Scene scene, GameObject prefab, Material materialOverride, Color color, Shader fallbackShader)
    {
        targetScene = scene;

        bool prefabChanged = activePrefab != prefab;
        bool matChanged = activeMaterialOverride != materialOverride;
        activePrefab = prefab;
        activeMaterialOverride = materialOverride;
        activeColor = color;

        if (prefabChanged)
        {
            ClearPool();
        }

        if (prefab == null && (runtimeMaterial == null || matChanged))
        {
            RebuildMaterial(materialOverride, color, fallbackShader);
        }
        else if (runtimeMaterial != null)
        {
            ApplyMaterialColor(runtimeMaterial, color);
        }
    }

    public void UpdateArrows(
        PathPolyline path,
        float userDistAlongPath,
        float startOffset,
        float endPadding,
        float spacing,
        float scrollSpeed,
        float maxRange,
        int maxCount,
        float heightOffset,
        float baseScale,
        float time)
    {
        if (path == null || path.Count < 2 || path.TotalLength < 0.2f)
        {
            HideAll();
            return;
        }

        float safeSpacing = Mathf.Max(0.35f, spacing);
        float startDist = Mathf.Max(0f, userDistAlongPath + startOffset);
        float endDist = Mathf.Min(path.TotalLength - endPadding, startDist + maxRange);
        if (endDist <= startDist + 0.1f)
        {
            HideAll();
            return;
        }

        EnsureContainer();
        if (!container.gameObject.activeSelf) container.gameObject.SetActive(true);

        float phase = scrollSpeed > 0f ? (time * scrollSpeed) % safeSpacing : 0f;
        int activeIndex = 0;

        for (float d = startDist + phase; d <= endDist && activeIndex < maxCount; d += safeSpacing)
        {
            if (!path.TrySampleAtDistance(d, out Vector3 pos, out Vector3 fwd)) continue;

            Transform arrow = GetOrCreateArrow(activeIndex);
            if (!arrow.gameObject.activeSelf) arrow.gameObject.SetActive(true);

            float fadeIn = Mathf.InverseLerp(startDist, startDist + 0.55f, d);
            float fadeOut = Mathf.InverseLerp(endDist, endDist - 0.65f, d);
            float fade = Mathf.Min(fadeIn, fadeOut);
            float pulse = 1f + 0.12f * Mathf.Sin((d - time * Mathf.Max(0.2f, scrollSpeed) * 2.0f) * 3.2f);

            arrow.SetPositionAndRotation(pos + Vector3.up * heightOffset, Quaternion.LookRotation(fwd, Vector3.up));
            arrow.localScale = Vector3.one * (baseScale * Mathf.Clamp01(fade) * pulse);
            activeIndex++;
        }

        for (int i = activeIndex; i < instances.Count; i++)
        {
            if (instances[i] != null && instances[i].gameObject.activeSelf)
                instances[i].gameObject.SetActive(false);
        }
    }

    public void HideAll()
    {
        if (container != null && container.gameObject.activeSelf)
            container.gameObject.SetActive(false);
    }

    public void Dispose()
    {
        ClearPool();
        if (container != null)
        {
            Object.Destroy(container.gameObject);
            container = null;
        }
        if (runtimeMaterial != null)
        {
            Object.Destroy(runtimeMaterial);
            runtimeMaterial = null;
        }
    }

    private void ClearPool()
    {
        for (int i = 0; i < instances.Count; i++)
        {
            if (instances[i] != null) Object.Destroy(instances[i].gameObject);
        }
        instances.Clear();
    }

    private void EnsureContainer()
    {
        if (container != null) return;
        var rootGo = new GameObject("PathArrows");
        if (targetScene.IsValid() && targetScene.isLoaded)
            SceneManager.MoveGameObjectToScene(rootGo, targetScene);
        container = rootGo.transform;
    }

    private Transform GetOrCreateArrow(int index)
    {
        while (instances.Count <= index)
        {
            GameObject go;
            if (activePrefab != null)
            {
                go = Object.Instantiate(activePrefab, container);
            }
            else
            {
                go = new GameObject($"NavArrow_{instances.Count}");
                go.transform.SetParent(container, false);
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = GetOrCreateArrowMesh();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = runtimeMaterial;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            instances.Add(go.transform);
        }
        return instances[index];
    }

    private void RebuildMaterial(Material materialOverride, Color color, Shader fallbackShader)
    {
        if (runtimeMaterial != null) Object.Destroy(runtimeMaterial);
        runtimeMaterial = CreateUnlitMaterial(materialOverride, color, fallbackShader);
    }

    /// <summary>
    /// Creates an unlit double-sided material supporting URP transparency when alpha &lt; 1.
    /// </summary>
    internal static Material CreateUnlitMaterial(Material materialOverride, Color color, Shader fallbackShader)
    {
        Material mat;
        if (materialOverride != null)
        {
            mat = new Material(materialOverride);
            ApplyMaterialColor(mat, color);
            return mat;
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = fallbackShader;
        if (shader == null) shader = Shader.Find("Sprites/Default");

        mat = new Material(shader) { mainTexture = Texture2D.whiteTexture };
        if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
        if (color.a < 0.99f && mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = 3000;
        }
        ApplyMaterialColor(mat, color);
        return mat;
    }

    internal static void ApplyMaterialColor(Material mat, Color color)
    {
        if (mat == null) return;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        mat.color = color;
    }

    /// <summary>
    /// Builds a clean 3D extruded arrow prism pointing along +Z in unit XZ bounds.
    /// </summary>
    private static Mesh GetOrCreateArrowMesh()
    {
        if (cachedArrowMesh != null) return cachedArrowMesh;

        const float halfH = 0.05f;
        Vector2[] profile =
        {
            new Vector2( 0.00f,  0.50f), // 0: tip
            new Vector2( 0.45f, -0.05f), // 1: right wing
            new Vector2( 0.17f, -0.05f), // 2: right shoulder
            new Vector2( 0.17f, -0.45f), // 3: right tail
            new Vector2(-0.17f, -0.45f), // 4: left tail
            new Vector2(-0.17f, -0.05f), // 5: left shoulder
            new Vector2(-0.45f, -0.05f), // 6: left wing
        };

        int n = profile.Length;
        var verts = new List<Vector3>(n * 2 + n * 4);
        var tris = new List<int>(64);

        // Top cap (0..n-1)
        for (int i = 0; i < n; i++) verts.Add(new Vector3(profile[i].x, halfH, profile[i].y));
        tris.AddRange(new[] { 0, 1, 6, 2, 3, 4, 2, 4, 5 });

        // Bottom cap (n..2n-1)
        for (int i = 0; i < n; i++) verts.Add(new Vector3(profile[i].x, -halfH, profile[i].y));
        tris.AddRange(new[] { n + 0, n + 6, n + 1, n + 2, n + 4, n + 3, n + 2, n + 5, n + 4 });

        // Side walls with separate vertices for crisp flat shading
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            int baseIdx = verts.Count;
            verts.Add(new Vector3(profile[i].x, halfH, profile[i].y));
            verts.Add(new Vector3(profile[next].x, halfH, profile[next].y));
            verts.Add(new Vector3(profile[next].x, -halfH, profile[next].y));
            verts.Add(new Vector3(profile[i].x, -halfH, profile[i].y));

            tris.Add(baseIdx + 0);
            tris.Add(baseIdx + 3);
            tris.Add(baseIdx + 2);
            tris.Add(baseIdx + 0);
            tris.Add(baseIdx + 2);
            tris.Add(baseIdx + 1);
        }

        cachedArrowMesh = new Mesh { name = "ProceduralNavArrow3D" };
        cachedArrowMesh.SetVertices(verts);
        cachedArrowMesh.SetTriangles(tris, 0);
        cachedArrowMesh.RecalculateNormals();
        cachedArrowMesh.RecalculateBounds();
        return cachedArrowMesh;
    }
}
