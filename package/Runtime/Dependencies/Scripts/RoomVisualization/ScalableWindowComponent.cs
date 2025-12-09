using System.Collections.Generic;
using UnityEngine;

public class ScalableWindowComponent : MonoBehaviour
{
    public GameObject windowSeperatorPrefab;

    Transform bottom, main, top, left, right;
    Renderer rendBottom, rendTop, rendLeft, rendRight, rendMain;

    Vector2 bottomBaseLocalXY, topBaseLocalXY, leftBaseLocalXY, rightBaseLocalXY;
    Vector2 bottomTargetWorldXY, topTargetWorldXY, leftTargetWorldXY, rightTargetWorldXY;

    float mainBaseHeightLocal, mainBaseWidthLocal;
    float originalZScaleSides;

    readonly List<Transform> seps = new List<Transform>();
    readonly List<Renderer> sepsR = new List<Renderer>();
    readonly List<Vector2> sepsBaseLocalXY = new List<Vector2>();

    Vector3 previousParentScale;

    void Awake()
    {
        bottom = transform.GetChild(0);
        rendBottom = bottom.GetComponent<Renderer>();
        bottomBaseLocalXY = new Vector2(rendBottom.localBounds.size.x, rendBottom.localBounds.size.y);
        {
            float sx = (bottom.parent != null ? bottom.parent : transform).TransformVector(bottom.localRotation * Vector3.right).magnitude;
            float sy = (bottom.parent != null ? bottom.parent : transform).TransformVector(bottom.localRotation * Vector3.up).magnitude;
            bottomTargetWorldXY = new Vector2(bottomBaseLocalXY.x * sx, bottomBaseLocalXY.y * sy);
        }

        main = transform.GetChild(1);
        rendMain = main.GetComponent<Renderer>();
        var sz = rendMain.localBounds.size;

        top = transform.GetChild(2);
        rendTop = top.GetComponent<Renderer>();
        topBaseLocalXY = new Vector2(rendTop.localBounds.size.x, rendTop.localBounds.size.y);
        {
            float sx = (top.parent != null ? top.parent : transform).TransformVector(top.localRotation * Vector3.right).magnitude;
            float sy = (top.parent != null ? top.parent : transform).TransformVector(top.localRotation * Vector3.up).magnitude;
            topTargetWorldXY = new Vector2(topBaseLocalXY.x * sx, topBaseLocalXY.y * sy);
        }

        left = transform.GetChild(3);
        rendLeft = left.GetComponent<Renderer>();
        leftBaseLocalXY = new Vector2(rendLeft.localBounds.size.x, rendLeft.localBounds.size.y);
        {
            float sx = (left.parent != null ? left.parent : transform).TransformVector(left.localRotation * Vector3.right).magnitude;
            float sy = (left.parent != null ? left.parent : transform).TransformVector(left.localRotation * Vector3.up).magnitude;
            leftTargetWorldXY = new Vector2(leftBaseLocalXY.x * sx, leftBaseLocalXY.y * sy);
        }

        right = transform.GetChild(4);
        rendRight = right.GetComponent<Renderer>();
        rightBaseLocalXY = new Vector2(rendRight.localBounds.size.x, rendRight.localBounds.size.y);
        {
            float sx = (right.parent != null ? right.parent : transform).TransformVector(right.localRotation * Vector3.right).magnitude;
            float sy = (right.parent != null ? right.parent : transform).TransformVector(right.localRotation * Vector3.up).magnitude;
            rightTargetWorldXY = new Vector2(rightBaseLocalXY.x * sx, rightBaseLocalXY.y * sy);
        }

        originalZScaleSides = right.localScale.z;

        mainBaseHeightLocal = Mathf.Max(1e-6f, sz.y);
        mainBaseWidthLocal = Mathf.Max(1e-6f, sz.z);
    }

    private void LateUpdate()
    {
        if (transform.parent != null && transform.parent.localScale != previousParentScale)
        {
            ApplyTransform();
            previousParentScale = transform.parent.localScale;
        }
    }

