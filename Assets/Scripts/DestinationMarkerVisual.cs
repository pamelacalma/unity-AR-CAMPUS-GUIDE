using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Manages the 3D AR marker shown at the currently active destination (a floating
/// rotating pin + pulsing floor ring by default, or an optional custom prefab).
/// </summary>
public class DestinationMarkerVisual
{
    private static Mesh cachedPinMesh;
    private static Mesh cachedRingMesh;

    private GameObject root;
    private GameObject customInstance;
    private Transform pinTransform;
    private Transform ringTransform;
    private Material pinMaterial;
    private Material ringMaterial;

    private Scene targetScene;
    private GameObject activePrefab;
    private Shader cachedFallbackShader;
    private Color lastAppliedColor;
    private bool hasAppliedColor;

    public void Configure(Scene scene, GameObject prefab, Shader fallbackShader)
    {
        targetScene = scene;
        if (fallbackShader != null) cachedFallbackShader = fallbackShader;

        if (activePrefab != prefab)
        {
            ClearVisual();
            activePrefab = prefab;
        }
    }

    public void UpdateVisual(
        Vector3 floorPosition,
        Color color,
        bool arrived,
        float pinHeight,
        float ringRadius,
        float time)
    {
        EnsureRoot(color);
        if (!root.activeSelf) root.SetActive(true);
        root.transform.position = floorPosition;

        if (activePrefab != null)
        {
            if (customInstance == null)
                customInstance = Object.Instantiate(activePrefab, root.transform, false);
            return;
        }

        if (!hasAppliedColor || lastAppliedColor != color)
        {
            PathArrowPool.ApplyMaterialColor(pinMaterial, color);
            Color ringColor = new Color(color.r, color.g, color.b, Mathf.Clamp01(color.a * 0.72f));
            PathArrowPool.ApplyMaterialColor(ringMaterial, ringColor);
            lastAppliedColor = color;
            hasAppliedColor = true;
        }

        if (ringTransform != null)
        {
            float pulseSpeed = arrived ? 4.5f : 2.8f;
            float pulseAmp = arrived ? 0.14f : 0.08f;
            float r = Mathf.Max(0.2f, ringRadius) * (1f + pulseAmp * Mathf.Sin(time * pulseSpeed));
            ringTransform.localPosition = new Vector3(0f, 0.03f, 0f);
            ringTransform.localScale = new Vector3(r, 1f, r);
        }

        if (pinTransform != null)
        {
            float bob = 0.10f * Mathf.Sin(time * 2.5f);
            pinTransform.localPosition = new Vector3(0f, Mathf.Max(0.4f, pinHeight) + bob, 0f);
            pinTransform.localRotation = Quaternion.Euler(0f, time * (arrived ? 115f : 70f), 0f);
        }
    }

    public void Hide()
    {
        if (root != null && root.activeSelf)
            root.SetActive(false);
    }

    public void Dispose()
    {
        ClearVisual();
        if (pinMaterial != null) { Object.Destroy(pinMaterial); pinMaterial = null; }
        if (ringMaterial != null) { Object.Destroy(ringMaterial); ringMaterial = null; }
        hasAppliedColor = false;
    }

    private void ClearVisual()
    {
        if (root != null)
        {
            Object.Destroy(root);
            root = null;
        }
        customInstance = null;
        pinTransform = null;
        ringTransform = null;
    }

    private void EnsureRoot(Color initialColor)
    {
        if (root != null) return;

        root = new GameObject("ActiveNavDestinationMarker");
        if (targetScene.IsValid() && targetScene.isLoaded)
            SceneManager.MoveGameObjectToScene(root, targetScene);

        if (activePrefab != null) return;

        if (pinMaterial == null)
            pinMaterial = PathArrowPool.CreateUnlitMaterial(null, initialColor, cachedFallbackShader);

        if (ringMaterial == null)
        {
            Color ringColor = new Color(initialColor.r, initialColor.g, initialColor.b, Mathf.Clamp01(initialColor.a * 0.72f));
            ringMaterial = PathArrowPool.CreateUnlitMaterial(null, ringColor, cachedFallbackShader);
        }

        var ringGo = new GameObject("FloorRing");
        ringGo.transform.SetParent(root.transform, false);
        ringGo.AddComponent<MeshFilter>().sharedMesh = GetOrCreateRingMesh();
        var ringRenderer = ringGo.AddComponent<MeshRenderer>();
        ringRenderer.sharedMaterial = ringMaterial;
        ringRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ringRenderer.receiveShadows = false;
        ringTransform = ringGo.transform;

        var pinGo = new GameObject("FloatingPin");
        pinGo.transform.SetParent(root.transform, false);
        pinGo.AddComponent<MeshFilter>().sharedMesh = GetOrCreatePinMesh();
        var pinRenderer = pinGo.AddComponent<MeshRenderer>();
        pinRenderer.sharedMaterial = pinMaterial;
        pinRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        pinRenderer.receiveShadows = false;
        pinTransform = pinGo.transform;
    }

    private static Mesh GetOrCreateRingMesh()
    {
        if (cachedRingMesh != null) return cachedRingMesh;

        const int segments = 36;
        const float innerR = 0.68f;
        const float outerR = 1.00f;

        var verts = new List<Vector3>(segments * 2);
        var tris = new List<int>(segments * 6);

        for (int i = 0; i < segments; i++)
        {
            float a = (i / (float)segments) * Mathf.PI * 2f;
            float c = Mathf.Cos(a);
            float s = Mathf.Sin(a);
            verts.Add(new Vector3(c * innerR, 0f, s * innerR));
            verts.Add(new Vector3(c * outerR, 0f, s * outerR));
        }

        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            int i0 = i * 2;
            int o0 = i * 2 + 1;
            int i1 = next * 2;
            int o1 = next * 2 + 1;

            tris.Add(i0);
            tris.Add(o1);
            tris.Add(o0);
            tris.Add(i0);
            tris.Add(i1);
            tris.Add(o1);
        }

        cachedRingMesh = new Mesh { name = "ProceduralMarkerRing" };
        cachedRingMesh.SetVertices(verts);
        cachedRingMesh.SetTriangles(tris, 0);
        cachedRingMesh.RecalculateNormals();
        cachedRingMesh.RecalculateBounds();
        return cachedRingMesh;
    }

    /// <summary>
    /// Builds an octagonal downward-pointing AR pin prism (bottom tip at y = -0.35, crown at y = +0.28).
    /// </summary>
    private static Mesh GetOrCreatePinMesh()
    {
        if (cachedPinMesh != null) return cachedPinMesh;

        const int sides = 8;
        const float waistR = 0.19f;
        const float tipY = -0.35f;
        const float waistY = 0.10f;
        const float crownY = 0.28f;

        var verts = new List<Vector3>();
        var tris = new List<int>();
        Vector3 bottom = new Vector3(0f, tipY, 0f);
        Vector3 top = new Vector3(0f, crownY, 0f);

        for (int i = 0; i < sides; i++)
        {
            float a0 = (i / (float)sides) * Mathf.PI * 2f;
            float a1 = ((i + 1) / (float)sides) * Mathf.PI * 2f;
            Vector3 p0 = new Vector3(Mathf.Cos(a0) * waistR, waistY, Mathf.Sin(a0) * waistR);
            Vector3 p1 = new Vector3(Mathf.Cos(a1) * waistR, waistY, Mathf.Sin(a1) * waistR);

            // Lower cone face (pointing down)
            int bIdx = verts.Count;
            verts.Add(bottom);
            verts.Add(p0);
            verts.Add(p1);
            tris.Add(bIdx);
            tris.Add(bIdx + 1);
            tris.Add(bIdx + 2);

            // Upper crown face
            int tIdx = verts.Count;
            verts.Add(top);
            verts.Add(p1);
            verts.Add(p0);
            tris.Add(tIdx);
            tris.Add(tIdx + 1);
            tris.Add(tIdx + 2);
        }

        cachedPinMesh = new Mesh { name = "ProceduralMarkerPin3D" };
        cachedPinMesh.SetVertices(verts);
        cachedPinMesh.SetTriangles(tris, 0);
        cachedPinMesh.RecalculateNormals();
        cachedPinMesh.RecalculateBounds();
        return cachedPinMesh;
    }
}
