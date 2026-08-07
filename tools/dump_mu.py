#!/usr/bin/env python3
"""Dump a KSP .mu model's transform tree, animation clips and materials.

WHY THIS EXISTS. A part config names transforms the model is supposed to carry - the optical
boresight, the transform holding the aperture door's animation - and getting one of those names
wrong produces a part that loads, renders and silently does not work. Reading the binary settles
it before the game is launched, which is the same reason every other tool in this directory
exists.

It uses io_object_mu's mu.py, which is a standalone .mu reader and needs no Blender. Clone
https://github.com/taniwha/io_object_mu next to the mod (or point IO_OBJECT_MU at it) and run:

    python3 tools/dump_mu.py path/to/model.mu
"""
import sys, os
sys.path.insert(0, os.environ.get("IO_OBJECT_MU",
                                 os.path.join(os.path.dirname(__file__), "..", "..", "io_object_mu-master")))
import mu as mulib

def walk(obj, depth=0):
    bits = []
    if getattr(obj, "shared_mesh", None) is not None:
        m = obj.shared_mesh
        bits.append(f"mesh({len(m.verts)}v,{len(m.submeshes)}sm)")
    if getattr(obj, "renderer", None) is not None:
        bits.append("renderer")
    if getattr(obj, "collider", None) is not None:
        bits.append("collider")
    anim = getattr(obj, "animation", None)
    if anim is not None:
        clips = [c.name for c in getattr(anim, "clips", [])]
        bits.append(f"ANIMATION clips={clips}")
    t = obj.transform
    print("  " * depth + f"- {t.name}  pos={tuple(round(v,4) for v in t.localPosition)}"
          + ("  [" + ", ".join(bits) + "]" if bits else ""))
    for c in obj.children:
        walk(c, depth + 1)

def check_textures(m, path):
    """Report every texture the model names against the files sitting next to it.

    KSP resolves a .mu's texture by name relative to the model's own directory, and when the
    file is not there it logs one ERR line at load and hands the material a null map. Nothing
    else happens: the mesh still draws, tinted by the material's _Color alone, so a model can
    ship for months referencing a texture that was never exported. ExoObservatoryLVL1 did
    exactly that, pointing its dome, doors and tower at a TextMesh Pro example asset. Returns
    the number of missing textures so the caller can fail on it.
    """
    directory = os.path.dirname(path) or "."
    missing = 0
    for t in m.textures:
        stem = os.path.splitext(t.name)[0]
        found = next((e for e in (".png", ".dds", ".jpg", ".tga", ".mbm", ".truecolor")
                      if os.path.exists(os.path.join(directory, stem + e))), None)
        print(f"  {t.name:44} {'-> ' + stem + found if found else 'MISSING NEXT TO THE MODEL'}")
        missing += found is None
    return missing


path = sys.argv[1]
m = mulib.Mu()
if not m.read(path):
    print("failed to read", path)
    sys.exit(1)
print(f"{os.path.basename(os.path.dirname(path))}/{os.path.basename(path)}  version={m.version}")
print(f"materials: {[mat.name for mat in m.materials]}")
print(f"textures ({len(m.textures)}):")
missing = check_textures(m, path)
print("transform tree:")
walk(m.obj)
if missing:
    print(f"\n{missing} texture(s) the game will not find. KSP logs one ERR each and draws the "
          "mesh with a null map, so this never shows up as a crash.")
    sys.exit(2)
