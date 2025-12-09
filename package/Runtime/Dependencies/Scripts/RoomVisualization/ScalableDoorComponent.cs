using UnityEngine;

public class ScalableDoorComponent : MonoBehaviour
{
    Transform handle, main, top, left, right;
    Renderer rendTop, rendLeft, rendRight, rendMain;

    Vector2 topBaseLocalXY, leftBaseLocalXY, rightBaseLocalXY;
    Vector2 topTargetWorldXY, leftTargetWorldXY, rightTargetWorldXY;

    float mainBaseHeightLocal, mainBaseWidthLocal;
    float bottomY, originalZScaleSides;

    Vector3 handleBaseLocalScale;
    Vector3 previousParentScale;

    void Awake()
    {
        handle = transform.GetChild(0);
        handleBaseLocalScale = handle.localScale;

        main = transform.GetChild(1);
        rendMain = main.GetComponent<Renderer>();
        var sz = rendMain.localBounds.size;
        mainBaseHeightLocal = Mathf.Max(1e-6f, sz.y);
        mainBaseWidthLocal = Mathf.Max(1e-6f, sz.z);

        top = transform.GetChild(2);
        rendTop = top.GetComponent<Renderer>();
        topBaseLocalXY = new Vector2(rendTop.localBounds.size.x, rendTop.localBounds.size.y);
        {
            float sx = (top.parent ? top.parent : transform).TransformVector(top.localRotation * Vector3.right).magnitude;
            float sy = (top.parent ? top.parent : transform).TransformVector(top.localRotation * Vector3.up).magnitude;
            topTargetWorldXY = new Vector2(topBaseLocalXY.x * sx, topBaseLocalXY.y * sy);
        }

        left = transform.GetChild(3);
        rendLeft = left.GetComponent<Renderer>();
        leftBaseLocalXY = new Vector2(rendLeft.localBounds.size.x, rendLeft.localBounds.size.y);
        {
            float sx = (left.parent ? left.parent : transform).TransformVector(left.localRotation * Vector3.right).magnitude;
            float sy = (left.parent ? left.parent : transform).TransformVector(left.localRotation * Vector3.up).magnitude;
            leftTargetWorldXY = new Vector2(leftBaseLocalXY.x * sx, leftBaseLocalXY.y * sy);
        }

        right = transform.GetChild(4);
        rendRight = right.GetComponent<Renderer>();
        rightBaseLocalXY = new Vector2(rendRight.localBounds.size.x, rendRight.localBounds.size.y);
        {
            float sx = (right.parent ? right.parent : transform).TransformVector(right.localRotation * Vector3.right).magnitude;
            float sy = (right.parent ? right.parent : transform).TransformVector(right.localRotation * Vector3.up).magnitude;
            rightTargetWorldXY = new Vector2(rightBaseLocalXY.x * sx, rightBaseLocalXY.y * sy);
        }

        originalZScaleSides = right.localScale.z;
        bottomY = main.localPosition.y;
    }

    private void LateUpdate()
    {
        if (transform.parent.localScale != previousParentScale)
        {
            ApplyTransform();
            previousParentScale = transform.parent.localScale;
        }
    }

