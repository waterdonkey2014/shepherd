using System.Runtime.InteropServices;
namespace Shepherd.Engine.Render;

/// <summary>Minimal SDL3 binding (Homebrew libSDL3 on the Mac; SDL picks Metal).</summary>
public static unsafe partial class Sdl
{
    const string L = "/opt/homebrew/lib/libSDL3.0.dylib";
    public const uint INIT_VIDEO = 0x20, INIT_EVENTS = 0x4000, INIT_GAMEPAD = 0x2000;
    public const ulong WINDOW_HIDDEN = 0x8, WINDOW_RESIZABLE = 0x20, WINDOW_HIGH_PIXEL_DENSITY = 0x2000;
    public const uint PIXELFORMAT_ABGR8888 = 0x16762004;   // R,G,B,A bytes in memory
    public const int TEXTUREACCESS_STATIC = 0, TEXTUREACCESS_STREAMING = 1, TEXTUREACCESS_TARGET = 2;
    public const uint BLENDMODE_NONE = 0, BLENDMODE_BLEND = 1, BLENDMODE_ADD = 2, BLENDMODE_MOD = 4, BLENDMODE_MUL = 8, BLENDMODE_BLEND_PREMULTIPLIED = 0x10, BLENDMODE_ADD_PREMULTIPLIED = 0x20;
    public const uint EVENT_QUIT = 0x100, EVENT_KEY_DOWN = 0x300, EVENT_KEY_UP = 0x301, EVENT_MOUSE_MOTION = 0x400, EVENT_MOUSE_BUTTON_DOWN = 0x401, EVENT_MOUSE_BUTTON_UP = 0x402,
        EVENT_GAMEPAD_AXIS_MOTION = 0x650, EVENT_GAMEPAD_BUTTON_DOWN = 0x651, EVENT_GAMEPAD_BUTTON_UP = 0x652, EVENT_GAMEPAD_ADDED = 0x653, EVENT_GAMEPAD_REMOVED = 0x654;

    [StructLayout(LayoutKind.Sequential)] public struct FPoint { public float x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct FColor { public float r, g, b, a; }
    [StructLayout(LayoutKind.Sequential)] public struct Vertex { public FPoint position; public FColor color; public FPoint tex_coord; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int x, y, w, h; }
    [StructLayout(LayoutKind.Sequential)] public struct FRect { public float x, y, w, h; }
    [StructLayout(LayoutKind.Sequential)] public struct Surface { public uint flags; public uint format; public int w, h, pitch; public IntPtr pixels; public int refcount; public IntPtr reserved; }
    [StructLayout(LayoutKind.Explicit, Size = 128)] public struct Event
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(24)] public uint key_scancode;   // SDL_KeyboardEvent.scancode
        [FieldOffset(28)] public uint key_key;        // SDL_KeyboardEvent.key (keycode)
        [FieldOffset(16)] public uint which;          // joystick id for gamepad events
        [FieldOffset(20)] public byte gbutton;        // SDL_GamepadButtonEvent.button
        [FieldOffset(20)] public byte gaxis;          // SDL_GamepadAxisEvent.axis
        [FieldOffset(24)] public short gvalue;        // SDL_GamepadAxisEvent.value
    }

    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_Init(uint flags);
    [LibraryImport(L)] public static partial void SDL_Quit();
    [LibraryImport(L)] public static partial IntPtr SDL_GetError();
    public static string Error => Marshal.PtrToStringUTF8(SDL_GetError());
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr SDL_CreateWindow(string title, int w, int h, ulong flags);
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr SDL_CreateRenderer(IntPtr window, string name);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_SetRenderVSync(IntPtr r, int vsync);
    [LibraryImport(L)] public static partial IntPtr SDL_CreateTexture(IntPtr r, uint format, int access, int w, int h);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_UpdateTexture(IntPtr tex, Rect* rect, void* pixels, int pitch);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_SetTextureBlendMode(IntPtr tex, uint mode);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_SetTextureScaleMode(IntPtr tex, int mode);
    [LibraryImport(L)] public static partial void SDL_DestroyTexture(IntPtr tex);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_SetRenderTarget(IntPtr r, IntPtr tex);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_SetRenderDrawColorFloat(IntPtr r, float red, float g, float b, float a);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_RenderClear(IntPtr r);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_RenderGeometry(IntPtr r, IntPtr tex, Vertex* v, int nv, int* idx, int ni);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_RenderTexture(IntPtr r, IntPtr tex, FRect* src, FRect* dst);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_SetRenderClipRect(IntPtr r, Rect* rect);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_RenderPresent(IntPtr r);
    [LibraryImport(L)] public static partial Surface* SDL_RenderReadPixels(IntPtr r, Rect* rect);
    [LibraryImport(L)] public static partial Surface* SDL_ConvertSurface(Surface* s, uint format);
    [LibraryImport(L)] public static partial void SDL_DestroySurface(Surface* s);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_PollEvent(Event* e);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_GetWindowSizeInPixels(IntPtr w, int* x, int* y);
    [LibraryImport(L)] public static partial IntPtr SDL_OpenGamepad(uint id);

    // ---- OpenGL context (GlBackend, SHEPHERD_RENDER=gl|compare only) ----
    public const ulong WINDOW_OPENGL = 0x2;
    public const int GL_RED_SIZE = 0, GL_GREEN_SIZE = 1, GL_BLUE_SIZE = 2, GL_ALPHA_SIZE = 3, GL_DOUBLEBUFFER = 5, GL_DEPTH_SIZE = 6, GL_STENCIL_SIZE = 7,
        GL_CONTEXT_MAJOR_VERSION = 17, GL_CONTEXT_MINOR_VERSION = 18, GL_CONTEXT_FLAGS = 19, GL_CONTEXT_PROFILE_MASK = 20;   // SDL3 SDL_GLAttr
    public const int GL_CONTEXT_PROFILE_CORE = 1, GL_CONTEXT_PROFILE_COMPATIBILITY = 2, GL_CONTEXT_PROFILE_ES = 4, GL_CONTEXT_FORWARD_COMPATIBLE_FLAG = 2;
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_GL_SetAttribute(int attr, int value);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_GL_GetAttribute(int attr, int* value);
    [LibraryImport(L)] public static partial void SDL_GL_ResetAttributes();
    [LibraryImport(L)] public static partial IntPtr SDL_GL_CreateContext(IntPtr window);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_GL_MakeCurrent(IntPtr window, IntPtr ctx);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_GL_DestroyContext(IntPtr ctx);
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr SDL_GL_GetProcAddress(string proc);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_GL_SwapWindow(IntPtr window);
    [LibraryImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_GL_SetSwapInterval(int interval);
    [LibraryImport(L)] public static partial void SDL_DestroyWindow(IntPtr window);
    [LibraryImport(L, StringMarshalling = StringMarshalling.Utf8)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool SDL_GL_ExtensionSupported(string extension);
}
