using UnityEngine;

public class ScalableWallComponent : MonoBehaviour
{
    Transform bottom, main;
    Renderer rendBottom;

    float originalYScaleMain, originalYPositionMain;
    Vector2 baseScaleBottom;

    Vector3 previousParentScale;

    private void Awake()
    {
        bottom = transform.GetChild(0);
        rendBottom = bottom.GetComponent<Renderer>();

        main = transform.GetChild(1);

        baseScaleBottom = new Vector2(bottom.localScale.x, bottom.localScale.y);
        originalYScaleMain = main.localScale.y;
        originalYPositionMain = main.localPosition.y;
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
        Quaternion childRot = bottom.localRotation;
        float parentScaleAlongChildX = transform.TransformVector(childRot * Vector3.right).magnitude;
        float parentScaleAlongChildY = transform.TransformVector(childRot * Vector3.up).magnitude;

        float localScaleX = baseScaleBottom.x / Mathf.Max(1e-6f, parentScaleAlongChildX);
        float localScaleY = baseScaleBottom.y / Mathf.Max(1e-6f, parentScaleAlongChildY);
        bottom.localScale = new Vector3(localScaleX, localScaleY, bottom.localScale.z);

        var lb = rendBottom.localBounds;
        Vector3 worldTopPoint = bottom.TransformPoint(new Vector3(lb.center.x, lb.max.y, lb.center.z));
        Vector3 localTopPoint = transform.InverseTransformPoint(worldTopPoint);

        main.localPosition = new Vector3(main.localPosition.x, localTopPoint.y, main.localPosition.z);

        float yScale = originalYScaleMain + (originalYPositionMain - main.localPosition.y);
        main.localScale = new Vector3(main.localScale.x, yScale, main.localScale.z);
    }
}


