# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**u2g-exporter** (Unity to Godot Exporter) is a cross-platform C++17 desktop application that converts Unity `.unitypackage` files into ready-to-open Godot 4.6.1 projects. V1 scope covers static meshes (FBX), textures, materials (URP Lit/Unlit + legacy Standard), scenes, and prefabs. See [SPEC.md](SPEC.md) for the complete specification — it is the authoritative source for all conversion logic.

**Current state:** The repository contains the specification and a Unity test project (for generating test `.unitypackage` files). The C++ implementation is being built per SPEC.md Section 12.

## Build System

- **C++17**, **CMake 3.16+**
- All dependencies vendored in `thirdparty/` (ufbx, Dear ImGui, GLFW, miniz, nativefiledialog-extended)
- Cross-platform: Windows, macOS, Linux

```bash
mkdir build && cd build
cmake ..
cmake --build . --config Release
```

## Architecture

The converter implements a **6-phase pipeline** running on a background worker thread (main thread runs the ImGui UI loop):

```
.unitypackage (tar.gz)
  → Extract to temp dir (miniz + custom tar reader)
  → Build GUID→path table from pathname + asset.meta files
  → Classify assets by type (scene, prefab, material, texture, FBX, other)
  → Convert each type in order: textures → FBX → materials → prefabs → scenes
  → Generate project.godot + folder structure
  → Produce skip report + cleanup temp dir
```

### Key Design Decisions

- **Custom Unity YAML parser** — Unity uses a non-standard YAML 1.1 variant with custom tags (`!u!<classID> &<fileID>`). A purpose-built parser handles this instead of a generic YAML library.
- **FBX files are copied, not converted** — Godot imports FBX natively. The converter uses ufbx only to extract node/mesh names for material override paths and fileID resolution.
- **Coordinate system conversion** — Unity is left-handed Y-up; Godot is right-handed Y-up. The conversion negates Z position, negates X/Y quaternion components, builds a 3x3 basis matrix, and serializes as `Transform3D`. Applied to scene-level transforms only (FBX transforms are handled by Godot's importer). Full algorithm in SPEC.md Section 7.
- **Best-effort error handling** — Never aborts on a single bad asset. Each asset is processed independently. Unresolvable references produce placeholder nodes + warnings.
- **Prefab nesting** — One level deep is flattened; deeper nesting is warned.

### Source Layout (per SPEC.md Section 12)

```
src/
├── main.cpp                         # Entry point, ImGui setup, main loop
├── gui/                             # ImGui window + converter UI
├── converter/                       # Pipeline: extractor, GUID table, YAML parser,
│                                    #   texture/material/scene/prefab/light/camera converters,
│                                    #   project writer, coord_convert
└── util/                            # Logging, common types (AssetEntry, etc.)
```

## Unity Test Project

The `Assets/`, `ProjectSettings/`, `Packages/` directories are a Unity 6 project used to produce test `.unitypackage` files. These are not part of the C++ converter — they exist so developers can create controlled test inputs.
