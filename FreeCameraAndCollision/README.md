# FreeCamera and Collision 1.2.0

A BepInEx mod for Sailwind 0.38.1.

## Installation

Extract the ZIP, then place the `FreeCameraAndCollision` folder inside
`Sailwind/BepInEx/plugins`. The DLL should end up at:

`Sailwind/BepInEx/plugins/FreeCameraAndCollision/FreeCameraAndCollision.dll`

Requires BepInEx 5.

## Controls

1. Press `C` (vanilla control) to enter Sailwind's third-person boat camera.
2. Press `B` to enter or leave free camera.
3. A controller can toggle free camera with the left-stick click.

While free camera is active:

- Mouse / right stick: look
- `W` / `S` or left stick: forward / backward
- `A` / `D` or left stick: strafe left / right
- `Space` / right shoulder: rise
- `Left Ctrl` or `Right Ctrl` / left shoulder: descend
- Hold `Left Shift` or `Right Shift`: move at 1.5x speed
- Press `H`: toggle following the ship's translation
- Mouse wheel / controller triggers: zoom

Gameplay input and player movement are captured while free camera is active.
Ship following starts enabled. When following is disabled, the camera stays in
world space until the ship would move more than 80 units away, then the camera
is moved along the distance boundary. Third-person boat-camera zoom is also
extended from the vanilla 40-unit limit to 80 units.

## Configuration

The BepInEx configuration file is created after the first launch:

`BepInEx/config/com.DogEggz.sailwind.freecameraandcollision.cfg`

Water settings update live when changed through Configuration Manager:

- Water Collision
- Sphere Radius
- Surface Padding

Water collision samples Sailwind's animated Crest ocean surface. Obsolete solid
collision settings from version 1.0.0 are removed from existing configuration
files when the mod starts.

## Compatibility notes

- Built against Sailwind 0.38.1 and Unity 2019.1.10f1.
- Designed for desktop keyboard/mouse and gamepad play.
- Free-camera activation is disabled in VR and in the shipyard.
