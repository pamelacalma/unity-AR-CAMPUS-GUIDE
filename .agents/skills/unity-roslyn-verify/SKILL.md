---
name: unity-roslyn-verify
description: >-
  Compile-checks C# runtime and editor scripts in this Unity 6.6 Windows project
  using Unity's bundled dotnet and Roslyn csc.dll while Unity Editor is open.
  Use after creating or editing any C# file under Assets/Scripts or Assets/Editor.
---

# Verify Unity 6.6 C# compilation with Roslyn

Use Unity `6000.6.1f1`'s bundled Roslyn compiler to verify `Assembly-CSharp.dll` and `Assembly-CSharp-Editor.dll` without launching a conflicting Unity batchmode process.

## Paths
- `dotnet`: `C:\Program Files\Unity\Hub\Editor\6000.6.1f1\Editor\Data\DotNetSdk\dotnet.exe`
- `csc.dll`: `C:\Program Files\Unity\Hub\Editor\6000.6.1f1\Editor\Data\DotNetSdk\sdk\8.0.318\Roslyn\bincore\csc.dll`
- Managed Unity DLLs: `C:\Program Files\Unity\Hub\Editor\6000.6.1f1\Editor\Data\Managed\UnityEngine\*.dll` and `...\Editor\Data\Managed\UnityEditor\*.dll`
- Package assemblies: `<repo>\Library\ScriptAssemblies\*.dll`

## Steps
1. Build a response file (`.rsp`) in the conversation scratch directory with `/nostdlib+`, `/target:library`, `/langversion:9.0`, `/define:UNITY_EDITOR;UNITY_EDITOR_64;UNITY_EDITOR_WIN;ENABLE_INPUT_SYSTEM;UNITY_6000_6_1;...`, references to NetStandard/Unity Managed DLLs and `Library\ScriptAssemblies\*.dll`, and all `.cs` files under `Assets` (excluding `Assets/Editor` for `Assembly-CSharp.dll`).
2. Compile `Assembly-CSharp.dll` first:
   `& "<dotnet>" "<csc.dll>" /noconfig "@<scratch>\Assembly-CSharp.dll.rsp"`
3. Compile `Assembly-CSharp-Editor.dll` second, referencing the freshly compiled `<scratch>\Assembly-CSharp.dll` (not `Library\ScriptAssemblies\Assembly-CSharp.dll`, which may be stale until Unity refreshes).
4. Confirm both commands exit with code `0`, zero errors, and zero new warnings in edited or added files.