    void ApplyTransform()
    {
        float topParentScaleX = ParentScaleAlong(top, Vector3.right);
        float topParentScaleY = ParentScaleAlong(top, Vector3.up);
        float topScaleX = topTargetWorldXY.x / (topBaseLocalXY.x * topParentScaleX);
        float topScaleY = topTargetWorldXY.y / (topBaseLocalXY.y * topParentScaleY);
        top.localScale = new Vector3(topScaleX, topScaleY, top.localScale.z);

        float leftParentScaleX = ParentScaleAlong(left, Vector3.right);
        float leftParentScaleY = ParentScaleAlong(left, Vector3.up);
        float leftParentScaleZ = ParentScaleAlong(left, Vector3.forward);
        float rightParentScaleX = ParentScaleAlong(right, Vector3.right);
        float rightParentScaleY = ParentScaleAlong(right, Vector3.up);
        float rightParentScaleZ = ParentScaleAlong(right, Vector3.forward);
        float mainParentScaleY = ParentScaleAlong(main, Vector3.up);
        float mainParentScaleZ = ParentScaleAlong(main, Vector3.forward);

        var tLB = rendTop.localBounds;
        Vector3 worldBottomPointTop = top.TransformPoint(new Vector3(tLB.center.x, tLB.min.y, tLB.center.z));
        var rLB = rendRight.localBounds;
        Vector3 worldRimRight = right.TransformPoint(new Vector3(rLB.center.x, rLB.center.y, rLB.min.z));
        var lLB = rendLeft.localBounds;
        Vector3 worldRimLeft = left.TransformPoint(new Vector3(lLB.center.x, lLB.center.y, lLB.max.z));
        Vector3 worldBottomLine = transform.TransformPoint(new Vector3(0f, bottomY, 0f));

        Vector3 localRimRight = transform.InverseTransformPoint(worldRimRight);
        Vector3 localRimLeft = transform.InverseTransformPoint(worldRimLeft);

        float heightMain = WorldDistanceAlong(main, worldBottomLine, worldBottomPointTop, Vector3.up);
        float heightLeft = WorldDistanceAlong(left, worldBottomLine, worldBottomPointTop, Vector3.up);
        float heightRight = WorldDistanceAlong(right, worldBottomLine, worldBottomPointTop, Vector3.up);
        float widthMain = WorldDistanceAlong(main, worldRimRight, worldRimLeft, Vector3.forward);

        float leftScaleX = leftTargetWorldXY.x / (leftBaseLocalXY.x * leftParentScaleX);
        float rightScaleX = rightTargetWorldXY.x / (rightBaseLocalXY.x * rightParentScaleX);
        float leftScaleY = heightLeft / (leftBaseLocalXY.y * leftParentScaleY);
        float rightScaleY = heightRight / (rightBaseLocalXY.y * rightParentScaleY);
        float leftScaleZ = originalZScaleSides / Mathf.Max(1e-6f, leftParentScaleZ);
        float rightScaleZ = originalZScaleSides / Mathf.Max(1e-6f, rightParentScaleZ);

        left.localScale = new Vector3(leftScaleX, leftScaleY, leftScaleZ);
        right.localScale = new Vector3(rightScaleX, rightScaleY, rightScaleZ);

        float zMid = 0.5f * (localRimLeft.z + localRimRight.z);
        main.localPosition = new Vector3(main.localPosition.x, bottomY, zMid);
        left.localPosition = new Vector3(left.localPosition.x, bottomY, localRimLeft.z);
        right.localPosition = new Vector3(right.localPosition.x, bottomY, localRimRight.z);

        float scaleY = heightMain / (mainBaseHeightLocal * mainParentScaleY);
        float scaleZ = widthMain / (mainBaseWidthLocal * mainParentScaleZ);
        main.localScale = new Vector3(main.localScale.x, scaleY, scaleZ);

        var mLB = rendMain.localBounds;
        float minY = mLB.min.y;
        float maxZ = mLB.max.z;
        float h = mLB.size.y;
        float w = mLB.size.z;

        Vector3 handleLocalInMain = new Vector3(mLB.center.x, minY + (h * .45f), maxZ - (0.2f * w));
        Vector3 handleWorld = main.TransformPoint(handleLocalInMain);
        Vector3 handleLocalInParent = transform.InverseTransformPoint(handleWorld);
        handle.localPosition = handleLocalInParent;

        float hsx = ParentScaleAlong(handle, Vector3.right);
        float hsy = ParentScaleAlong(handle, Vector3.up);
        float hsz = ParentScaleAlong(handle, Vector3.forward);
        handle.localScale = new Vector3(
            handleBaseLocalScale.x / Mathf.Max(1e-6f, hsx),
            handleBaseLocalScale.y / Mathf.Max(1e-6f, hsy),
            handleBaseLocalScale.z / Mathf.Max(1e-6f, hsz)
        );
    }

    float ParentScaleAlong(Transform child, Vector3 childLocalAxis)
    {
        Transform p = child.parent ? child.parent : transform;
        return p.TransformVector(child.localRotation * childLocalAxis).magnitude;
    }

    float WorldDistanceAlong(Transform t, Vector3 aWorld, Vector3 bWorld, Vector3 localAxis)
    {
        Vector3 axisW = t.TransformDirection(localAxis);
        return Mathf.Abs(Vector3.Dot(bWorld - aWorld, axisW));
    }
}
