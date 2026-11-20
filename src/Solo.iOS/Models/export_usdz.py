# Exports the open .blend as the model the app loads:
#
#   blender -b SoloLogo.blend -P export_usdz.py
#
# or, with the file open in Blender, run this from the Scripting tab. It writes
# ../Solo/Features/Onboarding/Models/<file name>.usdz, which the app's target picks up.
#
# The models are made in Blender's front view: x to the right, z up, and -y toward the
# eye. The export turns that into the app's stage, y up and z toward the eye, so a model
# is seen in the app as it is from the front here. The camera and lights in each file are
# for looking at it in Blender and stay behind, and so does the world.
#
# UV maps go along: the logo's bars have one, running bottom to top, which is what the app
# lays their fade of colour on. A mesh without one exports as it did before.
import os

import bpy

source = bpy.data.filepath
if not source:
    raise SystemExit("Save the file first: the export is named after it.")

name = os.path.splitext(os.path.basename(source))[0]
target = os.path.normpath(os.path.join(os.path.dirname(source), "..", "Solo", "Features", "Onboarding", "Models", name + ".usdz"))
os.makedirs(os.path.dirname(target), exist_ok=True)

bpy.ops.wm.usd_export(
    filepath=target,
    selected_objects_only=False,
    export_animation=False,
    export_cameras=False,
    export_lights=False,
    convert_world_material=False,
    export_materials=True,
    generate_preview_surface=True,
    export_normals=True,
    export_uvmaps=True,
    evaluation_mode='RENDER',
    convert_orientation=True,
    export_global_forward_selection='NEGATIVE_Z',
    export_global_up_selection='Y',
    root_prim_path="/root",
)
print("Exported", target)
