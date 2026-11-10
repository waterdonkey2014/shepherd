// Kit #12 boot (Render/K12Stencil.cs, SHEPHERD_K12_STENCIL=1): sprite.frag's plain path with uGUI's UNITY_UI_ALPHACLIP -- a fragment whose alpha is
// below 0.001 is discarded (Unity's clip (color.a - 0.001)), so a Mask graphic's transparent texels write no stencil. Built on first use, only while a
// stencil-writing material with _UseUIAlphaClip draws; outside the boot program set and the gxp cache (sprite.frag itself is unchanged).
uniform sampler2D u_tex;
varying vec4 v_color;
varying vec2 v_uv;
void main()
{
    vec4 c = texture2D(u_tex, v_uv) * v_color;
    if (c.a < 0.001) discard;
    gl_FragColor = c;
}
