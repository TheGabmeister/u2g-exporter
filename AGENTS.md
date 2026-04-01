# AGENTS.md

Guidance for coding agents working in this repository.

## Project Summary

`u2g-exporter` is a Unity Editor tool that exports selected Unity assets into a ready-to-open Godot 4.6.1 project. V1 scope is limited to static meshes (FBX), textures, materials (URP Lit/Unlit + legacy Standard), scenes, and prefabs.

The authoritative product and implementation spec is [SPEC.md](./SPEC.md). If this file and the spec ever disagree, follow `SPEC.md`.

## Current Repository State

- This repository is a Unity 6 project used as the development/test host for the exporter.
- As of now, the exporter implementation described in `SPEC.md` has not been scaffolded yet.
- Existing root Unity folders such as `Assets/`, `Packages/`, and `ProjectSettings/` should be treated as the project host, not as disposable scaffolding.

## Required Versions

- Unity editor: `6000.3.12f1` (`ProjectSettings/ProjectVersion.txt`)
- Unity rendering stack: URP (`com.unity.render-pipelines.universal` `17.3.0`)
- Target Godot version: `4.6.1`
- Language/runtime target from the project guidance: C# 9 / .NET Standard 2.1

## Where New Code Should Go

Per the spec, exporter code should live under `Assets/Editor/U2GExporter/` in an Editor-only assembly. Expected files include:

- `U2GExporter.asmdef`
- `ExportMenu.cs`
- `DependencyResolver.cs`
- `TextureExporter.cs`
- `FbxExporter.cs`
- `MaterialExporter.cs`
- `SceneExporter.cs`
- `PrefabExporter.cs`
- `LightExporter.cs`
- `CameraExporter.cs`
- `ProjectWriter.cs`
- `CoordConvert.cs`
- `TscnWriter.cs`
- `TresWriter.cs`
- `SkipReport.cs`

Do not edit generated Unity files like `Assembly-CSharp.csproj`, `Assembly-CSharp-Editor.csproj`, `Library/`, or `Temp/` unless the task explicitly requires it.

## Implementation Rules

- Keep the exporter Editor-only. Do not introduce runtime/player dependencies.
- Use Unity Editor APIs already called out in the spec (`AssetDatabase`, `PrefabUtility`, `EditorSceneManager`, `EditorUtility`, `ModelImporter`, etc.).
- Do not add external NuGet packages or third-party Unity assets unless the user explicitly asks for that change.
- Preserve the conversion order from the spec: textures, FBX, materials, prefabs, scenes, then `project.godot`.
- Treat conversion as best-effort: a bad asset should produce warnings/errors for that asset, not abort the whole export unless the failure is truly fatal.
- Prompt before writing into a non-empty output folder; do not silently overwrite an existing Godot project.
- Preserve folder structure by stripping only the `Assets/` prefix when generating Godot output paths.
- Keep logging explicit and actionable because the Unity Console is the main user feedback surface for this tool.
- Serialization must be deterministic: stable ordering, invariant-culture floats, double-quoted strings, escaped embedded quotes/backslashes, and forward-slash `res://` paths on every OS.

## Easy-To-Miss Spec Requirements

- Dependency resolution must include referenced assets outside the selected folder via `AssetDatabase.GetDependencies(..., recursive: true)`.
- FBX files are copied directly; they are not converted into another mesh format.
- V1 only supports meshes whose source asset path resolves to `.fbx`. Non-FBX mesh sources, including imported `.obj` / `.blend`, Unity primitive meshes, and generated mesh sub-assets, must become placeholder `Node3D`s with warnings and skip-report entries.
- Scene and prefab transforms require Unity-to-Godot handedness conversion exactly as described in `SPEC.md` section 6.
- Every exported Unity scene must have a synthetic single Godot root node named after the scene file, with all Unity root objects emitted beneath it.
- Scene export must capture the full pre-export editor scene-manager setup and restore it afterward with `EditorSceneManager.RestoreSceneManagerSetup()`, including additive scenes and the active scene.
- Material conversion must support `Universal Render Pipeline/Lit`, `Universal Render Pipeline/Unlit`, and legacy `Standard`; unknown shaders fall back to a default white `StandardMaterial3D` with a warning.
- Material export in V1 always emits `StandardMaterial3D`, not `ORMMaterial3D`. Packed metallic/smoothness textures are only approximated; do not invent channel-splitting or repacking behavior beyond what the spec defines.
- Duplicate sibling names must be made unique for Godot node output.
- Override targeting must be based on recorded source-object-to-emitted-node-path mappings, not a later name lookup.
- One level of nested prefabs is flattened; deeper nesting is warned and not fully resolved.
- Disabled state matters: inactive `GameObject`s and disabled `MeshRenderer`/`Light`/`Camera` components must preserve visibility behavior per the spec.
- Unsupported asset types belong in the skip report rather than being silently ignored.

## Working Safely In This Repo

- Prefer changes that are narrowly scoped to the exporter codepath.
- Avoid unrelated edits to sample scenes, render pipeline assets, or project settings unless they are required for the task.
- If you need tests, prefer Unity Edit Mode tests that exercise pure conversion logic or writer output deterministically.
- When working on scene export, be careful not to leave the Unity Editor in a different multi-scene state after the operation.
- When implementing serializers (`.tscn`, `.tres`), keep output stable and diff-friendly.

## Practical Workflow

1. Read `SPEC.md` sections relevant to the task before changing code.
2. Check whether the task belongs in a dedicated exporter class or in a shared writer/utility.
3. If the task touches scenes, prefabs, materials, or serialization, re-check the corresponding spec subsections before coding because those rules are intentionally strict.
4. Implement the smallest vertical slice that preserves the spec's output format and warning behavior.
5. Verify that any path, transform, material mapping, placeholder behavior, or scene-restore logic still matches the spec exactly.
