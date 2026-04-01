using UnityEngine;

namespace U2GExporter
{
    /// <summary>
    /// Converts Unity left-handed Y-up transforms to Godot right-handed Y-up Transform3D.
    /// </summary>
    public static class CoordConvert
    {
        // Identity basis + zero origin
        static readonly float[] Identity = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };

        /// <summary>
        /// Converts a Unity local transform to a Godot Transform3D 12-float array.
        /// Layout: basis row0 (3), basis row1 (3), basis row2 (3), origin (3).
        /// Returns null if the result is identity (caller should omit the property).
        /// </summary>
        public static float[] ConvertTransform(Transform t)
        {
            return ConvertTransform(t.localPosition, t.localRotation, t.localScale);
        }

        public static float[] ConvertTransform(Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
        {
            // Step 1: Handedness conversion
            float px = localPosition.x;
            float py = localPosition.y;
            float pz = -localPosition.z;

            float qx = -localRotation.x;
            float qy = -localRotation.y;
            float qz = localRotation.z;
            float qw = localRotation.w;

            float sx = localScale.x;
            float sy = localScale.y;
            float sz = localScale.z;

            // Step 2: Quaternion to 3x3 rotation matrix
            float xx = qx * qx, yy = qy * qy, zz = qz * qz;
            float xy = qx * qy, xz = qx * qz, yz = qy * qz;
            float wx = qw * qx, wy = qw * qy, wz = qw * qz;

            float r00 = 1f - 2f * (yy + zz);
            float r01 = 2f * (xy - wz);
            float r02 = 2f * (xz + wy);

            float r10 = 2f * (xy + wz);
            float r11 = 1f - 2f * (xx + zz);
            float r12 = 2f * (yz - wx);

            float r20 = 2f * (xz - wy);
            float r21 = 2f * (yz + wx);
            float r22 = 1f - 2f * (xx + yy);

            // Step 3: Apply scale to columns
            float b00 = r00 * sx, b01 = r01 * sy, b02 = r02 * sz;
            float b10 = r10 * sx, b11 = r11 * sy, b12 = r12 * sz;
            float b20 = r20 * sx, b21 = r21 * sy, b22 = r22 * sz;

            // Step 5: Check for identity
            if (IsIdentity(b00, b01, b02, b10, b11, b12, b20, b21, b22, px, py, pz))
                return null;

            // Step 4: Serialize row-by-row then origin
            return new float[]
            {
                b00, b01, b02,
                b10, b11, b12,
                b20, b21, b22,
                px, py, pz
            };
        }

        static bool IsIdentity(float b00, float b01, float b02,
                               float b10, float b11, float b12,
                               float b20, float b21, float b22,
                               float px, float py, float pz)
        {
            const float eps = 1e-6f;
            return Mathf.Abs(b00 - 1f) < eps && Mathf.Abs(b01) < eps && Mathf.Abs(b02) < eps
                && Mathf.Abs(b10) < eps && Mathf.Abs(b11 - 1f) < eps && Mathf.Abs(b12) < eps
                && Mathf.Abs(b20) < eps && Mathf.Abs(b21) < eps && Mathf.Abs(b22 - 1f) < eps
                && Mathf.Abs(px) < eps && Mathf.Abs(py) < eps && Mathf.Abs(pz) < eps;
        }
    }
}
