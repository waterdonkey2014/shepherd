// Shepherd GL backend, kit #11 gpu1 (SHEPHERD_K11_GPUPROJ=1): the world pass's projection on the GPU. GLSL ES 1.00 body (GlBackend prepends
// the per-dialect prelude). A vertex arrives in WORLD space (the CPU applied the renderer's local-to-world matrix); this program does what
// SceneRenderer.Project does on the CPU, expression for expression: clip x / y / w from the view-projection's rows 0, 1 and 3, w clamped at
// 1e-5, the camera's pixel rect and the target height, then the pixel -> clip mapping of sprite.vert (w = 1: colours and uvs interpolate
// across the screen exactly as for the CPU-built vertices). Only draws whose every vertex lies inside the near and far planes come here.
// u_p0 / u_p1 / u_p2 = view-projection rows 0 / 1 / 3, u_p3 = pixel rect (x, y, w, h), u_p4.x = target height.
attribute vec3 a_pos;
attribute vec4 a_color;
attribute vec2 a_uv;
uniform vec4 u_xform;   // ndc = pixel * u_xform.xy + u_xform.zw
uniform vec4 u_p0;
uniform vec4 u_p1;
uniform vec4 u_p2;
uniform vec4 u_p3;
uniform vec4 u_p4;
varying vec4 v_color;
varying vec2 v_uv;
void main()
{
    v_color = a_color;
    v_uv = a_uv;
    float cx = u_p0.x * a_pos.x + u_p0.y * a_pos.y + u_p0.z * a_pos.z + u_p0.w;
    float cy = u_p1.x * a_pos.x + u_p1.y * a_pos.y + u_p1.z * a_pos.z + u_p1.w;
    float cw = u_p2.x * a_pos.x + u_p2.y * a_pos.y + u_p2.z * a_pos.z + u_p2.w;
    if (cw <= 1e-5) cw = 1e-5;
    float sx = u_p3.x + (cx / cw * 0.5 + 0.5) * u_p3.z;
    float sy = u_p4.x - (u_p3.y + (cy / cw * 0.5 + 0.5) * u_p3.w);
#ifdef PERSP
    // kit #13 looks (SHEPHERD_K13_PERSP=1): the clip w goes to the GPU, which then interpolates perspective-correctly (the same pixels)
    gl_Position = vec4((vec2(sx, sy) * u_xform.xy + u_xform.zw) * cw, 0.0, cw);
#else
    gl_Position = vec4(vec2(sx, sy) * u_xform.xy + u_xform.zw, 0.0, 1.0);
#endif
}
