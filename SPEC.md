# Unity2Godot Exporter — V1 Specification

## Overview

A Unity Editor tool that exports selected assets from a Unity project into a ready-to-open Godot 4.6.1 project. The user right-clicks a folder in the Project window, selects "Export to Godot", picks an output directory, and receives a complete Godot project. V1 is limited to static meshes (FBX), textures, materials (URP + legacy), scenes, and prefabs. No C# scripts, custom shaders, skinned meshes, animations, or audio.

**Target Godot version:** 4.6.1
**Required Unity version:** Unity 6 (6000.x)

---

## Architecture

### Platform & Language

- **C#** Unity Editor scripts
- No external dependencies — uses only Unity Editor APIs
- Editor-only assembly (excluded from player builds via `.asmdef`)

### Unity API Surface

| API | Purpose |
|---|---|
| `AssetDatabase` | Asset discovery, GUID resolution, asset loading |
| `Material` | Read shader properties (colors, textures, floats) directly |
| `GameObject` / `Transform` | Scene and prefab hierarchy traversal, component access |
| `PrefabUtility` | Prefab loading, override inspection, nested prefab detection |
| `ModelImporter` | FBX import settings, node name extraction |
| `EditorSceneManager` | Scene loading for conversion |
| `EditorUtility` | Progress bar, folder dialog, confirmation dialogs |

---

## Conversion Pipeline

### High-Level Flow

```
User right-clicks folder → "Export to Godot"
    │
    ▼
[1. Discover all assets inside the selected folder]
    │
    ▼
[2. Classify assets by type: scene, prefab, material, texture, FBX, other]
    │
    ▼
[3. Pick output folder (EditorUtility.SaveFolderPanel)]
    │
    ▼
[4. Convert each asset type (order matters)]
    │   a. Textures → copy to output
    │   b. FBX → copy to output (Godot imports natively)
    │   c. Materials → convert to .tres files
    │   d. Prefabs → convert to .tscn files
    │   e. Scenes → convert to .tscn files
    │
    ▼
[5. Generate project.godot + folder structure]
    │
    ▼
[6. Produce skip report → log to Unity Console]
```

### Execution Model

Runs synchronously on the main thread. `EditorUtility.DisplayCancelableProgressBar()` is called between assets to show progress and support user cancellation. `EditorUtility.ClearProgressBar()` is called on completion, cancellation, or fatal error.

---

## 1. Unity Editor Integration

### Context Menu

Register via `[MenuItem("Assets/Export to Godot")]` with a validation method that only enables the menu item when a folder is selected in the Project window.

### User Flow

1. User right-clicks a folder in the Project window
2. Selects "Export to Godot"
3. `EditorUtility.SaveFolderPanel()` opens — user picks the output directory. If the user cancels, export aborts immediately.
4. If the chosen output directory already contains files (including an existing `project.godot`), prompt for confirmation before writing anything. If the user declines, export aborts with no output written.
5. If the user has unsaved scene changes, prompt to save via `EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()`. If the user cancels this prompt, export aborts with no output written.
6. Dependency resolution runs (see section 2)
7. Conversion runs with progress bar
8. On completion, a summary is logged to the Unity Console
9. `EditorUtility.RevealInFinder()` opens the output folder

### Progress Reporting

- `EditorUtility.DisplayCancelableProgressBar(title, info, progress)` is called between each asset
- If the user clicks Cancel, conversion stops after the current asset and partial output is preserved
- All messages (`[INFO]`, `[WARN]`, `[ERROR]`) are logged via `Debug.Log`, `Debug.LogWarning`, `Debug.LogError`

---

## 2. Asset Discovery

Only assets physically inside the selected folder are exported. External dependencies (textures, materials, models referenced by scenes/prefabs but located outside the folder) are **not** pulled in. This avoids exporting Unity-specific assets that Godot cannot use (built-in shaders, default resources, package assets, etc.). The tradeoff is that `ExtResource` references to assets outside the folder will be unresolved in Godot — the user must place those assets manually or ensure all needed assets are inside the selected folder.

### Process

**Phase 1 — Discover assets:** Use `AssetDatabase.FindAssets("", new[] { selectedFolderPath })` to enumerate all assets under the selected folder recursively.

**Phase 2 — Filter:** Remove assets that should not be exported:
- Paths starting with `Packages/` (Unity built-in packages)
- Script files (`.cs`)
- Editor-only assets, defined in V1 as any asset whose path contains an `Editor/` path segment (for example `Assets/Tools/Editor/Icon.png`)

**Phase 3 — Classify:** For each remaining path, determine asset type via file extension:
- `.unity` → scene
- `.prefab` → prefab
- `.mat` → material
- `.png`, `.jpg`, `.jpeg`, `.webp`, `.bmp`, `.tga`, `.psd`, `.exr` → texture
- `.fbx` → FBX model
- Everything else → other (skipped, added to skip report)

### Edge Cases

