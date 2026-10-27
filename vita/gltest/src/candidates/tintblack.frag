#version 100
precision highp float;
uniform sampler2D u_tex;
varying vec4 v_light;
varying vec4 v_dark;
varying vec2 v_uv;
void main()
{
    vec4 t = texture2D(u_tex, v_uv);
    vec3 rgb = ((t.a - 1.0) * v_dark.a + 1.0 - t.rgb) * v_dark.rgb + t.rgb * v_light.rgb;
    gl_FragColor = vec4(rgb, t.a * v_light.a);
}
