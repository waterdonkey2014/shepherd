// kit #7 spine track, lever L2 (vita/notes/kit7-spine.md): the seam for lazy Spine skins. tools/Rewriter --spine-lazyskin rewrites the owner's
// copy of spine-unity.dll (owner-local; the game's own DLL is never touched): SkeletonBinary.ReadSkin asks Mark whether a non-default skin's
// slot section may be read later; when the engine answers with a token, the section is read once with every attachment dropped (Skip
// brackets that read), and the first access to the skin's attachments reads it again through a stream from Open (Loaded follows). With no
// engine handler (the Mac default, SHEPHERD_SPINE_LAZYSKIN=0, or the kit #6 spine-unity.dll) Mark answers null: every skin is read at load
// exactly as before.
using System;
namespace Shepherd.Hooks
{
    public static class SpineLazy
    {
        /// <summary>The engine's handlers (Shepherd.Engine Runtime/SpineLazySkins.cs); null = no lazy skins.</summary>
        public static Func<int, object> OnMark;
        public static Func<object, object> OnOpen;
        public static Action<object, bool> OnSkip;
        public static Action<object, object> OnLoaded;
        /// <summary>The one lock every lazy load takes (a load can need another skin's parent mesh: one lock, re-entered, never two).</summary>
        static readonly object _lock = new object();
        public static object Lock => _lock;

        /// <summary>ReadSkin, after a non-default skin's header: a token (the slot section may be read later) or null (read it now, as before).
        /// count = the skin's slot entries.</summary>
        public static object Mark(int count) { var f = OnMark; return f == null ? null : f(count); }
        /// <summary>The slot section's first read (every attachment dropped): begin = true before, false after (also after an exception).</summary>
        public static void Skip(object token, bool begin) { OnSkip?.Invoke(token, begin); }
        /// <summary>A System.IO.Stream positioned at the token's slot section (the same bytes the first read saw).</summary>
        public static object Open(object token) { var f = OnOpen; return f?.Invoke(token); }
        /// <summary>The skin was read (stream = what Open returned; the engine closes it).</summary>
        public static void Loaded(object token, object stream) { OnLoaded?.Invoke(token, stream); }
    }
}