- **Assets outside the selected folder:** NOT included. If a scene references `Assets/SharedMaterials/Glass.mat` but that path is outside the selected folder, the material is not exported. The `.tscn` file will contain an `ExtResource` reference to `res://SharedMaterials/Glass.tres` which will be unresolved until the user exports or places that material manually.
- **Prefab-only folders:** If the selected folder contains no `.unity` scene files (only prefabs, textures, models, etc.), the output is still valid — `project.godot` is generated normally, but no scene `.tscn` files are produced.
- **Multi-scene setups:** Unity additive/multi-scene setups are converted as independent scenes. Each `.unity` file becomes its own `.tscn` with no cross-scene references.

---

## 3. Texture Handling

### Copy

1. **Supported by Godot natively:** PNG, JPG, WebP, BMP, TGA → **copy as-is**
2. **Not supported:** PSD, EXR → **copy as-is with a warning** that Godot cannot import these formats. User must convert manually. Transcoding support planned for a future version.

### Source Paths

Textures are copied from their project path (as returned by `AssetDatabase.GetAssetPath()`). The source file is the original asset on disk.

### Import Settings

No `.import` files are generated. Godot auto-generates these on first project open. Texture import settings from Unity (normal map detection, filter/wrap modes, sRGB) are not carried over in V1 — the user must configure these manually in Godot. Automatic import setting mapping is planned for a future version.

---

## 4. FBX Handling

### Approach: Keep FBX, Let Godot Import

FBX files are **copied directly** to the Godot project, preserving the original Unity folder structure (minus the `Assets/` prefix). Godot 4.6.1 natively imports FBX files.

### Supported Model Sources

V1 supports **only** meshes whose source asset path ends with `.fbx`.

- During scene/prefab conversion, a `MeshFilter.sharedMesh` is considered exportable only if `AssetDatabase.GetAssetPath(meshFilter.sharedMesh)` resolves to a `.fbx` asset path.
- Non-FBX mesh sources are **not** converted in V1. This includes imported `.obj` / `.blend` files, Unity primitive meshes, and generated mesh sub-assets with no `.fbx` source path.
- When a non-FBX mesh source is encountered, create an empty `Node3D` placeholder at the correct transform, log a warning that includes the mesh name and source path if available, and add the asset to the skip report under an `Unsupported Mesh Sources` category.

### Node Name Extraction

The converter loads each FBX as a `GameObject` via `AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath)` and traverses its `Transform` hierarchy to read node names. These names are used to construct material override paths in `.tscn` files.

### FBX Import Scale Compensation

Unity and Godot apply different scale transformations when importing the same FBX file (due to differing handling of `UnitScaleFactor`, `Convert Units`, and `Use File Scale` settings). To ensure models appear at the correct size in Godot:

- **FBX-backed prefabs:** The `PrefabExporter` reads the instantiated prefab root's `localPosition`, `localRotation`, and `localScale` (which includes Unity's FBX import scale from `useFileUnits` conversion) and writes it as the root `Transform3D` on the FBX instance node. This overrides Godot's FBX import root transform with Unity's.
- **FBX instances in scenes:** The `NodeConverter` already writes the scene-level transform from `go.transform`, which includes the FBX import scale. No additional compensation needed.
- **Prefab instances in scenes:** The scene-level transform written by `ConvertPrefabInstance` includes the FBX import scale (inherited by the prefab variant from the FBX model). This overrides the prefab `.tscn`'s root transform.

### Scene References to FBX

When a Unity scene instances a mesh from an FBX file, the Godot scene will use `instance = ExtResource(...)` to load the entire FBX as a sub-scene at the correct transform. Individual mesh selection within the FBX is not performed — the whole model is instanced.

### Material Overrides on FBX Instances

When a Unity `MeshRenderer` assigns materials to a mesh from an FBX, the converter applies those materials as overrides on the instanced FBX's child nodes:

1. Load the FBX as a `GameObject` and traverse its hierarchy to extract node names
2. Map the Unity `MeshRenderer`'s target to the corresponding node name in the FBX
3. Write child node overrides in the `.tscn` using that name as the path:

```ini
[node name="Building" parent="." instance=ExtResource("1")]

[node name="Wall" parent="Building"]
surface_material_override/0 = ExtResource("2")
```

This works when Godot's import produces the same node names as Unity's import (~80-90% of cases). It can fail if Godot renames nodes (e.g., duplicate name suffixes, sanitization) or creates a different hierarchy structure. Failed overrides are logged as warnings.

### Multiple Sub-Object References

Unity references individual meshes inside an FBX via `MeshFilter.sharedMesh`. There are two usage patterns:

1. **FBX placed via PrefabInstance** (most common) — the scene has a `PrefabInstance` component pointing to the FBX. Handled by prefab conversion — the whole model is instanced, which is correct.

2. **Individual meshes placed directly** (uncommon) — the scene has bare `MeshFilter` + `MeshRenderer` GameObjects, each referencing a different sub-mesh from the same FBX. Instancing the whole FBX at each location produces visual duplicates.

**V1 behavior:**

