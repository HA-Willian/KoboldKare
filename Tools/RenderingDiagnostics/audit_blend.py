"""Read-only Blender source geometry audit; run with blender --background <file> --python this.py -- <output>."""
import bpy
import json
import math
import statistics
import sys
from collections import Counter, defaultdict
from pathlib import Path
from mathutils import Vector
from mathutils.bvhtree import BVHTree

output = Path(sys.argv[sys.argv.index("--") + 1])
records = []
for obj in bpy.data.objects:
    if obj.type != "MESH" or obj.name.lower() != "stone":
        continue
    mesh = obj.data
    mesh.calc_loop_triangles()
    vertices = [obj.matrix_world @ v.co for v in mesh.vertices]
    edges = Counter()
    for face in mesh.polygons:
        indices = list(face.vertices)
        for a, b in zip(indices, indices[1:] + indices[:1]):
            edges[tuple(sorted((a, b)))] += 1
    groups = defaultdict(list)
    for face in mesh.polygons:
        normal = (obj.matrix_world.to_3x3().inverted().transposed() @ face.normal).normalized()
        point = vertices[face.vertices[0]]
        key = tuple(round(float(c), 3) for c in normal) + (round(float(normal.dot(point)), 2),)
        groups[key].extend(vertices[i] for i in face.vertices)
    planes = []
    for key, points in groups.items():
        if len(points) < 6:
            continue
        n = Vector(key[:3]).normalized()
        u = n.cross(Vector((0, 0, 1)))
        if u.length < .01:
            u = n.cross(Vector((0, 1, 0)))
        u.normalize()
        v = n.cross(u).normalized()
        extents = [max(p.dot(axis) for p in points) - min(p.dot(axis) for p in points) for axis in (u, v)]
        planes.append({"normal": list(key[:3]), "distance": key[3], "spans_blender_units": extents, "vertex_references": len(points)})
    tree = BVHTree.FromPolygons(vertices, [list(t.vertices) for t in mesh.loop_triangles], all_triangles=True)
    lo = [min(p[i] for p in vertices) for i in range(3)]
    hi = [max(p[i] for p in vertices) for i in range(3)]
    thicknesses = []
    same_facing_pairs = 0
    hit_rays = 0
    for axis in (0, 1):
        cross_axis = 1 - axis
        direction = Vector((1, 0, 0) if axis == 0 else (0, 1, 0))
        for cross_index in range(1, 20):
            for height_index in range(1, 12):
                origin = Vector((0, 0, 0))
                origin[axis] = lo[axis] - 1
                origin[cross_axis] = lo[cross_axis] + (hi[cross_axis] - lo[cross_axis]) * cross_index / 20
                origin[2] = lo[2] + (hi[2] - lo[2]) * height_index / 12
                hits = []
                cursor = origin.copy()
                for unused in range(64):
                    point, normal, face, distance = tree.ray_cast(cursor, direction, hi[axis] - lo[axis] + 2)
                    if point is None:
                        break
                    hits.append((float((point - origin).dot(direction)), float(normal.dot(direction))))
                    cursor = point + direction * .0001
                if hits:
                    hit_rays += 1
                for entry, exit in zip(hits, hits[1:]):
                    if entry[1] < -.1 and exit[1] > .1 and exit[0] - entry[0] > .0002:
                        thicknesses.append(exit[0] - entry[0])
                    elif entry[1] * exit[1] > .01:
                        same_facing_pairs += 1
    records.append({
        "object": obj.name, "mesh": mesh.name, "unit_scale": bpy.context.scene.unit_settings.scale_length,
        "object_scale": list(obj.scale), "vertices": len(vertices), "polygons": len(mesh.polygons),
        "triangles": len(mesh.loop_triangles), "boundary_edges_by_vertex_index": sum(n == 1 for n in edges.values()),
        "nonmanifold_edges_by_vertex_index": sum(n > 2 for n in edges.values()),
        "source_bounds": {"min": [min(p[i] for p in vertices) for i in range(3)], "max": [max(p[i] for p in vertices) for i in range(3)]},
        "coplanar_groups": sorted(planes, key=lambda p: p["spans_blender_units"][0] * p["spans_blender_units"][1], reverse=True),
        "ray_audit": {"sampled_rays": 418, "rays_with_hits": hit_rays, "entry_exit_pairs": len(thicknesses),
                      "same_facing_adjacent_pairs": same_facing_pairs,
                      "thickness_min_blender_units": min(thicknesses) if thicknesses else None,
                      "thickness_median_blender_units": statistics.median(thicknesses) if thicknesses else None,
                      "thickness_max_blender_units": max(thicknesses) if thicknesses else None,
                      "pairs_below_0_3_units": sum(t < .3 for t in thicknesses),
                      "limitation": "Grid ray samples and face winding, not an exhaustive watertightness test. Same-facing pairs can come from intentionally open/disconnected geometry. Smallest Hole describes openings, not a required minimum wall thickness."},
        "limitation": "Read-only source audit. Approximate plane groups can include disconnected pieces and openings. Blender and imported Unity object coordinates differ; source spans alone do not establish world wall dimensions or invalid occlusion."
    })
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps({"source": bpy.data.filepath, "objects": records}, indent=2), encoding="utf-8")
print("Source geometry audit:", output)
