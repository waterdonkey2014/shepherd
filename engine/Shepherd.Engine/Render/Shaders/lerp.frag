// Shepherd GL backend, kit #11 gpu2: the linear blend of two textures into a render target, a + t * (b - a) per channel (the
// textbook lerp, written in that order so it rounds like a multiply-add of the difference). Used for the post effect's blended
// LUT pair (GlBackend.RunLut, LutArgs.Blend): the target and both textures are the same size, and v_uv lands on texel centres,
// so filtering returns the texels themselves. Prelude precision: highp (GlBackend.FragPrecision). Built on first use, outside the
// boot program set and the gxp cache.
uniform sampler2D u_tex;
uniform sampler2D u_tex1;
uniform vec4 u_p0;   // x = t
varying vec2 v_uv;
void main()
{
    vec4 a = texture2D(u_tex, v_uv);
    vec4 b = texture2D(u_tex1, v_uv);
    gl_FragColor = a + u_p0.x * (b - a);
}