    public void ApplyTransform()
    {
        var bLB = rendBottom.localBounds;
        Vector3 worldTopPointBottom = bottom.TransformPoint(new Vector3(bLB.center.x, bLB.max.y, bLB.center.z));

        var tLB = rendTop.localBounds;
        Vector3 worldBottomPointTop = top.TransformPoint(new Vector3(tLB.center.x, tLB.min.y, tLB.center.z));

        var rLB = rendRight.localBounds;
        Vector3 worldRimRight = right.TransformPoint(new Vector3(rLB.center.x, rLB.center.y, rLB.min.z));

        var lLB = rendLeft.localBounds;
        Vector3 worldRimLeft = left.TransformPoint(new Vector3(lLB.center.x, lLB.center.y, lLB.max.z));

        Vector3 localTopPointBottom = transform.InverseTransformPoint(worldTopPointBottom);
        Vector3 localBottomPointTop = transform.InverseTransformPoint(worldBottomPointTop);
        Vector3 localRimRight = transform.InverseTransformPoint(worldRimRight);
        Vector3 localRimLeft = transform.InverseTransformPoint(worldRimLeft);

        float bottomParentScaleX = ParentScaleAlong(bottom, Vector3.right);
        float bottomParentScaleY = ParentScaleAlong(bottom, Vector3.up);
        float topParentScaleX = ParentScaleAlong(top, Vector3.right);
        float topParentScaleY = ParentScaleAlong(top, Vector3.up);

        float bottomScaleX = bottomTargetWorldXY.x / (bottomBaseLocalXY.x * bottomParentScaleX);
        float bottomScaleY = bottomTargetWorldXY.y / (bottomBaseLocalXY.y * bottomParentScaleY);
        bottom.localScale = new Vector3(bottomScaleX, bottomScaleY, bottom.localScale.z);

        float topScaleX = topTargetWorldXY.x / (topBaseLocalXY.x * topParentScaleX);
        float topScaleY = topTargetWorldXY.y / (topBaseLocalXY.y * topParentScaleY);
        top.localScale = new Vector3(topScaleX, topScaleY, top.localScale.z);

        float heightMain = WorldDistanceAlong(main, worldTopPointBottom, worldBottomPointTop, Vector3.up);
        float heightLeft = WorldDistanceAlong(left, worldTopPointBottom, worldBottomPointTop, Vector3.up);
        float heightRight = WorldDistanceAlong(right, worldTopPointBottom, worldBottomPointTop, Vector3.up);
        float widthMain = WorldDistanceAlong(main, worldRimRight, worldRimLeft, Vector3.forward);

        float leftParentScaleX = ParentScaleAlong(left, Vector3.right);
        float leftParentScaleY = ParentScaleAlong(left, Vector3.up);
        float leftParentScaleZ = ParentScaleAlong(left, Vector3.forward);
        float rightParentScaleX = ParentScaleAlong(right, Vector3.right);
        float rightParentScaleY = ParentScaleAlong(right, Vector3.up);
        float rightParentScaleZ = ParentScaleAlong(right, Vector3.forward);
        float mainParentScaleY = ParentScaleAlong(main, Vector3.up);
        float mainParentScaleZ = ParentScaleAlong(main, Vector3.forward);

        float leftScaleX = leftTargetWorldXY.x / (leftBaseLocalXY.x * leftParentScaleX);
        float rightScaleX = rightTargetWorldXY.x / (rightBaseLocalXY.x * rightParentScaleX);
        float leftScaleY = heightLeft / (leftBaseLocalXY.y * leftParentScaleY);
        float rightScaleY = heightRight / (rightBaseLocalXY.y * rightParentScaleY);
        float leftScaleZ = originalZScaleSides / Mathf.Max(1e-6f, leftParentScaleZ);
        float rightScaleZ = originalZScaleSides / Mathf.Max(1e-6f, rightParentScaleZ);

        left.localScale = new Vector3(leftScaleX, leftScaleY, leftScaleZ);
        right.localScale = new Vector3(rightScaleX, rightScaleY, rightScaleZ);

        float zMid = 0.5f * (localRimLeft.z + localRimRight.z);
        main.localPosition = new Vector3(main.localPosition.x, localTopPointBottom.y, zMid);
        left.localPosition = new Vector3(left.localPosition.x, localTopPointBottom.y, localRimLeft.z);
        right.localPosition = new Vector3(right.localPosition.x, localTopPointBottom.y, localRimRight.z);

        float scaleY = heightMain / (mainBaseHeightLocal * mainParentScaleY);
        float scaleZ = widthMain / (mainBaseWidthLocal * mainParentScaleZ);
        main.localScale = new Vector3(main.localScale.x, scaleY, scaleZ);

        float worldSpan = Vector3.Distance(worldRimLeft, worldRimRight);

        int sepCount = worldSpan >= 2f ? Mathf.FloorToInt(worldSpan / 2f) : 0;
        EnsureSepsCount(sepCount);

        if (sepCount == 0)
            return;

        Vector3 localLeft = transform.InverseTransformPoint(worldRimLeft);
        Vector3 localRight = transform.InverseTransformPoint(worldRimRight);

        int segments = sepCount + 1;

        for (int i = 0; i < sepCount; i++)
        {
            float t = (i + 1f) / segments;
            Vector3 localOnBottom = Vector3.Lerp(localLeft, localRight, t);

            var sep = seps[i];
            var baseXY = sepsBaseLocalXY[i];

            float sepParentScaleX = ParentScaleAlong(sep, Vector3.right);
            float sepParentScaleY = ParentScaleAlong(sep, Vector3.up);
            float sepParentScaleZ = ParentScaleAlong(sep, Vector3.forward);

            float sepScaleX = leftTargetWorldXY.x / (baseXY.x * Mathf.Max(1e-6f, sepParentScaleX));
            float sepScaleY = heightMain / (baseXY.y * Mathf.Max(1e-6f, sepParentScaleY));
            float sepScaleZ = originalZScaleSides / Mathf.Max(1e-6f, sepParentScaleZ);

            sep.localScale = new Vector3(sepScaleX, sepScaleY, sepScaleZ);

            sep.localPosition = new Vector3(
                localOnBottom.x,
                localTopPointBottom.y,
                localOnBottom.z
            );
        }
    }

