using System.Collections.Generic;
using UnityEngine;

public class ScalableTableComponent : MonoBehaviour
{
    public GameObject legPrefab;

    Transform tableTop;
    Renderer topRend;

    float legInset = .05f;
    float legSpacingStep = 3f;

    class LegData
    {
        public Transform root;
        public Transform shaft;
        public Renderer shaftRenderer;
        public Renderer baseRenderer;
        public Vector3 baseLocalScale;
        public float shaftBaseHeightLocal;
    }

    readonly List<LegData> legs = new List<LegData>();

    Vector3 previousParentScale;

    void Awake()
    {
        tableTop = transform.GetChild(0);
        topRend = tableTop.GetComponent<Renderer>();
    }

    void LateUpdate()
    {
        if (transform.parent.localScale != previousParentScale)
        {
            ApplyTransform();
            previousParentScale = transform.parent.localScale;
        }
    }

    void ApplyTransform()
    {
        if (!legPrefab || !topRend) return;

        var lb = topRend.localBounds;
        var c = lb.center;
        var e = lb.extents;

        float invSx = 1f / Mathf.Max(1e-6f, transform.localScale.x);
        float invSy = 1f / Mathf.Max(1e-6f, transform.localScale.y);
        float invSz = 1f / Mathf.Max(1e-6f, transform.localScale.z);

        float insetX = legInset * invSx;
        float insetZ = legInset * invSz;
        float yBottom = lb.min.y;

        Vector3[] localCorners =
        {
        new Vector3(c.x - (e.x - insetX), yBottom, c.z - (e.z - insetZ)),
        new Vector3(c.x + (e.x - insetX), yBottom, c.z - (e.z - insetZ)),
        new Vector3(c.x - (e.x - insetX), yBottom, c.z + (e.z - insetZ)),
        new Vector3(c.x + (e.x - insetX), yBottom, c.z + (e.z - insetZ))
    };

        int xDiv = Mathf.FloorToInt(transform.localScale.x / legSpacingStep);
        int zDiv = Mathf.FloorToInt(transform.localScale.z / legSpacingStep);

        var desired = new List<Vector3>();
        AddLegLine(localCorners[0], localCorners[1], xDiv, desired);
        AddLegLine(localCorners[2], localCorners[3], xDiv, desired);
        AddLegLine(localCorners[0], localCorners[2], zDiv, desired);
        AddLegLine(localCorners[1], localCorners[3], zDiv, desired);

        for (int i = desired.Count - 1; i >= 0; i--)
        {
            for (int j = i - 1; j >= 0; j--)
            {
                if ((desired[i] - desired[j]).sqrMagnitude < 1e-6f)
                {
                    desired.RemoveAt(i);
                    break;
                }
            }
        }

        while (legs.Count < desired.Count)
        {
            var legRoot = Instantiate(legPrefab, transform).transform;
            legRoot.rotation = transform.rotation;

            Transform shaft = legRoot.childCount > 0 ? legRoot.GetChild(0) : null;
            Renderer shaftR = shaft ? shaft.GetComponentInChildren<Renderer>() : null;
            Renderer baseR = legRoot.GetComponentInChildren<Renderer>();

            float shaftBaseH = Mathf.Max(1e-6f, shaftR ? shaftR.localBounds.size.y : 1f);

            legs.Add(new LegData
            {
                root = legRoot,
                shaft = shaft,
                shaftRenderer = shaftR,
                baseRenderer = baseR,
                baseLocalScale = legRoot.localScale,
                shaftBaseHeightLocal = shaftBaseH
            });
        }

        while (legs.Count > desired.Count)
        {
            int idx = legs.Count - 1;
            if (legs[idx].root) Destroy(legs[idx].root.gameObject);
            legs.RemoveAt(idx);
        }

        float tableBottomWorldY = topRend.bounds.min.y;

        for (int i = 0; i < desired.Count; i++)
        {
            var ld = legs[i];
            Vector3 localPos = desired[i];
            Vector3 worldCorner = tableTop.TransformPoint(localPos);

            Vector3 worldBasePos = new Vector3(worldCorner.x, MUES_RoomVisualizer.floorHeight, worldCorner.z);
            ld.root.position = worldBasePos;

            ld.root.localScale = new Vector3(
                ld.baseLocalScale.x * invSx,
                ld.baseLocalScale.y * invSy,
                ld.baseLocalScale.z * invSz
            );

            if (ld.shaft && ld.baseRenderer)
            {
                float baseTopWorldY = ld.baseRenderer.bounds.max.y;
                float heightWorld = Mathf.Max(0f, tableBottomWorldY - baseTopWorldY);
                float localHeight = heightWorld / Mathf.Max(1e-6f, ld.root.lossyScale.y);
                float newY = localHeight / Mathf.Max(1e-6f, ld.shaftBaseHeightLocal);

                Vector3 s = ld.shaft.localScale;
                ld.shaft.localScale = new Vector3(s.x, newY, s.z);

                Vector3 baseTopWorldPoint = new Vector3(
                    ld.baseRenderer.bounds.center.x,
                    baseTopWorldY,
                    ld.baseRenderer.bounds.center.z
                );

                Vector3 baseTopLocal = ld.root.InverseTransformPoint(baseTopWorldPoint);

                Vector3 lp = ld.shaft.localPosition;
                ld.shaft.localPosition = new Vector3(lp.x, baseTopLocal.y, lp.z);
            }
        }
    }

    void AddLegLine(Vector3 start, Vector3 end, int divisions, List<Vector3> list)
    {
        list.Add(start);
        for (int i = 0; i < divisions; i++)
        {
            float t = (i + 1f) / (divisions + 1f);
            list.Add(Vector3.Lerp(start, end, t));
        }
        list.Add(end);
    }
}
