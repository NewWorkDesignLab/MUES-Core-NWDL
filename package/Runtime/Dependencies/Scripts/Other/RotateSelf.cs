using UnityEngine;

public class RotateSelf : MonoBehaviour
{
    public float rotationSpeed;
    public string rotationAxis;
    public bool active;

    private Vector3 rotationVector;
    private Quaternion startRotation;

    private void Start()
    {
        startRotation = transform.rotation;

        if (rotationAxis == "X")
            rotationVector = Vector3.right;
        else if (rotationAxis == "Y")
            rotationVector = Vector3.up;
        else if (rotationAxis == "Z")
            rotationVector = Vector3.forward;
        else
            Debug.LogError("No valid axis (Axis name must be in all Caps)");
    }

    void Update()
    {
        if (active)
            transform.Rotate(rotationVector * rotationSpeed * Time.deltaTime);
    }

    public void ResetRotation()
    {
        active = false;
        transform.rotation = startRotation;
    }
}