    float ParentScaleAlong(Transform child, Vector3 childLocalAxis)
    {
        Transform p = child.parent != null ? child.parent : transform;
        return p.TransformVector(child.localRotation * childLocalAxis).magnitude;
    }

    float WorldDistanceAlong(Transform t, Vector3 aWorld, Vector3 bWorld, Vector3 localAxis)
    {
        Vector3 axisW = t.TransformDirection(localAxis);
        return Mathf.Abs(Vector3.Dot(bWorld - aWorld, axisW));
    }

    void EnsureSepsCount(int count)
    {
        if (windowSeperatorPrefab == null)
        {
            while (seps.Count > 0)
            {
                int i = seps.Count - 1;
                if (seps[i] != null) Destroy(seps[i].gameObject);
                seps.RemoveAt(i);
                sepsR.RemoveAt(i);
                sepsBaseLocalXY.RemoveAt(i);
            }
            return;
        }

        while (seps.Count < count)
        {
            var go = Instantiate(windowSeperatorPrefab, transform);
            var tr = go.transform;
            var r = go.GetComponentInChildren<Renderer>();
            seps.Add(tr);
            sepsR.Add(r);
            var lb = r != null ? r.localBounds : new Bounds(Vector3.zero, Vector3.one);
            var baseXY = new Vector2(lb.size.x, lb.size.y);
            sepsBaseLocalXY.Add(baseXY);
        }

        while (seps.Count > count)
        {
            int i = seps.Count - 1;
            if (seps[i] != null) Destroy(seps[i].gameObject);
            seps.RemoveAt(i);
            sepsR.RemoveAt(i);
            sepsBaseLocalXY.RemoveAt(i);
        }
    }
}
