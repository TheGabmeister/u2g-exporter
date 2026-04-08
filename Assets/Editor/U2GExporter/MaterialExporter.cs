using System.IO;
using UnityEditor;
using UnityEngine;

namespace U2GExporter
{
    public static class MaterialExporter
    {
        public static void Export(string assetPath, string outputDir, SkipReport skipReport)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (material == null)
            {
                Debug.LogError($"[U2G][ERROR] Failed to load material: {assetPath}");
                return;
            }

            string shaderName = material.shader.name;
            var writer = new TresWriter();

            switch (shaderName)
            {
                case "Universal Render Pipeline/Lit":
                    WriteURPLit(material, writer);
                    break;
                case "Universal Render Pipeline/Unlit":
                    WriteURPUnlit(material, writer);
                    break;
                case "Standard":
                    WriteLegacyStandard(material, writer);
                    break;
                case "Legacy Shaders/Diffuse":
                case "Legacy Shaders/Specular":
                case "Legacy Shaders/Bumped Diffuse":
                case "Legacy Shaders/Bumped Specular":
                case "Mobile/Diffuse":
                case "Mobile/Bumped Diffuse":
                case "Mobile/Bumped Specular":
                case "Mobile/Unlit (Supports Lightmap)":
                    WriteLegacyBuiltin(material, writer, transparent: false);
                    break;
                case "Legacy Shaders/Transparent/Diffuse":
                case "Legacy Shaders/Transparent/Specular":
                case "Legacy Shaders/Transparent/Bumped Diffuse":
                case "Legacy Shaders/Transparent/Bumped Specular":
                    WriteLegacyBuiltin(material, writer, transparent: true);
                    break;
                default:
                    Debug.LogWarning($"[U2G][WARN] Unknown shader '{shaderName}' on material '{assetPath}'. Using default white material.");
                    WriteDefaultWhite(writer);
                    break;
            }

            string relativePath = PathUtil.UnityToGodotRelativePath(assetPath);
            relativePath = Path.ChangeExtension(relativePath, ".tres");
            string destPath = Path.Combine(outputDir, relativePath);
            writer.Write(destPath);

            Debug.Log($"[U2G][INFO] Converted material: {assetPath}");
        }

