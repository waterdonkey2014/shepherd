Candidate shaders: NOT part of GlBackend (engine/Shepherd.Engine/Render/Shaders). The gltest compiles, links and checks them
on vitaGL so a later engine change knows they work there. Each is reported as `CAND` (not counted in the PASS/FAIL verdict).

- `tintblack.*`: Spine two-colour tint ("tint black") in the premultiplied-alpha form of spine-runtimes' two-colour batch
  (light = vertex colour, premultiplied; dark = second vertex colour, dark.a = 1 for PMA atlases):
  `rgb = ((t.a - 1) * dark.a + 1 - t.rgb) * dark.rgb + t.rgb * light.rgb`, `a = t.a * light.a`.
  GlBackend has no tint-black path today: SceneRenderer maps Spine materials to BLEND_PREMULTIPLIED with the tint (and the
  premultiplication) in the one vertex colour, and drops a dark colour. Check the formula against the game's own Spine
  shader before adopting it. Attribute 3 (`a_color2`) is the dark colour; vertex = 48 bytes (pos 2f, light 4f, dark 4f, uv 2f).
Written like the backend's text: `#version 100`, one precision line, no comments or preprocessor lines for vitaGL.