- During scene conversion, track all mesh references to FBX files. Use `AssetDatabase.GetAssetPath(meshFilter.sharedMesh)` to identify the source FBX, and `meshFilter.sharedMesh.name` to identify the specific sub-mesh.
- **Single unique mesh per FBX** (or single-mesh FBX): instance the whole FBX normally. This covers the vast majority of cases.
- **Multiple different meshes from the same FBX**: instance the whole FBX at each location anyway, but log a **prominent warning** listing the FBX file and which sub-meshes were referenced by name, so the user knows to fix it manually in Godot.

---

## 5. Material Conversion

### Reading Material Properties

Materials are loaded via `AssetDatabase.LoadAssetAtPath<Material>(path)` and their properties are read directly through the `Material` API:

- **Shader detection:** `material.shader.name` determines which mapping table to use
- **Colors:** `material.GetColor("_BaseColor")`
- **Floats:** `material.GetFloat("_Metallic")`, `material.GetFloat("_Smoothness")`
- **Textures:** `material.GetTexture("_BaseMap")` → resolve path via `AssetDatabase.GetAssetPath(texture)`
- **Tiling/offset:** `material.GetTextureScale("_BaseMap")`, `material.GetTextureOffset("_BaseMap")`

### Output Naming

Unity `.mat` files are converted to Godot `.tres` files using the original material filename with the extension changed. The original folder structure is preserved (via the `Assets/` prefix stripping rule in section 8), which naturally avoids name collisions between materials with the same name in different folders:

```
Assets/Environment/Materials/Glass.mat  →  Environment/Materials/Glass.tres
Assets/Characters/Materials/Glass.mat   →  Characters/Materials/Glass.tres
```

### Shader Scope

Five shader families are supported:

| Unity Shader | Godot Material | Notes |
|---|---|---|
| `Universal Render Pipeline/Lit` | `StandardMaterial3D` | Full PBR mapping |
| `Universal Render Pipeline/Unlit` | `StandardMaterial3D` (unshaded) | `shading_mode = SHADING_MODE_UNSHADED` |
| `Standard` (Built-in) | `StandardMaterial3D` | Legacy Built-in pipeline, full PBR |
| Legacy Built-in shaders | `StandardMaterial3D` | Diffuse/Specular/Bumped/Transparent families + Mobile variants |
| Legacy Unlit shaders | `StandardMaterial3D` (unshaded) | `Unlit/*` family — color, texture, transparency |

The following legacy built-in and unlit shader names are recognized:

- `Unlit/Texture`
- `Unlit/Color`
- `Unlit/Transparent`
- `Unlit/Transparent Cutout`
- `Legacy Shaders/Diffuse`
- `Legacy Shaders/Specular`
- `Legacy Shaders/Bumped Diffuse`
- `Legacy Shaders/Bumped Specular`
- `Legacy Shaders/Transparent/Diffuse`
- `Legacy Shaders/Transparent/Specular`
- `Legacy Shaders/Transparent/Bumped Diffuse`
- `Legacy Shaders/Transparent/Bumped Specular`
- `Mobile/Diffuse`
- `Mobile/Bumped Diffuse`
- `Mobile/Bumped Specular`
- `Mobile/Unlit (Supports Lightmap)`

Unknown/unsupported shaders → create a default white `StandardMaterial3D` + log a warning.

### PBR Texture Strategy

V1 always emits `StandardMaterial3D`, never `ORMMaterial3D`.

- This is a deliberate simplification even though Godot's `StandardMaterial3D` expects separate ambient-occlusion, roughness, and metallic textures.
- Unity metallic workflows commonly pack metallic into one channel and smoothness into the alpha channel of the same texture.
- V1 does **not** split channels, invert texture data, or repack textures.
- Therefore, V1 exports the scalar metallic/roughness values and any directly reusable texture references, while logging a warning when packed metallic-smoothness data is detected because the visual result will only be approximate.

### Property Mapping: URP/Lit → StandardMaterial3D

| Unity Property | Godot Property | Conversion |
|---|---|---|
| `_BaseColor` | `albedo_color` | Direct RGBA mapping |
| `_BaseMap` | `albedo_texture` | Texture reference via path |
| `_Metallic` | `metallic` | Direct float |
| `_MetallicGlossMap` | `metallic_texture` + `metallic_texture_channel` | Texture reference, `TEXTURE_CHANNEL_RED` |
| `_Smoothness` | `roughness` | `roughness = 1.0 - smoothness` |
| `_BumpMap` | `normal_enabled` + `normal_texture` | Set `normal_enabled = true`, texture reference |
| `_BumpScale` | `normal_scale` | Direct float |
| `_EmissionColor` | `emission_enabled` + `emission` + `emission_energy_multiplier` | Set `emission_enabled = true`, Color → emission, brightness → multiplier |
| `_EmissionMap` | `emission_texture` | Texture reference |
| `_OcclusionMap` | `ao_enabled` + `ao_texture` + `ao_texture_channel` | Set `ao_enabled = true`, texture reference, `TEXTURE_CHANNEL_GREEN` |
| `_OcclusionStrength` | `ao_light_affect` | Direct float |
| `_Cutoff` | `alpha_scissor_threshold` | Alpha cutoff value, only when using alpha scissor |
| `_Surface` (0/1) | `transparency` | 0 = `TRANSPARENCY_DISABLED`; 1 = `TRANSPARENCY_ALPHA`, unless `_Cutoff > 0` in which case use `TRANSPARENCY_ALPHA_SCISSOR` |
| `_Cull` (0/1/2) | `cull_mode` | 0 = off, 1 = front, 2 = back |
| `_BaseMap` tiling/offset | `uv1_scale` + `uv1_offset` | `material.GetTextureScale` / `GetTextureOffset` |

