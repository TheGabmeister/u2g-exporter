# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**u2g-exporter** (Unity to Godot Exporter) is a Unity Editor tool that exports selected assets from a Unity project into a ready-to-open Godot 4.6.1 project. The user right-clicks a folder in the Project window, selects "Export to Godot", and gets a complete Godot project. V1 scope covers static meshes (FBX), textures, materials (URP Lit/Unlit + legacy Standard), scenes, and prefabs. See [SPEC.md](SPEC.md) for the complete specification — it is the authoritative source for all conversion logic.

**Unity version:** Unity 6 (6000.x), C# 9 / .NET Standard 2.1

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

### Source Layout (per SPEC.md Section 10)

All code lives in `Assets/Editor/U2GExporter/` with an Editor-only `.asmdef`:

```
Assets/Editor/U2GExporter/
├── ExportMenu.cs          # Context menu entry point, orchestration
├── DependencyResolver.cs  # Asset discovery + classification
├── SceneExporter.cs       # Scene → .tscn conversion
├── PrefabExporter.cs      # Prefab → .tscn conversion
├── MaterialExporter.cs    # Material → .tres conversion
├── TextureExporter.cs     # Texture copy logic
├── FbxExporter.cs         # FBX copy + node name extraction
├── CoordConvert.cs        # Coordinate system conversion math
├── ProjectWriter.cs       # project.godot + folder structure
├── TscnWriter.cs          # .tscn file format serializer
├── TresWriter.cs          # .tres file format serializer
└── SkipReport.cs          # Skip report generation
```

## Unity Test Project

The root-level `Assets/Scenes/`, `Assets/Settings/`, `ProjectSettings/`, `Packages/` directories are a Unity 6 project with sample content. This project serves as the development environment and provides test assets for validating the exporter.
