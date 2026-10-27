#version 100
precision highp float;
attribute vec2 a_pos;
attribute vec4 a_color;
attribute vec2 a_uv;
uniform vec4 u_xform;   // ndc = a_pos * u_xform.xy + u_xform.zw
varying vec4 v_color;
varying vec2 v_uv;
void main()
{
    v_color = a_color;
    v_uv = a_uv;
    gl_Position = vec4(a_pos * u_xform.xy + u_xform.zw, 0.0, 1.0);
}
