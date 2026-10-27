// Shepherd GL backend: sprites, meshes, UI. GLSL ES 1.00 body, no #version and no precision statements: GlBackend prepends
// a per-dialect prelude (essl100 = Vita/vitaGL, glsl120 = macOS legacy 2.1, glsl150 = macOS 3.2 core).
// Positions arrive in target pixels (top-down, y down); u_xform maps them to clip space.
attribute vec2 a_pos;
attribute vec4 a_color;
attribute vec2 a_uv;
uniform vec4 u_xform;   // ndc = a_pos * u_xform.xy + u_xform.zw
varying vec4 v_color;
varying vec2 v_uv;
#ifdef GRAB
// polish round 2, the BlendModes backdrop program: this vertex's place in the target as a 0..1 coordinate (u_xform.xy = 2 / size),
// to sample the backdrop copy (same size and top-down layout as the target)
varying vec2 v_screen;
#endif
#ifdef ADD
// fidelity round 1, the lit-sprite program (IAddDraw): a per-vertex colour added after texture x colour (what the owner-local lit
// model adds: fog, fill, emission); a = 1 marks a premultiplied draw, whose added colour is scaled by the fragment's alpha
attribute vec4 a_add;
varying vec4 v_add;
#endif
void main()
{
    v_color = a_color;
    v_uv = a_uv;
#ifdef GRAB
    v_screen = a_pos * u_xform.xy * 0.5;
#endif
#ifdef ADD
    v_add = a_add;
#endif
    gl_Position = vec4(a_pos * u_xform.xy + u_xform.zw, 0.0, 1.0);
}
