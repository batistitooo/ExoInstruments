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

path = sys.argv[1]
m = mulib.Mu()
if not m.read(path):
    print("failed to read", path)
    sys.exit(1)
print(f"{os.path.basename(os.path.dirname(path))}/{os.path.basename(path)}  version={m.version}")
print(f"materials: {[mat.name for mat in m.materials]}")
print(f"textures:  {[t.name for t in m.textures]}")
print("transform tree:")
walk(m.obj)
