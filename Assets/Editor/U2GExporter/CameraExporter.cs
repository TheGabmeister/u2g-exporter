using UnityEngine;

namespace U2GExporter
{
    public static class CameraExporter
    {
        public static void WriteProperties(TscnWriter writer, Camera cam)
        {
            if (cam.orthographic)
            {
                writer.AddPropertyInt("projection", 1); // PROJECTION_ORTHOGONAL
                writer.AddPropertyFloat("size", cam.orthographicSize);
            }
            else
            {
                writer.AddPropertyFloat("fov", cam.fieldOfView);
            }

            writer.AddPropertyFloat("near", cam.nearClipPlane);
            writer.AddPropertyFloat("far", cam.farClipPlane);
        }
    }
}