        static void WriteURPLit(Material mat, TresWriter w)
        {
            // Albedo
            Color baseColor = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
            w.AddPropertyColor("albedo_color", baseColor.r, baseColor.g, baseColor.b, baseColor.a);

            WriteTextureProperty(mat, w, "_BaseMap", "albedo_texture", "Texture2D");

            // Metallic
            if (mat.HasProperty("_Metallic"))
                w.AddPropertyFloat("metallic", mat.GetFloat("_Metallic"));

            // Metallic texture
            if (mat.HasProperty("_MetallicGlossMap") && mat.GetTexture("_MetallicGlossMap") != null)
            {
                string id = AddTextureExtResource(mat, w, "_MetallicGlossMap");
                if (id != null)
                {
                    w.AddPropertyExtResource("metallic_texture", id);
                    w.AddProperty("metallic_texture_channel", "0"); // TEXTURE_CHANNEL_RED = 0
                }
            }

            // Roughness (inverted smoothness)
            if (mat.HasProperty("_Smoothness"))
                w.AddPropertyFloat("roughness", 1f - mat.GetFloat("_Smoothness"));

            // Normal map
            if (mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") != null)
            {
                w.AddPropertyBool("normal_enabled", true);
                string id = AddTextureExtResource(mat, w, "_BumpMap");
                if (id != null)
                    w.AddPropertyExtResource("normal_texture", id);

                if (mat.HasProperty("_BumpScale"))
                    w.AddPropertyFloat("normal_scale", mat.GetFloat("_BumpScale"));
            }

            // Emission
            WriteEmission(mat, w, "_EmissionColor", "_EmissionMap");

            // Occlusion
            WriteOcclusion(mat, w, "_OcclusionMap", "_OcclusionStrength");

            // Transparency
            if (mat.HasProperty("_Surface"))
            {
                float surface = mat.GetFloat("_Surface");
                if (surface >= 1f) // Transparent
                {
                    float cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0f;
                    if (cutoff > 0f)
                    {
                        w.AddPropertyInt("transparency", 2); // TRANSPARENCY_ALPHA_SCISSOR
                        w.AddPropertyFloat("alpha_scissor_threshold", cutoff);
                    }
                    else
                    {
                        w.AddPropertyInt("transparency", 1); // TRANSPARENCY_ALPHA
                    }
                }
            }

            // Cull mode
            if (mat.HasProperty("_Cull"))
            {
                int cull = (int)mat.GetFloat("_Cull");
                // Unity: 0=off, 1=front, 2=back; Godot: 0=back, 1=front, 2=disabled
                int godotCull;
                switch (cull)
                {
                    case 0: godotCull = 2; break; // off → CULL_DISABLED
                    case 1: godotCull = 1; break; // front → CULL_FRONT
                    default: godotCull = 0; break; // back → CULL_BACK
                }
                w.AddPropertyInt("cull_mode", godotCull);
            }

            // UV tiling/offset
            WriteUVTilingOffset(mat, w, "_BaseMap");
        }

        static void WriteURPUnlit(Material mat, TresWriter w)
        {
            // Unshaded mode
            w.AddPropertyInt("shading_mode", 0); // SHADING_MODE_UNSHADED

            // Albedo
            Color baseColor = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
            w.AddPropertyColor("albedo_color", baseColor.r, baseColor.g, baseColor.b, baseColor.a);

            WriteTextureProperty(mat, w, "_BaseMap", "albedo_texture", "Texture2D");

            // Transparency
            if (mat.HasProperty("_Surface"))
            {
                float surface = mat.GetFloat("_Surface");
                if (surface >= 1f)
                {
                    float cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0f;
                    if (cutoff > 0f)
                    {
                        w.AddPropertyInt("transparency", 2);
                        w.AddPropertyFloat("alpha_scissor_threshold", cutoff);
                    }
                    else
                    {
                        w.AddPropertyInt("transparency", 1);
                    }
                }
            }

            // Cull mode
            if (mat.HasProperty("_Cull"))
            {
                int cull = (int)mat.GetFloat("_Cull");
                int godotCull;
                switch (cull)
                {
                    case 0: godotCull = 2; break;
                    case 1: godotCull = 1; break;
                    default: godotCull = 0; break;
                }
                w.AddPropertyInt("cull_mode", godotCull);
            }

            WriteUVTilingOffset(mat, w, "_BaseMap");
        }

        static void WriteLegacyStandard(Material mat, TresWriter w)
        {
            // Albedo
            Color color = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
            w.AddPropertyColor("albedo_color", color.r, color.g, color.b, color.a);

            WriteTextureProperty(mat, w, "_MainTex", "albedo_texture", "Texture2D");

            // Metallic texture
            if (mat.HasProperty("_MetallicGlossMap") && mat.GetTexture("_MetallicGlossMap") != null)
            {
                string id = AddTextureExtResource(mat, w, "_MetallicGlossMap");
                if (id != null)
                {
                    w.AddPropertyExtResource("metallic_texture", id);
                    w.AddProperty("metallic_texture_channel", "0");
                }
            }

            // Roughness (inverted glossiness)
            if (mat.HasProperty("_Glossiness"))
                w.AddPropertyFloat("roughness", 1f - mat.GetFloat("_Glossiness"));

            // Normal map
            if (mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") != null)
            {
                w.AddPropertyBool("normal_enabled", true);
                string id = AddTextureExtResource(mat, w, "_BumpMap");
                if (id != null)
                    w.AddPropertyExtResource("normal_texture", id);
            }

            // Emission
            WriteEmission(mat, w, "_EmissionColor", "_EmissionMap");

            // Occlusion
            WriteOcclusion(mat, w, "_OcclusionMap", null);

            // UV tiling/offset
            WriteUVTilingOffset(mat, w, "_MainTex");
        }

        static void WriteLegacyBuiltin(Material mat, TresWriter w, bool transparent)
        {
            // Albedo
            Color color = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
            w.AddPropertyColor("albedo_color", color.r, color.g, color.b, color.a);

            WriteTextureProperty(mat, w, "_MainTex", "albedo_texture", "Texture2D");

            // Shininess → roughness (Specular / Bumped Specular variants)
            if (mat.HasProperty("_Shininess"))
                w.AddPropertyFloat("roughness", 1f - mat.GetFloat("_Shininess"));

            // Normal map (Bumped variants)
            if (mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") != null)
            {
                w.AddPropertyBool("normal_enabled", true);
                string id = AddTextureExtResource(mat, w, "_BumpMap");
                if (id != null)
                    w.AddPropertyExtResource("normal_texture", id);
            }

            // Emission (if present on this shader)
            WriteEmission(mat, w, "_EmissionColor", "_EmissionMap");

            // Transparency (Transparent/* variants)
            if (transparent)
                w.AddPropertyInt("transparency", 1); // TRANSPARENCY_ALPHA

            // UV tiling/offset
            WriteUVTilingOffset(mat, w, "_MainTex");
        }

        static void WriteDefaultWhite(TresWriter w)
        {
            w.AddPropertyColor("albedo_color", 1f, 1f, 1f, 1f);
        }

        static void WriteEmission(Material mat, TresWriter w, string colorProp, string mapProp)
        {
            bool hasMap = mat.HasProperty(mapProp) && mat.GetTexture(mapProp) != null;
            bool hasColor = mat.HasProperty(colorProp);
            Color emissionColor = hasColor ? mat.GetColor(colorProp) : Color.black;

            // Enable emission if map exists or color is non-black
            if (hasMap || (hasColor && (emissionColor.r > 0f || emissionColor.g > 0f || emissionColor.b > 0f)))
            {
                w.AddPropertyBool("emission_enabled", true);

                // Compute energy multiplier from HDR emission color brightness
                float maxChannel = Mathf.Max(emissionColor.r, Mathf.Max(emissionColor.g, emissionColor.b));
                float multiplier = maxChannel > 1f ? maxChannel : 1f;
                Color normalizedEmission = maxChannel > 1f
                    ? new Color(emissionColor.r / maxChannel, emissionColor.g / maxChannel, emissionColor.b / maxChannel, 1f)
                    : new Color(emissionColor.r, emissionColor.g, emissionColor.b, 1f);

                w.AddPropertyColor("emission", normalizedEmission.r, normalizedEmission.g, normalizedEmission.b, normalizedEmission.a);
                w.AddPropertyFloat("emission_energy_multiplier", multiplier);

                if (hasMap)
                {
                    string id = AddTextureExtResource(mat, w, mapProp);
                    if (id != null)
                        w.AddPropertyExtResource("emission_texture", id);
                }
            }
        }

        static void WriteOcclusion(Material mat, TresWriter w, string mapProp, string strengthProp)
        {
            if (mat.HasProperty(mapProp) && mat.GetTexture(mapProp) != null)
            {
                w.AddPropertyBool("ao_enabled", true);

                string id = AddTextureExtResource(mat, w, mapProp);
                if (id != null)
                    w.AddPropertyExtResource("ao_texture", id);

                w.AddProperty("ao_texture_channel", "1"); // TEXTURE_CHANNEL_GREEN = 1

                if (strengthProp != null && mat.HasProperty(strengthProp))
                    w.AddPropertyFloat("ao_light_affect", mat.GetFloat(strengthProp));
            }
        }

        static void WriteTextureProperty(Material mat, TresWriter w, string unityProp, string godotProp, string type)
        {
            if (!mat.HasProperty(unityProp))
                return;

            var tex = mat.GetTexture(unityProp);
            if (tex == null)
                return;

            string texPath = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(texPath))
                return;

            string resPath = PathUtil.UnityToGodotResPath(texPath);
            string id = w.AddExtResource(type, resPath);
            w.AddPropertyExtResource(godotProp, id);
        }

        static string AddTextureExtResource(Material mat, TresWriter w, string unityProp)
        {
            var tex = mat.GetTexture(unityProp);
            if (tex == null)
                return null;

            string texPath = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(texPath))
                return null;

            string resPath = PathUtil.UnityToGodotResPath(texPath);
            return w.AddExtResource("Texture2D", resPath);
        }

        static void WriteUVTilingOffset(Material mat, TresWriter w, string texProp)
        {
            if (!mat.HasProperty(texProp))
                return;

            Vector2 scale = mat.GetTextureScale(texProp);
            Vector2 offset = mat.GetTextureOffset(texProp);

            bool hasScale = Mathf.Abs(scale.x - 1f) > 1e-6f || Mathf.Abs(scale.y - 1f) > 1e-6f;
            bool hasOffset = Mathf.Abs(offset.x) > 1e-6f || Mathf.Abs(offset.y) > 1e-6f;

            if (hasScale || hasOffset)
            {
                w.AddPropertyVector3("uv1_scale", scale.x, scale.y, 1f);
                w.AddPropertyVector3("uv1_offset", offset.x, offset.y, 0f);
            }
        }
    }
}