Additional V1 rules for URP/Lit:

- If `_MetallicGlossMap` is present, write `metallic_texture` and `metallic_texture_channel = TEXTURE_CHANNEL_RED`.
- V1 does **not** write `roughness_texture`; roughness comes from the scalar smoothness inversion only.
- If `_EmissionMap` is present but `_EmissionColor` is black, still enable emission and write the texture.
- If `_OcclusionMap` is present, write `ao_enabled = true`, `ao_texture`, and `ao_texture_channel = TEXTURE_CHANNEL_GREEN`.

### Property Mapping: Legacy Standard → StandardMaterial3D

| Unity Property | Godot Property | Conversion |
|---|---|---|
| `_Color` | `albedo_color` | Direct RGBA |
| `_MainTex` | `albedo_texture` | Texture reference |
| `_MetallicGlossMap` | `metallic_texture` + `metallic_texture_channel` | Texture reference, `TEXTURE_CHANNEL_RED` |
| `_Glossiness` | `roughness` | `roughness = 1.0 - glossiness` |
| `_BumpMap` | `normal_enabled` + `normal_texture` | Set `normal_enabled = true`, texture reference |
| `_EmissionColor` | `emission_enabled` + `emission` + `emission_energy_multiplier` | Set `emission_enabled = true`, color mapping, default multiplier `1.0` |
| `_EmissionMap` | `emission_texture` | Texture reference |
| `_OcclusionMap` | `ao_enabled` + `ao_texture` + `ao_texture_channel` | Set `ao_enabled = true`, texture reference, `TEXTURE_CHANNEL_GREEN` |

Additional V1 rules for legacy Standard:

- If `_MetallicGlossMap` is present, write `metallic_texture` and `metallic_texture_channel = TEXTURE_CHANNEL_RED`.
- V1 does **not** write `roughness_texture`; roughness comes from the scalar glossiness inversion only.
- If `_OcclusionMap` is present, write `ao_enabled = true`, `ao_texture`, and `ao_texture_channel = TEXTURE_CHANNEL_GREEN`.

### Property Mapping: Legacy Built-in Shaders → StandardMaterial3D

These shaders predate the Standard shader and have a smaller property set. The exporter reads whichever properties exist on the material.

