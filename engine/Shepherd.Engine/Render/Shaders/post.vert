// Shepherd GL backend: full-screen passes (Stylizer, present). a_uv is passed through unchanged:
// target pixel coordinates for the Stylizer, 0..1 texture coordinates for the present copy.
attribute vec2 a_pos;
attribute vec2 a_uv;
uniform vec4 u_xform;   // ndc = a_pos * u_xform.xy + u_xform.zw
varying vec2 v_uv;
void main()
{
    v_uv = a_uv;
    gl_Position = vec4(a_pos * u_xform.xy + u_xform.zw, 0.0, 1.0);
}
