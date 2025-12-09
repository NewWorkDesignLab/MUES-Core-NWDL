using UnityEngine;

public class LookAtCamera : MonoBehaviour
{
    public bool lockXRotation;
    public bool lockYRotation;
    public bool lockZRotation;

    Camera mainCam;

    void Start() => mainCam = Camera.main;

    void Update()
    {
        transform.LookAt(mainCam.transform.position);

        float xRotation = lockXRotation ? 0 : transform.rotation.eulerAngles.x;
        float yRotation = lockYRotation ? 0 : transform.rotation.eulerAngles.y;
        float zRotation = lockZRotation ? 0 : transform.rotation.eulerAngles.z;

        if (lockXRotation && lockYRotation && lockZRotation)
            Debug.LogWarning("All Axis locked!");

        transform.rotation = Quaternion.Euler(xRotation, yRotation, zRotation);
    }
}
