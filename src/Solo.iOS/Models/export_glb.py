# Exports the open .blend as the model the documentation site's front page loads:
#
#   blender -b SoloLogo.blend -P export_glb.py
#
# It writes site/assets/models/<file name>.glb, beside export_usdz.py's copy for the app:
# the one source, exported twice. After changing SoloLogo.blend, run both and commit all
# three.
#
# The models are made in Blender's front view: x to the right, z up, and -y toward the
# eye. glTF is y up and z toward the eye, which the export sees to. Only the meshes go:
# the camera and lights are for looking at it in Blender, and the page paints the pieces
# itself (site/_hero/hero-logo.js), finding each by the name it has here, so no materials
# go either.
import os

import bpy

source = bpy.data.filepath
if not source:
    raise SystemExit("Save the file first: the export is named after it.")

name = os.path.splitext(os.path.basename(source))[0]
target = os.path.normpath(os.path.join(os.path.dirname(source), "..", "..", "..", "site", "assets", "models", name + ".glb"))
os.makedirs(os.path.dirname(target), exist_ok=True)

bpy.ops.object.select_all(action='DESELECT')
for item in bpy.data.objects:
    item.select_set(item.type == 'MESH')

bpy.ops.export_scene.gltf(
    filepath=target,
    export_format='GLB',
    use_selection=True,
    export_apply=True,
    export_yup=True,
    export_normals=True,
    export_texcoords=True,
    export_materials='NONE',
    export_animations=False,
    export_cameras=False,
    export_lights=False,
)
print("Wrote", target)
