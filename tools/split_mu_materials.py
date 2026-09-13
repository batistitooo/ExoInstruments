#!/usr/bin/env python3
"""
Give every renderer in a .mu exactly one material, which is all KSP's PartReader supports.

KSP applies each material with Renderer.sharedMaterial, so a renderer listing several keeps only
the last one applied, drawn on its first submesh. This rewrites each such object: it keeps its
first submesh and material, and every other submesh moves to a child object with an identity
transform, its own vertices and that submesh's material. The geometry is unchanged.

    python3 tools/split_mu_materials.py model.mu [out.mu]

Needs io_object_mu, found like tools/dump_mu.py finds it.
"""
import sys, os, copy
sys.path.insert(0, os.environ.get("IO_OBJECT_MU",
                                 os.path.join(os.path.dirname(__file__), "..", "..", "io_object_mu-master")))
import mu as mulib


def sub_mesh(mesh, tris):
    used = sorted({i for t in tris for i in t})
    remap = {old: new for new, old in enumerate(used)}
    n = len(mesh.verts)
    out = mulib.MuMesh()
    out.verts = [mesh.verts[i] for i in used]
    for attr in ("uvs", "uv2s", "normals", "tangents", "colors", "boneWeights"):
        src = getattr(mesh, attr)
        setattr(out, attr, [src[i] for i in used] if len(src) == n else [])
    out.bindPoses = list(mesh.bindPoses)
    out.submeshes = [[tuple(remap[i] for i in t) for t in tris]]
    return out


def split(mu_file, obj):
    added = sum(split(mu_file, child) for child in list(obj.children))
    r = getattr(obj, "renderer", None)
    m = getattr(obj, "shared_mesh", None)
    if r is None or m is None or len(r.materials) <= 1:
        return added
    if len(r.materials) != len(m.submeshes):
        raise ValueError(f"{obj.transform.name}: {len(r.materials)} materials for {len(m.submeshes)} "
                         "submeshes; split the object by material before exporting")
    for k in range(1, len(r.materials)):
        child = mulib.MuObject()
        t = mulib.MuTransform()
        t.name = f"{obj.transform.name}__{mu_file.materials[r.materials[k]].name}"
        t.localPosition = (0.0, 0.0, 0.0)
        t.localRotation = (1.0, 0.0, 0.0, 0.0)
        t.localScale = (1.0, 1.0, 1.0)
        child.transform = t
        child.tag_and_layer = copy.deepcopy(obj.tag_and_layer)
        child.shared_mesh = sub_mesh(m, m.submeshes[k])
        renderer = mulib.MuRenderer()
        renderer.castShadows, renderer.receiveShadows = r.castShadows, r.receiveShadows
        renderer.materials = [r.materials[k]]
        child.renderer = renderer
        child.components = [child.shared_mesh, renderer]
        obj.children.append(child)
        added += 1
    first = sub_mesh(m, m.submeshes[0])
    obj.components = [first if c is m else c for c in obj.components]
    obj.shared_mesh = first
    r.materials = [r.materials[0]]
    return added


if __name__ == "__main__":
    if len(sys.argv) not in (2, 3):
        sys.exit(__doc__)
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) == 3 else src
    f = mulib.Mu()
    if not f.read(src):
        sys.exit(f"cannot read {src}")
    added = split(f, f.obj)
    f.write(dst)
    print(f"{src}: {added} object(s) added, written to {dst}")
