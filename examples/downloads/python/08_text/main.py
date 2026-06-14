#! python3
# r: compas_nest

from compas.colors import Color
from compas.geometry import Frame
from compas.scene import Scene
from compas_nest import text_to_polylines

BLUE = Color.from_hex("#0072B2")

# 1) a frame to place/orient the text (origin = text origin, x-axis = reading direction)
frame = Frame([0.0, 0.0, 0.0], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0])

# 2) render text to single-stroke polylines (OpenNest font) on that frame, 10-unit cap height
strokes = text_to_polylines("compas_nest\n0 1 2 3 4 5 6 7 8 9", height=10.0, font="regular", frame=frame)

# 3) draw the stroke polylines (blue)
scene = Scene()
scene.clear()
for stroke in strokes:
    scene.add(stroke, color=BLUE)
scene.draw()
