# AGENTS.md

Guidance for coding agents working in this repository.

## Project Summary

`u2g-exporter` is a Unity Editor tool that exports selected Unity assets into a ready-to-open Godot 4.6.1 project. V1 scope is limited to static meshes (FBX), textures, materials, scenes, and prefabs.

Material support currently covers:

- `Universal Render Pipeline/Lit`
- `Universal Render Pipeline/Simple Lit`
- `Universal Render Pipeline/Unlit`
- `Standard`
- recognized legacy built-in shader families
- recognized `Unlit/*` shader families

The authoritative product and implementation spec is [SPEC.md](./SPEC.md). If this file and the spec ever disagree, follow `SPEC.md`.

## Current Repository State

- This repository is a Unity 6 project used as the development and test host for the exporter.
- The exporter already exists under `Assets/Editor/U2GExporter/`; extend the current implementation instead of recreating it.
- Existing root Unity folders such as `Assets/`, `Packages/`, and `ProjectSettings/` are project host content, not disposable scaffolding.
- Local sample content may live under `Assets/Test/` for manual export verification, but do not assume specific test assets or scenes are present unless you confirm them in the workspace first.
- There is no separate CLI build or test harness in the repo; compilation and end-to-end verification happen inside the Unity Editor.

## Required Versions

- Unity editor: `6000.3.12f1` (`ProjectSettings/ProjectVersion.txt`)
- Unity rendering stack: URP (`com.unity.render-pipelines.universal` `17.3.0`)
- Target Godot version: `4.6.1`
- Language/runtime target from the project guidance: C# 9 / .NET Standard 2.1

## Where Exporter Code Lives

Exporter code belongs under `Assets/Editor/U2GExporter/` in an Editor-only assembly. Current files include:

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
- `NodeConverter.cs`
- `PathUtil.cs`
- `TscnWriter.cs`
- `TresWriter.cs`
- `SkipReport.cs`

Prefer fitting work into the existing class layout before adding new files. Do not edit generated Unity files like `Assembly-CSharp.csproj`, `Assembly-CSharp-Editor.csproj`, `Library/`, or `Temp/` unless the task explicitly requires it.

## Implementation Rules

- Keep the exporter Editor-only. Do not introduce runtime or player-build dependencies.
- Use Unity Editor APIs already called out in the spec such as `AssetDatabase`, `PrefabUtility`, `EditorSceneManager`, `EditorUtility`, and `ModelImporter`.
- Do not add external NuGet packages or third-party Unity assets unless the user explicitly asks for that change.
- Preserve the conversion order from the spec: textures, FBX, materials, prefabs, scenes, then `project.godot`.
- Treat conversion as best-effort: a bad asset should produce warnings or errors for that asset, not abort the whole export unless the failure is truly fatal.
- Prompt before writing into a non-empty output folder; do not silently overwrite an existing Godot project.
- Preserve folder structure by stripping only the `Assets/` prefix when generating Godot output paths.
- Keep logging explicit and actionable because the Unity Console is the main user feedback surface for this tool.
- Serialization must be deterministic: stable ordering, invariant-culture floats, double-quoted strings, escaped embedded quotes and backslashes, and forward-slash `res://` paths on every OS.
- Do not pre-generate Godot `.import` files as part of export; V1 relies on Godot generating them on first open.

## Easy-To-Miss Spec Requirements

- Export only assets physically inside the selected folder. Referenced assets outside that folder are not pulled in for V1, even if scenes or prefabs reference them.
- Filter out `Packages/` assets, script files, and editor-only assets whose path contains an `Editor/` segment during discovery.
- Texture handling is copy-only in V1. PSD and EXR files are copied with warnings; they are not transcoded.
- FBX files are copied directly, but the exported copy may need binary patching so Godot import scale matches Unity import scale. This includes `UnitScaleFactor` correction and resetting non-unit `Lcl Scaling` values on FBX Model nodes back to identity so Godot does not apply scale a second time. Never modify the original Unity source FBX in place.
- Do not revive the rejected `.import`-file workaround for FBX scaling. The spec explicitly says it breaks resource references because Godot-owned remap metadata cannot be reliably pre-generated.
- The FBX scale patching logic is best-effort and binary-only. ASCII FBX files are copied unmodified if the parser cannot locate patchable values.
- V1 only supports meshes whose source asset path resolves to `.fbx`. Non-FBX mesh sources, including imported `.obj` and `.blend`, Unity primitive meshes, and generated mesh sub-assets, must become placeholder `Node3D`s with warnings and skip-report entries.
- `AssetDatabase.LoadAssetAtPath<GameObject>()` can return unresolved FBX-backed hierarchies. If code needs FBX child nodes or material assignments, use a temporary instantiated copy and clean it up afterward.
- Scene and prefab transforms require Unity-to-Godot handedness conversion exactly as described in `SPEC.md` section 6.
- Every exported Unity scene must have a synthetic single Godot root node named after the scene file, with all Unity root objects emitted beneath it.
- Scene export must capture the full pre-export editor scene-manager setup and restore it afterward with `EditorSceneManager.RestoreSceneManagerSetup()`, including additive scenes and the active scene.
- If the user has unsaved scene changes, export must first go through `EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()` and abort cleanly if the user cancels.
- Material conversion must support the shader families listed above. Unknown shaders still fall back to a default white `StandardMaterial3D` with a warning.
- All supported materials in V1 emit `StandardMaterial3D`, not `ORMMaterial3D`. Unlit families must still be marked unshaded, and packed metallic and smoothness textures are only approximated.
- Custom shaders are still out of scope unless they match one of the explicitly recognized shader families in the spec.
- Duplicate sibling names must be made unique for Godot node output.
- Override targeting must be based on recorded source-object-to-emitted-node-path mappings, not a later name lookup.
- One level of nested prefabs is flattened; deeper nesting is warned and not fully resolved.
- Disabled state matters: inactive `GameObject`s and disabled `MeshRenderer`, `Light`, and `Camera` components must preserve visibility behavior per the spec.
- Unsupported asset types belong in the skip report rather than being silently ignored.

## Working Safely In This Repo

- Prefer changes that are narrowly scoped to the exporter codepath.
- Avoid unrelated edits to sample scenes, render pipeline assets, or project settings unless they are required for the task.
- If you need tests, prefer Unity Edit Mode tests that exercise pure conversion logic or writer output deterministically.
- When working on scene export, be careful not to leave the Unity Editor in a different multi-scene state after the operation.
- When implementing serializers such as `.tscn` and `.tres`, keep output stable and diff-friendly.
- Treat existing worktree changes as user-owned unless you made them; do not clean up unrelated files as part of exporter work.

## Practical Workflow

1. Read the `SPEC.md` sections relevant to the task before changing code.
2. Check whether the task belongs in a dedicated exporter class or in a shared writer or utility.
3. If the task touches scenes, prefabs, materials, serialization, or FBX scale handling, re-check the corresponding spec subsections before coding because those rules are intentionally strict.
4. Implement the smallest vertical slice that preserves the spec's output format and warning behavior.
5. Verify that any path, transform, material mapping, placeholder behavior, scene-restore logic, FBX binary patching behavior, and skip-report behavior still match the spec exactly.
