using UnityEngine;

namespace U2GExporter
{
    public static class LightExporter
    {
        public struct LightData
        {
            public string GodotType;
            public Color Color;
            public float Energy;
            public float Range;
            public float SpotAngle;
            public bool ShadowEnabled;
            public bool IsValid;
        }

        public static LightData Extract(Light light)
        {
            var data = new LightData
            {
                IsValid = true,
                Color = light.color,
                Energy = light.intensity,
                ShadowEnabled = light.shadows != LightShadows.None
            };

            switch (light.type)
            {
                case LightType.Directional:
                    data.GodotType = "DirectionalLight3D";
                    break;
                case LightType.Point:
                    data.GodotType = "OmniLight3D";
                    data.Range = light.range;
                    break;
                case LightType.Spot:
                    data.GodotType = "SpotLight3D";
                    data.Range = light.range;
                    data.SpotAngle = light.spotAngle / 2f;
                    break;
                default:
                    data.IsValid = false;
                    break;
            }

            return data;
        }

        public static void WriteProperties(TscnWriter writer, LightData data)
        {
            writer.AddPropertyColor("light_color", data.Color.r, data.Color.g, data.Color.b, data.Color.a);
            writer.AddPropertyFloat("light_energy", data.Energy);

            if (data.ShadowEnabled)
                writer.AddPropertyBool("shadow_enabled", true);

            if (data.GodotType == "OmniLight3D")
            {
                writer.AddPropertyFloat("omni_range", data.Range);
            }
            else if (data.GodotType == "SpotLight3D")
            {
                writer.AddPropertyFloat("spot_range", data.Range);
                writer.AddPropertyFloat("spot_angle", data.SpotAngle);
            }
        }
    }
}
