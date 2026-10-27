#version 100
precision highp float;
uniform sampler2D u_tex;
varying vec4 v_color;
varying vec2 v_uv;
void main()
{
    vec4 t = texture2D(u_tex, v_uv);
    float a = clamp((t.a - 0.44) / 0.12, 0.0, 1.0);
    t = vec4(1.0, 1.0, 1.0, a * a * (3.0 - 2.0 * a));
    gl_FragColor = t * v_color;
}
