# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**u2g-exporter** (Unity to Godot Exporter) is a Unity Editor tool that exports selected assets from a Unity project into a ready-to-open Godot 4.6.1 project. The user right-clicks a folder in the Project window, selects "Export to Godot", and gets a complete Godot project. V1 scope covers static meshes (FBX), textures, materials (URP Lit/Unlit + legacy Standard), scenes, and prefabs. See [SPEC.md](SPEC.md) for the complete specification — it is the authoritative source for all conversion logic. If AGENTS.md and the spec disagree, follow SPEC.md.

**Unity version:** Unity 6 (6000.3.12f1), C# 9 / .NET Standard 2.1
**Target Godot version:** 4.6.1

## Development Environment

This is a Unity Editor extension with no external build tooling. There are no CLI build commands, test runners, or linting scripts — all development and testing happens inside the Unity Editor.

- **To test:** Open the Unity project (repo root), right-click `Assets/Test` in the Project window, select "Export to Godot", and pick an output directory. Verify the output in Godot 4.6.x.
- **To verify compilation:** Open the project in Unity; the Editor-only assembly compiles automatically. Check the Console window for errors.
- **Test assets:** `Assets/Test/` contains sample content (monkey.fbx, monkey.prefab, Red.mat, SampleScene.unity) used for manual export validation.

## Architecture

The tool is a set of C# Editor scripts with no external dependencies — it uses only Unity Editor APIs (`AssetDatabase`, `Material`, `PrefabUtility`, `EditorSceneManager`, etc.).

### Conversion Pipeline

```
Right-click folder → "Export to Godot"
  → Discover assets in selected folder (AssetDatabase.FindAssets)
  → Classify assets by type (scene, prefab, material, texture, FBX, other)
  → Convert each type in order: textures → FBX → materials → prefabs → scenes
  → Generate project.godot + folder structure
  → Log skip report to Unity Console
```

### Key Design Decisions

- **Runs inside Unity** — Uses Unity's own APIs to read materials, traverse scene hierarchies, and resolve asset references. No custom YAML parser or external libraries needed.
- **Folder-only export** — Only assets physically inside the selected folder are exported. External dependencies are not pulled in, avoiding Unity-specific assets Godot can't use. Unresolved `ExtResource` references are expected if referenced assets live outside the folder.
- **FBX files are copied, not converted** — Godot imports FBX natively. Node names are extracted by loading the FBX as a `GameObject` via `AssetDatabase.LoadAssetAtPath<GameObject>()`.
- **Coordinate system conversion** — Unity is left-handed Y-up; Godot is right-handed Y-up. The conversion negates Z position, negates X/Y quaternion components, builds a 3x3 basis matrix, and serializes as `Transform3D`. Applied to scene-level transforms only. Full algorithm in SPEC.md Section 6.
- **Best-effort error handling** — Never aborts on a single bad asset. Each asset is processed independently. Unresolvable references produce placeholder nodes + warnings.
- **Prefab nesting** — One level deep is flattened; deeper nesting is warned.

### Source Layout

All code lives in `Assets/Editor/U2GExporter/` with an Editor-only `.asmdef`:

| File | Role |
|---|---|
| `ExportMenu.cs` | Context menu entry point, orchestration, progress bar |
| `DependencyResolver.cs` | Asset discovery + classification |
| `NodeConverter.cs` | Shared hierarchy traversal used by both SceneExporter and PrefabExporter |
| `SceneExporter.cs` | Scene → .tscn, prefab instance detection, override application |
| `PrefabExporter.cs` | Prefab → .tscn, FBX instancing, material overrides |
| `MaterialExporter.cs` | Material → .tres (URP Lit, URP Unlit, legacy Standard) |
| `TextureExporter.cs` | Texture copy logic |
| `FbxExporter.cs` | FBX copy + node name extraction |
| `LightExporter.cs` | Light component → Godot light node data |
| `CameraExporter.cs` | Camera component → Godot camera node data |
| `CoordConvert.cs` | Coordinate system conversion math |
| `TscnWriter.cs` | .tscn file format serializer |
| `TresWriter.cs` | .tres file format serializer |
| `PathUtil.cs` | Unity→Godot path conversion utilities |
| `ProjectWriter.cs` | project.godot generation |
| `SkipReport.cs` | Skip report generation |

## Critical Unity API Pitfalls

These are hard-won lessons from development. Do not change this behavior without understanding why.

### FBX/Prefab Hierarchy Resolution

**`AssetDatabase.LoadAssetAtPath<GameObject>()` returns unresolved hierarchies for FBX-backed assets.** For single-mesh FBX files and FBX prefab variants, the returned GameObject has zero children — Unity doesn't resolve the FBX hierarchy until the object is instantiated. Any code that needs to inspect FBX children or material assignments must use `Object.Instantiate()` on a temporary copy, then `Object.DestroyImmediate()` it after.

This affects:
- **`PrefabExporter`** — Uses `Object.Instantiate(prefabAsset)` to get resolved children with materials.
- **`FbxExporter.GetNodeNames()`** — Falls back to instantiation, then to `MeshFilter.sharedMesh.name` for the Godot child node name.

### Scene Closing

Unity won't allow closing the last loaded scene. `ExportMenu` checks `EditorSceneManager.loadedSceneCount > 1` before calling `CloseScene`. The `finally` block restores the original scene setup via `RestoreSceneManagerSetup`.

### AssetDatabase on Instantiated Clones

`AssetDatabase.GetAssetPath()` does NOT work on instantiated GameObjects (returns empty). Use the original asset reference for any AssetDatabase queries. However, `sharedMaterials` on an instantiated clone still references the original material assets, so `GetAssetPath(mat)` works on those.

## Serialization Rules

- All Godot paths use forward slashes and `res://` prefixes, even on Windows
- Floats use `CultureInfo.InvariantCulture` (decimal separator always `.`)
- Strings are double-quoted with escaped `"` and `\`
- Asset lists from HashSets are sorted lexicographically before export

## Unity Test Project

The root-level `Assets/Scenes/`, `Assets/Settings/`, `ProjectSettings/`, `Packages/` directories are a Unity 6 project with sample content. Do not edit generated Unity files like `Assembly-CSharp.csproj`, `Library/`, or `Temp/`.