| Unity Property | Godot Property | Conversion | Present on |
|---|---|---|---|
| `_Color` | `albedo_color` | Direct RGBA | All |
| `_MainTex` | `albedo_texture` | Texture reference | All |
| `_Shininess` | `roughness` | `roughness = 1.0 - shininess` | Specular, Bumped Specular |
| `_BumpMap` | `normal_enabled` + `normal_texture` | Set `normal_enabled = true`, texture reference | Bumped Diffuse, Bumped Specular |
| `_EmissionColor` | `emission_enabled` + `emission` + `emission_energy_multiplier` | Same as URP/Lit emission logic | If present on material |
| `_EmissionMap` | `emission_texture` | Texture reference | If present on material |
| (Transparent variants) | `transparency` | `TRANSPARENCY_ALPHA` (= 1) | Transparent/* shader names |
| `_MainTex` tiling/offset | `uv1_scale` + `uv1_offset` | `material.GetTextureScale` / `GetTextureOffset` | All |

Additional V1 rules for legacy built-in shaders:

- `Mobile/Unlit (Supports Lightmap)` is mapped identically to `Mobile/Diffuse` — it uses the same `_MainTex`/`_Color` properties. The lightmap-specific features are not converted.
- `_SpecColor` is present on Specular variants but is not mapped — Godot's `StandardMaterial3D` has no direct specular color equivalent.
- Properties are read via `HasProperty` checks, so a single code path handles all legacy built-in variants.

### Property Mapping: Legacy Unlit Shaders → StandardMaterial3D

All `Unlit/*` shaders set `shading_mode = SHADING_MODE_UNSHADED` (= 0).

| Unity Property | Godot Property | Conversion | Present on |
|---|---|---|---|
| `_Color` | `albedo_color` | Direct RGBA | All |
| `_MainTex` | `albedo_texture` | Texture reference | All |
| `_Cutoff` | `transparency` + `alpha_scissor_threshold` | `TRANSPARENCY_ALPHA_SCISSOR` if cutoff > 0, else `TRANSPARENCY_ALPHA` | `Unlit/Transparent Cutout` |
| (Transparent variants) | `transparency` | `TRANSPARENCY_ALPHA` (= 1) | `Unlit/Transparent` |
| `_MainTex` tiling/offset | `uv1_scale` + `uv1_offset` | `material.GetTextureScale` / `GetTextureOffset` | All |

### Output Format: .tres

```ini
[gd_resource type="StandardMaterial3D" format=3]

[ext_resource type="Texture2D" path="res://Textures/brick_albedo.png" id="1"]
[ext_resource type="Texture2D" path="res://Textures/brick_normal.png" id="2"]

[resource]
albedo_color = Color(1, 1, 1, 1)
albedo_texture = ExtResource("1")
metallic = 0.5
roughness = 0.5
normal_enabled = true
normal_texture = ExtResource("2")
```

---

## 6. Scene Conversion

### Unity Scene → Godot .tscn

Each `.unity` scene file found in the selected folder is converted to a `.tscn` file.

### Loading Scenes

Before exporting any scenes, capture the full current editor scene setup via `EditorSceneManager.GetSceneManagerSetup()` and remember the active scene.

Scenes are loaded via `EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive)`. Before starting export, the user's currently open scenes are saved if they have unsaved changes (prompted via `EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()`).

After conversion completes, is canceled by the user, or fails, restore the exact prior editor scene setup via `EditorSceneManager.RestoreSceneManagerSetup()`, including:

- all scenes that were open before export
- which of those scenes were loaded additively
- which scene was active

If the user cancels the save prompt before export starts, no scenes are opened and no output is written.

### Scene Root Node

Every exported Unity scene produces a single synthetic Godot root node:

- Node type: `Node3D`
- Node name: the Unity scene filename without extension
- Transform: identity, omitted from output

All Unity root `GameObject`s in the scene become children of this synthetic root, in their existing hierarchy order. This guarantees a valid Godot scene even when the Unity scene contains multiple root objects.

### Hierarchy Traversal

1. Get root objects via `scene.GetRootGameObjects()`
2. Recursively walk children via `Transform.childCount` / `Transform.GetChild(i)`
3. For each `GameObject`, determine its Godot node type by checking components:
   - Has `MeshFilter` + `MeshRenderer` whose mesh source path ends with `.fbx` → instance the referenced FBX as `ExtResource`
   - Has `MeshFilter` + `MeshRenderer` with any other mesh source → create `Node3D` placeholder + warning
   - Has `Light` component → `DirectionalLight3D`, `OmniLight3D`, or `SpotLight3D`
   - Has `Camera` component → `Camera3D`
   - Otherwise (empty/grouping) → `Node3D`
4. **Inactive GameObjects** (`GameObject.activeSelf == false`) are exported with `visible = false` on the Godot node
5. Disabled components are preserved explicitly:
   - Disabled `MeshRenderer` → export the node, but write `visible = false`
   - Disabled `Light` → export the light node, but write `visible = false`
   - Disabled `Camera` → export the camera node, but write `visible = false` and `current = false`
6. Apply coordinate system conversion to all transforms
7. Resolve material references: iterate over `MeshRenderer.sharedMaterials` array — each element at index `N` maps to `surface_material_override/N` in the Godot node. Resolve each material path via `AssetDatabase.GetAssetPath()` → ExtResource refs to converted .tres files.
8. For FBX instances, apply material overrides as child node overrides using node names from the loaded FBX hierarchy (see section 4, "Material Overrides on FBX Instances")
9. Calculate `load_steps` as the total count of `[ext_resource]` + `[sub_resource]` entries in the file
10. Write the .tscn file

### Duplicate Node Names

Unity allows sibling GameObjects with identical names. Godot does not — sibling node names must be unique. When building the Godot node tree, track sibling names at each hierarchy level. If a name already exists, append a numeric suffix:

```
Chair  →  Chair
Chair  →  Chair_2
Chair  →  Chair_3
```

Note: renaming affects only the emitted Godot node name. Override targeting still works through the source-object mapping described below.

### Override Target Resolution

To avoid ambiguity when duplicate names are renamed for Godot output, overrides are resolved by recorded source-object identity, not by re-searching for node names later.

- As each scene or prefab node is emitted, record a mapping from the source Unity object (`GameObject`, `Transform`, or component target as applicable) to the final Godot node path that was written.
- Prefab transform overrides and material overrides must use this recorded mapping to find the correct emitted node.
- If an override target does not exist in the recorded mapping, log a warning and skip that override.

This makes override behavior deterministic even when Unity sibling names had to be uniquified for Godot.

### Transform Conversion

Unity is **left-handed** (Y-up, Z-forward). Godot is **right-handed** (Y-up, -Z-forward). Unity stores transforms as separate position (`Vector3`), rotation (`Quaternion`), and scale (`Vector3`). Godot serializes transforms as a `Transform3D`: a 3x3 basis matrix (rotation + scale) followed by the origin (position).

Conversion is applied to **scene-level transforms only** (FBX files are handled by Godot's importer). Each node's transform is **local** (relative to parent), same as Unity — convert each local transform independently.

The full conversion algorithm, in order:

**Step 1: Read Unity transform values and apply handedness conversion**

```
position.x =  transform.localPosition.x
position.y =  transform.localPosition.y
position.z = -transform.localPosition.z

quat.x = -transform.localRotation.x
quat.y = -transform.localRotation.y
quat.z =  transform.localRotation.z
quat.w =  transform.localRotation.w

scale = transform.localScale  (unchanged — scale is handedness-independent)
```

**Step 2: Convert the handedness-corrected quaternion to a 3x3 rotation matrix**

Given quaternion `(x, y, z, w)` after step 1:

```
        | 1-2(y²+z²)   2(xy-wz)    2(xz+wy)  |
R   =   | 2(xy+wz)     1-2(x²+z²)  2(yz-wx)  |
        | 2(xz-wy)     2(yz+wx)    1-2(x²+y²) |
```

**Step 3: Apply scale to the rotation matrix to produce the basis**

Multiply each **column** of R by the corresponding scale component:

```
basis[row][0] = R[row][0] * scale.x    (for all 3 rows)
basis[row][1] = R[row][1] * scale.y    (for all 3 rows)
basis[row][2] = R[row][2] * scale.z    (for all 3 rows)
```

**Step 4: Serialize as Transform3D**

Godot's `.tscn` format serializes row by row, then origin:

```
Transform3D(
    basis[0][0], basis[0][1], basis[0][2],
    basis[1][0], basis[1][1], basis[1][2],
    basis[2][0], basis[2][1], basis[2][2],
    position.x,  position.y,  position.z
)
```

**Step 5: Omit identity transforms**

If the result equals the identity (`Transform3D(1,0,0, 0,1,0, 0,0,1, 0,0,0)`), omit the `transform` property entirely. This keeps `.tscn` files clean.

### Node Type Mapping

| Unity Component | Godot Node | Property Mapping |
|---|---|---|
| GameObject (empty) | `Node3D` | name, transform |
| MeshFilter + MeshRenderer with `.fbx` source | `Node3D` + instanced FBX scene | transform, material overrides |
| MeshFilter + MeshRenderer with non-`.fbx` source | `Node3D` placeholder | transform only, warning |
| Light (Directional) | `DirectionalLight3D` | color, intensity, shadows |
| Light (Point) | `OmniLight3D` | color, intensity, range, shadows |
| Light (Spot) | `SpotLight3D` | color, intensity, range, angle, shadows |
| Camera | `Camera3D` | fov, near, far, projection |

### Light Conversion Details

| Unity Property | Godot Property | Conversion |
|---|---|---|
| `light.color` | `light_color` | Direct RGB |
| `light.intensity` | `light_energy` | Direct float (may need scaling factor) |
| `light.range` | `omni_range` / `spot_range` | Direct float |
| `light.spotAngle` | `spot_angle` | `godot_angle = unity_angle / 2.0` |
| `light.shadows` | `shadow_enabled` | `None` = no shadows, `Hard`/`Soft` = shadows on |

### Camera Conversion Details

| Unity Property | Godot Property | Conversion |
|---|---|---|
| `camera.fieldOfView` | `fov` | Direct (both vertical FOV in degrees) |
| `camera.nearClipPlane` | `near` | Direct float |
| `camera.farClipPlane` | `far` | Direct float |
| `camera.orthographic` | `projection` | false = perspective, true = orthogonal |
| `camera.orthographicSize` | `size` | Direct float |

### .tscn Output Format

`ext_resource` IDs are assigned as sequential integers starting from `"1"`, in the order resources are first referenced. The same convention applies to `.tres` files.

### Deterministic Serialization Rules

To keep generated files valid and diff-friendly, V1 serialization follows these rules:

- All emitted Godot resource paths use forward slashes and `res://` prefixes, even on Windows.
- String values in `.tscn`, `.tres`, and `project.godot` are always double-quoted.
- Any embedded double quotes or backslashes inside serialized strings are escaped.
- Floating-point values are serialized with `CultureInfo.InvariantCulture` so the decimal separator is always `.`.
- Asset lists discovered from sets or hash-based collections must be sorted lexicographically before export so file ordering is deterministic.
- Within scenes and prefabs, hierarchy traversal preserves Unity sibling order.

```ini
[gd_scene load_steps=3 format=3]

[ext_resource type="PackedScene" path="res://Models/building.fbx" id="1"]
[ext_resource type="Material" path="res://Materials/brick.tres" id="2"]

[node name="MainLevel" type="Node3D"]

[node name="Building" parent="." instance=ExtResource("1")]
transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 5, 0, -3)

[node name="GroupNode" type="Node3D" parent="."]

[node name="Sun" type="DirectionalLight3D" parent="."]
light_color = Color(1, 0.95, 0.85, 1)
light_energy = 1.5
shadow_enabled = true
```

---

## 7. Prefab Conversion

### Approach: Prefabs → Godot Scenes (.tscn)

Each Unity `.prefab` file is converted to its own `.tscn` file. Scenes that instance prefabs use `instance = ExtResource(...)` to reference the prefab's `.tscn`.

### Loading Prefabs

Prefabs are loaded via `AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath)`, which returns the prefab root as a fully resolved `GameObject`. The hierarchy is traversed identically to scene conversion.

### Prefab Instancing in Scenes

When a scene contains a `PrefabInstance`:

1. Identify the source prefab via `PrefabUtility.GetCorrespondingObjectFromSource()`
2. Resolve its path via `AssetDatabase.GetAssetPath()` to find the corresponding `.tscn`
3. Create an instanced node: `instance=ExtResource("<id>")`
4. Apply overrides (see below)

If a prefab instance refers to a missing or unsupported prefab source, create an empty `Node3D` placeholder at the correct transform and log a warning.

### Override Support (V1 Scope)

V1 supports the two most common override types, read via `PrefabUtility.GetPropertyModifications()`:

**Transform overrides:**
- `m_LocalPosition`, `m_LocalRotation`, `m_LocalScale` on any target within the prefab
- Applied by setting the `transform` property on the instanced node or its children

**Material overrides:**
- `m_Materials.Array.data[N]` changes on MeshRenderer targets
- Applied by setting `surface_material_override/N` on the relevant mesh node

All other override types are **logged as warnings** and skipped.

### Nested Prefabs

- **One level deep:** If a prefab references another prefab, the inner prefab reference is **flattened** (its hierarchy is baked directly into the outer prefab's .tscn)
- Deeper nesting is detected via `PrefabUtility.GetCorrespondingObjectFromSource()` and logged as a warning
- This avoids recursive override resolution while handling the majority of real-world cases

### Disabled Prefab Components

The same inactive/disabled rules used for scene conversion apply during prefab conversion:

- Inactive prefab `GameObject` → `visible = false`
- Disabled `MeshRenderer` / `Light` → export node with `visible = false`
- Disabled `Camera` → export node with `visible = false` and `current = false`

---

## 8. Godot Project Generation

### Output Structure

```
output_folder/
├── project.godot
├── Scenes/
│   └── MainLevel.tscn
├── Models/
│   └── building.fbx
├── Textures/
│   ├── brick_albedo.png
│   └── brick_normal.png
├── Materials/
│   └── brick.tres
└── Prefabs/
    └── Lamp.tscn
```

### Folder Mapping

Unity paths are mapped to Godot paths by stripping the `Assets/` prefix:

```
Assets/Models/building.fbx  →  Models/building.fbx
Assets/Textures/brick.png   →  Textures/brick.png
Assets/Scenes/Main.unity    →  Scenes/Main.tscn
Assets/Prefabs/Lamp.prefab  →  Prefabs/Lamp.tscn
```

### Special Characters in Names

Unity allows spaces, parentheses, unicode, and other special characters in file and folder names. These are preserved as-is in the output — Godot supports them.

When writing references in `.tscn`/`.tres` files:

- always quote paths with double quotes
- always use forward slashes in `res://` paths
- escape embedded `"` and `\` characters if they appear in names

Example: `path="res://Models/Old House (1)/building.fbx"`

### project.godot

A minimal but valid `project.godot` file:

```ini
; Engine configuration file.
; It's best edited using the editor, so don't edit it unless you know what you're doing.

config_version=5

[application]
config/name="<selected folder name>"
config/features=PackedStringArray("4.6")

[rendering]
renderer/rendering_method="forward_plus"
```

---

## 9. Error Handling

### Philosophy: Best-Effort with Warnings

The converter **never aborts** due to a single bad asset. Every asset is processed independently.

### Error Categories

| Severity | Behavior | Examples |
|---|---|---|
| **INFO** | Normal progress | "Converting texture brick.png", "Found 142 assets" |
| **WARN** | Asset partially converted or skipped | Unknown shader, missing texture reference, unsupported override type, unsupported mesh source, nested prefab depth > 1 |
| **ERROR** | Asset failed to convert entirely | Corrupt FBX file, failed to load scene, I/O failure on a specific file |
| **FATAL** | Conversion cannot continue | Output directory not writable, out of disk space |

### Missing References

When a reference cannot be resolved:

- **Texture ref in material:** Skip the texture, use default value, log warning
- **Material ref in scene:** Use default material, log warning
- **Mesh/FBX ref in scene:** Create an empty Node3D placeholder, log warning
- **Prefab ref in scene:** Create an empty Node3D placeholder, log warning

### Skip Report

After conversion completes, a categorized summary is logged to the Unity Console:

```
=== Skip Report ===
Skipped asset types (not supported in V1):
  C# Scripts:           4 files
  Particle Systems:     2 files
  Animator Controllers: 1 file
  Custom Shaders:       1 file
  Unsupported Mesh Src: 2 files
  Audio Clips:          3 files
  
  Details:
    Scripts/PlayerController.cs
    Scripts/GameManager.cs
    ...
```

---

## 10. Project Structure

### File Layout

```
Assets/Editor/U2GExporter/
├── U2GExporter.asmdef              # Editor-only assembly definition
├── ExportMenu.cs                   # Context menu entry point, orchestration
├── DependencyResolver.cs           # Asset discovery and dependency collection
├── TextureExporter.cs              # Texture copy logic
├── FbxExporter.cs                  # FBX copy + node name extraction
├── MaterialExporter.cs             # Material → .tres conversion
├── SceneExporter.cs                # Scene → .tscn conversion
├── PrefabExporter.cs               # Prefab → .tscn conversion
├── LightExporter.cs                # Light component → Godot light node data
├── CameraExporter.cs               # Camera component → Godot camera node data
├── ProjectWriter.cs                # project.godot + folder structure generation
├── CoordConvert.cs                 # Coordinate system conversion math
├── TscnWriter.cs                   # .tscn file format serializer
├── TresWriter.cs                   # .tres file format serializer
└── SkipReport.cs                   # Skip report generation
```

### Assembly Definition

The `.asmdef` file must be configured with:
- **Platform:** Editor only (unchecked "Any Platform", checked "Editor" only)
- This ensures the exporter code is excluded from player builds

---

## 11. Out of Scope (V1)

The following are explicitly **not supported** in V1 and will be logged in the skip report:

- C# scripts and MonoBehaviour components
- Custom shaders (beyond URP Lit/Unlit and legacy Standard)
- Skinned meshes / SkinnedMeshRenderer
- Animations / Animator / AnimationClip
- Audio clips and AudioSource
- Particle systems
- UI Canvas / UGUI elements
- Terrain data
- NavMesh data
- Physics materials (beyond what colliders use)
- ScriptableObjects
- Lightmap data (baked lighting)
- Reflection probes
- Video clips
- Sprite / 2D assets
- Imported `.obj` / `.blend` model sources
- Nested prefabs deeper than 1 level
- Prefab variants
- Batch mode / CLI export
- Persistent EditorWindow or export settings

---

## 12. Known Limitations & Risks

1. **FBX instancing granularity:** Since we instance entire FBX files rather than individual meshes, a Unity scene that uses 3 different meshes from the same FBX will create 3 instances of the full model. This may produce visual duplicates if the FBX contains multiple objects. Mitigation: log a warning when this is detected.

2. **Material overrides on FBX instances:** The converter extracts node names from the FBX via Unity's importer and constructs override paths assuming Godot's importer produces matching names. This works in ~80-90% of cases. It can fail if Godot renames nodes (duplicate suffixes, sanitization) or restructures the hierarchy. Mitigation: best-effort path matching, warning on failure. Failed overrides result in the FBX's embedded materials being used instead.

3. **Smoothness → Roughness inversion:** Unity stores smoothness in the alpha channel of the metallic map texture. Godot `StandardMaterial3D` uses a separate roughness texture. V1 will keep the scalar inversion (`roughness = 1 - smoothness`) and may reuse the metallic texture's red channel for metallic, but it will **not** extract/invert the alpha channel into a standalone roughness texture. Packed metallic-smoothness textures will produce a warning and only approximate the Unity result.

4. **Light intensity:** Unity and Godot use different light intensity units. Unity URP uses physical units (lumens/lux) while Godot uses an arbitrary energy multiplier. Direct value copy may produce too-bright or too-dim lighting. Mitigation: copy value as-is, document that manual adjustment may be needed.

5. **Unsupported texture formats:** PSD and EXR files are copied as-is but Godot cannot import them. The user must manually convert these to PNG/JPG. Automatic transcoding is planned for a future version.

6. **FBX import-time root transforms:** Unity and Godot may apply different corrections when importing the same FBX file (e.g., axis rotation, scale factor), since FBX files can be authored in various coordinate systems (Z-up, Y-up) and unit scales. This can cause models to appear rotated or scaled differently compared to Unity, even though our scene-level coordinate conversion is correct. The converter cannot control Godot's FBX import behavior. Mitigation: the user manually adjusts affected models in Godot.

7. **Scene loading during export:** Opening scenes for conversion temporarily changes the active scene setup in the Editor. V1 mitigates this by capturing the full scene-manager setup before export and restoring it afterward, but the editor may still briefly switch scenes while export is running.

8. **Editor responsiveness:** Export runs synchronously on the main thread, which may cause the Editor to appear unresponsive during large exports. Mitigation: progress bar provides feedback and supports cancellation.

---

## 13. Future Versions (Not In Scope — Reference Only)

- V1.x: Texture transcoding (PSD/EXR → PNG) via `Texture2D.EncodeToPNG()`
- V1.x: Texture import settings mapping (normal map detection, filter/wrap modes, sRGB) via .import files
- V1.x: Batch mode / CLI support for automated exports
- V1.x: Persistent EditorWindow with settings (default output folder, export presets)
- V1.x/V2: Imported `.obj` and `.blend` model source support
- V2: Skinned meshes, animations, blend shapes
- V3: Audio import, particle system basic conversion
- V4: C# → GDScript transpilation (limited subset)
- V5: Custom shader → Godot shader conversion
